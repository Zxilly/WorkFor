using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using WorkFor.Host;
using Xunit;

namespace WorkFor.Validation.Tests;

/// <summary>Runs the actual NativeSession against an owned BCL-only bridge process.</summary>
public sealed class NativeSessionTests
{
    [Fact]
    public async Task ConfirmsInitialAndChangedWidthAndRevisionOverTheRealChildStdin()
    {
        using var fixture = new BridgeFixture("normal");
        await using var session = new NativeSession(fixture.Executable, fixture.StateDirectory, 220, 5);
        session.Start();
        await UntilAsync(() => session.Snapshot()["configRevision"]?.GetValue<long>() == 5);
        Assert.Equal(220, session.Snapshot()["widthDip"]!.GetValue<int>());

        session.Configure(320, 6);
        await UntilAsync(() => session.Snapshot()["configRevision"]?.GetValue<long>() == 6);
        Assert.Equal(320, session.Snapshot()["widthDip"]!.GetValue<int>());
        var commands = fixture.Events().Where(x => x["event"]?.GetValue<string>() == "command")
            .Select(x => x["message"]!.AsObject()).ToArray();
        Assert.Contains(commands, x => x["payload"]!["widthDip"]!.GetValue<int>() == 220 &&
            x["payload"]!["configRevision"]!.GetValue<long>() == 5);
        Assert.Contains(commands, x => x["payload"]!["widthDip"]!.GetValue<int>() == 320 &&
            x["payload"]!["configRevision"]!.GetValue<long>() == 6);
        Assert.Equal(commands.Length, commands.Select(x => x["requestId"]!.GetValue<string>()).Distinct().Count());
        Assert.Throws<ArgumentOutOfRangeException>(() => session.Configure(179, 7));
    }

    [Fact]
    public async Task ClosingTheSessionDeliversEofAndKeepsTheConfirmedDormantResult()
    {
        using var fixture = new BridgeFixture("normal");
        await using var session = new NativeSession(fixture.Executable, fixture.StateDirectory);
        session.Start();
        await UntilAsync(() => session.Snapshot()["state"]?.GetValue<string>() == "Attached");
        await session.DisposeAsync();
        Assert.Contains(fixture.Events(), x => x["event"]?.GetValue<string>() == "stdin.eof");
        Assert.Equal("Dormant", session.Snapshot()["state"]!.GetValue<string>());
        Assert.False(session.Snapshot()["layoutAttached"]!.GetValue<bool>());
    }

    [Fact]
    public async Task StaleRevisionsCannotRollBackAnAlreadyConfirmedWidth()
    {
        using var fixture = new BridgeFixture("normal");
        await using var session = new NativeSession(fixture.Executable, fixture.StateDirectory, 220, 5);
        session.Start();
        await UntilAsync(() => session.Snapshot()["configRevision"]?.GetValue<long>() == 5);
        session.Configure(320, 6);
        await UntilAsync(() => session.Snapshot()["configRevision"]?.GetValue<long>() == 6);
        var count = fixture.Events().Count(x => x["event"]?.GetValue<string>() == "command");

        session.Configure(180, 5);
        session.Configure(320, 6);
        Assert.Throws<ArgumentException>(() => session.Configure(260, 6));
        await Task.Delay(150, TestContext.Current.CancellationToken);
        Assert.Equal(count, fixture.Events().Count(x => x["event"]?.GetValue<string>() == "command"));
        Assert.Equal(6, session.Snapshot()["configRevision"]!.GetValue<long>());
        Assert.Equal(320, session.Snapshot()["widthDip"]!.GetValue<int>());

        session.Configure(360, 7);
        await UntilAsync(() => session.Snapshot()["configRevision"]?.GetValue<long>() == 7);
        Assert.Equal(360, session.Snapshot()["widthDip"]!.GetValue<int>());
    }

    [Fact]
    public async Task UnsupportedBuildDoesNotEnterTheAutomaticRetryLoop()
    {
        using var fixture = new BridgeFixture("unsupported");
        await using var session = new NativeSession(fixture.Executable, fixture.StateDirectory);
        session.Start();
        await UntilAsync(() => session.Snapshot()["state"]?.GetValue<string>() == "Unsupported");
        await Task.Delay(TimeSpan.FromSeconds(2.2), TestContext.Current.CancellationToken);
        Assert.Single(fixture.Starts());
        Assert.Equal("UNSUPPORTED_BUILD", session.Snapshot()["code"]!.GetValue<string>());
        Assert.Equal(0, session.Snapshot()["recovery"]!["failureCount"]!.GetValue<int>());
    }

