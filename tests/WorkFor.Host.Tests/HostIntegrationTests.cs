using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.IO.Pipes;
using System.Security.Principal;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using WorkFor.Contracts;
using Microsoft.Win32.SafeHandles;
using Xunit;

[assembly: Xunit.v3.Parallelization(Mode = Xunit.Sdk.ParallelMode.None)]

namespace WorkFor.Host.Tests;

/// <summary>Cross-process tests over the published pipe contract. They never touch Explorer.</summary>
public sealed class HostIntegrationTests
{
    private const string LiveRule = """
        export default function calculate(ctx) {
          return {apiVersion:1,currency:ctx.currency.code,amountMinor:String(ctx.now.epochMs)};
        }
        """;

    [Fact]
    public async Task PublishesPeriodicSnapshotsAndActivatesRulesAtomically()
    {
        await using var host = await RunningHost.StartAsync();
        var initial = await host.RequestAsync("config.get", cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(1, initial["revision"]!.GetValue<long>());
        Assert.False(initial["startWithWindows"]!.GetValue<bool>());

        // Candidate preview must not mutate the running configuration.
        var patch = new JsonObject { ["expectedRevision"] = 1, ["source"] = LiveRule };
        var preview = await host.RequestAsync("rule.preview", patch, TestContext.Current.CancellationToken);
        Assert.NotNull(preview);
        Assert.Equal(1, (await host.RequestAsync("config.get", cancellationToken: TestContext.Current.CancellationToken))["revision"]!.GetValue<long>());

        var activated = await host.RequestAsync("config.activate", patch, TestContext.Current.CancellationToken);
        Assert.Equal(2, activated["revision"]!.GetValue<long>());
        Assert.Equal(LiveRule, activated["rule"]!["source"]!.GetValue<string>());

        await using var display = await host.SubscribeAsync();
        var first = await ReadFreshSnapshotAsync(display, 2);
        var second = await ReadFreshSnapshotAsync(display, 2, first["sequence"]!.GetValue<long>());
        Assert.Equal(first["generation"]!.GetValue<string>(), second["generation"]!.GetValue<string>());
        Assert.True(second["evaluatedAtMs"]!.GetValue<long>() > first["evaluatedAtMs"]!.GetValue<long>());
        Assert.NotEqual(first["amountText"]!.GetValue<string>(), second["amountText"]!.GetValue<string>());
        Assert.Equal("fresh", second["status"]!.GetValue<string>());

        // A stale revision must not overwrite a rule activated by another editor.
        var conflict = await host.RequestEnvelopeAsync("config.activate", patch, TestContext.Current.CancellationToken);
        Assert.EndsWith(".error", conflict["type"]!.GetValue<string>());
        Assert.Equal("CONFIG_CONFLICT", conflict["payload"]!["code"]!.GetValue<string>());
        Assert.Equal(2, (await host.RequestAsync("config.get", cancellationToken: TestContext.Current.CancellationToken))["revision"]!.GetValue<long>());

        // Invalid candidates leave both persistent configuration and live snapshots unchanged.
        var invalid = await host.RequestEnvelopeAsync("config.activate", new JsonObject
        {
            ["expectedRevision"] = 2,
            ["source"] = "export default function ( {"
        }, TestContext.Current.CancellationToken);
        Assert.EndsWith(".error", invalid["type"]!.GetValue<string>());
        var after = await host.RequestAsync("config.get", cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(2, after["revision"]!.GetValue<long>());
        Assert.Equal(LiveRule, after["rule"]!["source"]!.GetValue<string>());

        // Settings written by activation survive a real host/process restart.
        await display.DisposeAsync();
        await host.RestartAsync();
        var restored = await host.RequestAsync("config.get", cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(2, restored["revision"]!.GetValue<long>());
        Assert.Equal(LiveRule, restored["rule"]!["source"]!.GetValue<string>());
        await using var reconnected = await host.SubscribeAsync();
        var newSnapshot = await ReadFreshSnapshotAsync(reconnected, 2);
        Assert.NotEqual(first["generation"]!.GetValue<string>(), newSnapshot["generation"]!.GetValue<string>());
    }

    [Fact]
    public async Task MalformedAndAbandonedClientsDoNotStopOtherClients()
    {
        await using var host = await RunningHost.StartAsync();
        // A frame larger than the advertised limit is rejected before allocating its payload.
        await using (var oversized = await host.ConnectAsync("control", TestContext.Current.CancellationToken))
        {
            var header = new byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(header, FramedJson.ControlLimit + 1u);
            await oversized.WriteAsync(header, TestContext.Current.CancellationToken);
            await oversized.FlushAsync(TestContext.Current.CancellationToken);
        }
        Assert.Equal(1, (await host.RequestAsync("config.get", cancellationToken: TestContext.Current.CancellationToken))["revision"]!.GetValue<long>());

        // Duplicate fields are not allowed to select an arbitrary parser-dependent command.
        await using (var duplicate = await host.ConnectAsync("control", TestContext.Current.CancellationToken))
        {
            var text = """{"protocolVersion":1,"type":"config.get","type":"config.activate","requestId":"bad","payload":{}}""";
            var bytes = Encoding.UTF8.GetBytes(text);
            var header = new byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(header, (uint)bytes.Length);
            await duplicate.WriteAsync(header, TestContext.Current.CancellationToken);
            await duplicate.WriteAsync(bytes, TestContext.Current.CancellationToken);
            await duplicate.FlushAsync(TestContext.Current.CancellationToken);
        }

        // Abandoning a subscribed display must free its capacity; repeat beyond subscriber limit.
        for (var index = 0; index < 16; index++)
        {
            await using var abandoned = await host.SubscribeAsync();
            await ReadSnapshotAsync(abandoned);
        }
        await using var healthy = await host.SubscribeAsync();
        var snapshot = await ReadSnapshotAsync(healthy);
        Assert.Equal("display.snapshot", snapshot["type"]!.GetValue<string>());
        Assert.Equal(1, (await host.RequestAsync("config.get", cancellationToken: TestContext.Current.CancellationToken))["revision"]!.GetValue<long>());
        Assert.False(host.HasExited);
    }

    [Fact]
    public async Task NonterminatingCandidateIsBoundedAndDoesNotReplaceActiveRule()
    {
        await using var host = await RunningHost.StartAsync();
        var before = await host.RequestAsync("config.get", cancellationToken: TestContext.Current.CancellationToken);
        var timer = Stopwatch.StartNew();
        var response = await host.RequestEnvelopeAsync("config.activate", new JsonObject
        {
            ["expectedRevision"] = 1,
            ["source"] = "export default function calculate(ctx) { while (true) {} }"
        }, TestContext.Current.CancellationToken);
        Assert.EndsWith(".error", response["type"]!.GetValue<string>());
        Assert.True(timer.Elapsed < TimeSpan.FromSeconds(15), "Candidate execution exceeded its external deadline.");
        var after = await host.RequestAsync("config.get", cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(before["revision"]!.GetValue<long>(), after["revision"]!.GetValue<long>());
        Assert.Equal(before["rule"]!["contentHash"]!.GetValue<string>(), after["rule"]!["contentHash"]!.GetValue<string>());
        await using var display = await host.SubscribeAsync();
        Assert.Equal("fresh", (await ReadFreshSnapshotAsync(display, 1))["status"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("{\"calendar\":{\"timeZone\":\"Not/A/RealZone\"}}")]
    [InlineData("{\"calendar\":{\"shifts\":[null]}}")]
    [InlineData("{\"display\":{\"currency\":\"NOPE\"}}")]
    [InlineData("{\"source\":{}}")]
    [InlineData("{\"parameters\":\"not-an-object\"}")]
    [InlineData("{\"display\":[]}")]
    public async Task InvalidConfigurationReceivesStructuredErrorAndLeavesRevisionUnchanged(string candidate)
    {
        await using var host = await RunningHost.StartAsync();
        var patch = JsonNode.Parse(candidate)!.AsObject();
        patch["expectedRevision"] = 1;
        var response = await host.RequestEnvelopeAsync("config.activate", patch, TestContext.Current.CancellationToken);
        Assert.EndsWith(".error", response["type"]!.GetValue<string>());
        Assert.False(string.IsNullOrWhiteSpace(response["payload"]!["code"]!.GetValue<string>()));
        Assert.Equal(1, (await host.RequestAsync("config.get", cancellationToken: TestContext.Current.CancellationToken))["revision"]!.GetValue<long>());
        Assert.False(host.HasExited);
    }

    [Fact]
    public async Task ReachingPipeInstanceLimitDoesNotPermanentlyStopListener()
    {
        await using var host = await RunningHost.StartAsync();
        var abandoned = new List<NamedPipeClientStream>();
        try
        {
            // None sends a handshake; each occupies one native pipe server instance.
            for (var index = 0; index < 16; index++)
            {
                try { abandoned.Add(await host.ConnectAsync("control", TestContext.Current.CancellationToken)); }
                catch (TimeoutException) { break; } // Bounded admission may intentionally stop accepting sooner.
            }
        }
        finally
        {
            foreach (var pipe in abandoned) await pipe.DisposeAsync();
        }
        Assert.NotEmpty(abandoned);
        // After clients disconnect the accept loop must still exist, not be a faulted Task.
        var config = await host.RequestAsync("config.get", cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(1, config["revision"]!.GetValue<long>());
        Assert.False(host.HasExited);
    }

    private static async Task<JsonObject> ReadFreshSnapshotAsync(Stream stream, long revision, long afterSequence = -1)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (true)
        {
            var message = await FramedJson.ReadAsync(stream, FramedJson.DisplayLimit, timeout.Token);
            Assert.Equal("display.snapshot", message["type"]!.GetValue<string>());
            var payload = message["payload"]!.AsObject();
            if (payload["configRevision"]!.GetValue<long>() == revision
                && payload["sequence"]!.GetValue<long>() > afterSequence
                && payload["status"]!.GetValue<string>() == "fresh") return payload;
        }
    }

    private static async Task<JsonObject> ReadSnapshotAsync(Stream stream)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        return await FramedJson.ReadAsync(stream, FramedJson.DisplayLimit, timeout.Token);
    }
}

internal sealed class RunningHost : IAsyncDisposable
{
    private readonly string _root;
    private readonly string _configuration;
    private readonly string _data;
    private readonly StringBuilder _diagnostics = new();
    private Process? _process;
    private Task? _stdout;
    private Task? _stderr;
    private long _request;

    private RunningHost(string root, string configuration)
    {
        _root = root;
        _configuration = configuration;
        _data = Path.Combine(root, ".local", "host-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_data);
    }

    public bool HasExited => _process is null || _process.HasExited;

    public static async Task<RunningHost> StartAsync()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Directory.Packages.props"))) directory = directory.Parent;
        var root = directory?.FullName ?? throw new InvalidOperationException("Cannot locate repository root.");
        var configuration = AppContext.BaseDirectory.Split(Path.DirectorySeparatorChar).Contains("Release") ? "Release" : "Debug";
        var host = new RunningHost(root, configuration);
        try { await host.StartProcessAsync(); return host; }
        catch { await host.DisposeAsync(); throw; }
    }

    public async Task RestartAsync()
    {
        await StopProcessAsync();
        await StartProcessAsync();
    }

    private async Task StartProcessAsync()
    {
        var dotnetDirectory = Path.Combine(_root, ".tools", "dotnet");
        var dotnet = Path.Combine(dotnetDirectory, "dotnet.exe");
        var host = Path.Combine(_root, "src", "WorkFor.Host", "bin", _configuration, "net10.0-windows", "WorkFor.Host.dll");
        var scriptHost = Path.Combine(_root, "src", "WorkFor.ScriptHost", "bin", _configuration, "net10.0", "WorkFor.ScriptHost.exe");
        if (!File.Exists(dotnet) || !File.Exists(host) || !File.Exists(scriptHost))
            throw new InvalidOperationException("Build Host and ScriptHost with scripts/bootstrap-dotnet.ps1 before running process tests.");
        var start = new ProcessStartInfo(dotnet)
        {
            WorkingDirectory = _root,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        start.ArgumentList.Add(host);
        start.ArgumentList.Add("--data-dir"); start.ArgumentList.Add(_data);
        start.ArgumentList.Add("--script-host"); start.ArgumentList.Add(scriptHost);
        start.Environment["DOTNET_ROOT"] = dotnetDirectory;
        _process = Process.Start(start) ?? throw new InvalidOperationException("Cannot start Host.");
        _stdout = DrainAsync(_process.StandardOutput);
        _stderr = DrainAsync(_process.StandardError);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while (!timeout.IsCancellationRequested)
        {
            if (_process.HasExited) throw new InvalidOperationException($"Host exited {_process.ExitCode}: {_diagnostics}");
            try { await RequestAsync("config.get", cancellationToken: timeout.Token); return; }
            catch (Exception error) when (error is IOException or TimeoutException or OperationCanceledException)
            { await Task.Delay(50, timeout.Token); }
        }
        throw new TimeoutException($"Host did not start: {_diagnostics}");
    }

    public async Task<NamedPipeClientStream> ConnectAsync(string channel, CancellationToken cancellationToken = default)
    {
        var sid = WindowsIdentity.GetCurrent().User!.Value;
        var pipe = new NamedPipeClientStream(".", $"workfor-{channel}-v1-{sid}-{Process.GetCurrentProcess().SessionId}",
            PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        try
        {
            await pipe.ConnectAsync(1000, cancellationToken);
            if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle, out var server)
                || _process is null || server != (uint)_process.Id)
                throw new InvalidOperationException("管道不属于本测试启动的 Host；拒绝修改同会话中的其他实例。");
            return pipe;
        }
        catch { pipe.Dispose(); throw; }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint processId);

    public async Task<NamedPipeClientStream> SubscribeAsync()
    {
        var pipe = await ConnectAsync("display");
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await FramedJson.WriteAsync(pipe, FramedJson.Message("display.subscribe", "test-display", new JsonObject()),
                FramedJson.DisplayLimit, timeout.Token);
            return pipe;
        }
        catch { pipe.Dispose(); throw; }
    }

    public async Task<JsonObject> RequestAsync(string type, JsonObject? payload = null, CancellationToken cancellationToken = default)
    {
        var response = await RequestEnvelopeAsync(type, payload, cancellationToken);
        if (response["type"]!.GetValue<string>().EndsWith(".error", StringComparison.Ordinal))
            throw new InvalidOperationException(response.ToJsonString());
        return response["payload"]!.AsObject();
    }

    public async Task<JsonObject> RequestEnvelopeAsync(string type, JsonObject? payload = null, CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        await using var pipe = await ConnectAsync("control", timeout.Token);
        var id = Interlocked.Increment(ref _request).ToString(CultureInfo.InvariantCulture);
        await FramedJson.WriteAsync(pipe, FramedJson.Message(type, id, payload?.DeepClone() ?? new JsonObject()),
            FramedJson.ControlLimit, timeout.Token);
        var response = await FramedJson.ReadAsync(pipe, FramedJson.ControlLimit, timeout.Token);
        Assert.Equal(1, response["protocolVersion"]!.GetValue<int>());
        Assert.Equal(id, response["requestId"]!.GetValue<string>());
        return response;
    }

    private async Task DrainAsync(StreamReader reader)
    {
        var buffer = new char[1024];
        try
        {
            int count;
            while ((count = await reader.ReadAsync(buffer)) > 0)
                lock (_diagnostics) if (_diagnostics.Length < 32768) _diagnostics.Append(buffer, 0, count);
        }
        catch (IOException) { }
        catch (ObjectDisposedException) { }
    }

    private async Task StopProcessAsync()
    {
        if (_process is null) return;
        try
        {
            if (!_process.HasExited) _process.Kill(entireProcessTree: true);
            await _process.WaitForExitAsync();
            if (_stdout is not null) await _stdout;
            if (_stderr is not null) await _stderr;
        }
        finally { _process.Dispose(); _process = null; }
    }

    public async ValueTask DisposeAsync()
    {
        await StopProcessAsync();
        // Every test path is a literal GUID child of repository .local/host-tests.
        if (Directory.Exists(_data)) Directory.Delete(_data, recursive: true);
    }
}