    [Fact]
    public async Task ExplicitRetryIsRequiredToReattemptAnUnsupportedBuild()
    {
        using var fixture = new BridgeFixture("unsupported");
        await using var session = new NativeSession(fixture.Executable, fixture.StateDirectory);
        session.Start();
        await UntilAsync(() => session.Snapshot()["state"]?.GetValue<string>() == "Unsupported");
        session.Retry();
        await UntilAsync(() => fixture.Starts().Length == 2);
        await UntilAsync(() => session.Snapshot()["state"]?.GetValue<string>() == "Unsupported");
        Assert.Equal(2, fixture.Starts().Length);
    }

    [Fact]
    public async Task ABridgeExitingAfterAttachedCannotLeaveTheHostStateAttached()
    {
        using var fixture = new BridgeFixture("exit-attached");
        await using var session = new NativeSession(fixture.Executable, fixture.StateDirectory);
        session.Start();
        await UntilAsync(() => session.Snapshot()["state"]?.GetValue<string>() == "Reconnecting");
        Assert.False(session.Snapshot()["layoutAttached"]!.GetValue<bool>());
        Assert.Equal("BRIDGE_EXITED", session.Snapshot()["code"]!.GetValue<string>());
    }

    [Fact]
    public async Task ThreeRelatedProcessExitsQuarantineAndPersistAcrossHostRestarts()
    {
        using var fixture = new BridgeFixture("exit-attached");
        await using (var first = new NativeSession(fixture.Executable, fixture.StateDirectory))
        {
            first.Start();
            await UntilAsync(() => first.Snapshot()["state"]?.GetValue<string>() == "Quarantined", TimeSpan.FromSeconds(15));
            Assert.Equal(3, fixture.Starts().Length);
            Assert.Equal(3, first.Snapshot()["recovery"]!["failureCount"]!.GetValue<int>());
        }
        await using var restarted = new NativeSession(fixture.Executable, fixture.StateDirectory);
        restarted.Start();
        await UntilAsync(() => restarted.Snapshot()["state"]?.GetValue<string>() == "Quarantined");
        await Task.Delay(300, TestContext.Current.CancellationToken);
        Assert.Equal(3, fixture.Starts().Length);
        Assert.True(restarted.Snapshot()["recovery"]!["quarantined"]!.GetValue<bool>());
    }

    [Theory]
    [InlineData("malformed")]
    [InlineData("oversized")]
    public async Task MalformedOrOversizedChildOutputIsBoundedAndUsesTheRecoveryBudget(string mode)
    {
        using var fixture = new BridgeFixture(mode);
        await using var session = new NativeSession(fixture.Executable, fixture.StateDirectory);
        session.Start();
        await UntilAsync(() => session.Snapshot()["state"]?.GetValue<string>() == "Quarantined", TimeSpan.FromSeconds(15));
        Assert.Equal(3, fixture.Starts().Length);
        Assert.Equal("BRIDGE_PROTOCOL_ERROR", session.Snapshot()["recovery"]!["lastCode"]!.GetValue<string>());
        Assert.False(session.Snapshot()["layoutAttached"]!.GetValue<bool>());
    }

    [Fact]
    public async Task AProtocolFaultPeerIgnoringEofIsKilledWithinTheShutdownDeadline()
    {
        using var fixture = new BridgeFixture("malformed-stubborn");
        await using var session = new NativeSession(fixture.Executable, fixture.StateDirectory);
        session.Start();
        await UntilAsync(() => session.Snapshot()["code"]?.GetValue<string>() == "BRIDGE_PROTOCOL_ERROR");
        var elapsed = Stopwatch.StartNew();
        await session.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(14), TestContext.Current.CancellationToken);
        Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(14));
        Assert.All(fixture.Starts(), start => Assert.False(fixture.IsOwnedProcessAlive(start)));
        Assert.NotEqual("Attached", session.Snapshot()["state"]!.GetValue<string>());
        Assert.Equal("DetachPending", session.Snapshot()["state"]!.GetValue<string>());
    }

    [Fact]
    public async Task WellFormedJsonWithInvalidStateTypesCannotEscapeIntoTheHostSnapshot()
    {
        using var fixture = new BridgeFixture("invalid-state");
        await using var session = new NativeSession(fixture.Executable, fixture.StateDirectory);
        session.Start();
        await UntilAsync(() => fixture.Events().Any(x => x["event"]?.GetValue<string>() == "invalid-state-emitted"));
        await Task.Delay(150, TestContext.Current.CancellationToken);
        var snapshot = session.Snapshot();
        Assert.True(snapshot["state"] is JsonValue state && state.TryGetValue<string>(out _),
            "Malformed child state reached the public Host snapshot.");
        Assert.True(snapshot["layoutAttached"] is JsonValue attached && attached.TryGetValue<bool>(out _));
        await UntilAsync(() => session.Snapshot()["recovery"]?["lastCode"]?.GetValue<string>() == "BRIDGE_PROTOCOL_ERROR");
    }

    private static async Task UntilAsync(Func<bool> condition, TimeSpan? timeout = null)
    {
        var deadline = Stopwatch.StartNew();
        var limit = timeout ?? TimeSpan.FromSeconds(6);
        while (deadline.Elapsed < limit)
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            if (condition()) return;
            await Task.Delay(25, TestContext.Current.CancellationToken);
        }
        Assert.Fail("Owned bridge did not reach the expected protocol state within the deadline.");
    }

    private sealed class BridgeFixture : IDisposable
    {
        private readonly string _parent = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "WorkFor.NativeSession.Tests"));
        private readonly string _root;
        private readonly string _log;
        public string Executable { get; }
        public string StateDirectory { get; }
        public BridgeFixture(string mode)
        {
            _root = Path.Combine(_parent, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
            DirectoryInfo? current = new(AppContext.BaseDirectory);
            while (current is not null && !File.Exists(Path.Combine(current.FullName, "WorkFor.slnx"))) current = current.Parent;
            if (current is null) throw new InvalidOperationException("Cannot locate the test repository root.");
            var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
            var binaries = Path.Combine(current.FullName, "tests", "WorkFor.NativeFixture", "bin", configuration, "net10.0");
            Assert.True(File.Exists(Path.Combine(binaries, "WorkFor.NativeBridge.exe")), "Build WorkFor.NativeFixture before these tests.");
            foreach (var file in Directory.EnumerateFiles(binaries, "WorkFor.NativeBridge.*"))
                File.Copy(file, Path.Combine(_root, Path.GetFileName(file)));
            File.WriteAllText(Path.Combine(_root, "WorkFor.Taskbar.Live.dll"),
                new JsonObject { ["fixture"] = true, ["mode"] = mode }.ToJsonString(), new UTF8Encoding(false));
            Executable = Path.Combine(_root, "WorkFor.NativeBridge.exe");
            StateDirectory = Path.Combine(_root, "host-data");
            _log = Path.Combine(_root, "fixture-events.jsonl");
        }
        public JsonObject[] Events()
        {
            if (!File.Exists(_log)) return [];
            using var stream = new FileStream(_log, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            var values = new List<JsonObject>();
            while (reader.ReadLine() is { } line)
            {
                try { if (JsonNode.Parse(line) is JsonObject item) values.Add(item); }
                catch (JsonException) { }
            }
            return values.ToArray();
        }
        public JsonObject[] Starts() => Events().Where(x => x["event"]?.GetValue<string>() == "start").ToArray();
        public bool IsOwnedProcessAlive(JsonObject identity)
        {
            try
            {
                using var process = Process.GetProcessById(identity["processId"]!.GetValue<int>());
                return IsOwnedProcess(process, identity) && !process.HasExited;
            }
            catch (Exception error) when (error is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
            { return false; }
        }
        private bool IsOwnedProcess(Process process, JsonObject identity) =>
            !process.HasExited && process.StartTime.ToUniversalTime().Ticks == identity["creationTimeTicks"]!.GetValue<long>() &&
            string.Equals(process.MainModule?.FileName, Executable, StringComparison.OrdinalIgnoreCase);
        public void Dispose()
        {
            var path = Path.GetFullPath(_root);
            if (!path.StartsWith(_parent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Refusing fixture cleanup outside the test directory.");
            foreach (var start in Starts())
            {
                try
                {
                    using var process = Process.GetProcessById(start["processId"]!.GetValue<int>());
                    if (IsOwnedProcess(process, start)) { process.Kill(entireProcessTree: true); process.WaitForExit(5000); }
                }
                catch (Exception error) when (error is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { }
            }
            Directory.Delete(path, recursive: true);
        }
    }
}
