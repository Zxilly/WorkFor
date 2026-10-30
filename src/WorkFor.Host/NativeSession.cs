using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using WorkFor.Contracts;

namespace WorkFor.Host;

/// <summary>Owns the bridge, its bounded commands, recovery budget and confirmed detach.</summary>
internal sealed class NativeSession : IAsyncDisposable
{
    private sealed record DesiredConfiguration(int WidthDip, long Revision);
    private static readonly HashSet<string> PermanentCodes = new(StringComparer.Ordinal)
    {
        "UNSUPPORTED_BUILD", "TARGET_IDENTITY_MISMATCH", "LOAD_POLICY_BLOCKED",
        "NATIVE_ABI_MISMATCH", "NATIVE_BINARY_MISMATCH", "LAYOUT_ATTACH_FAILED",
        "UI_OPERATION_PENDING", "DETACH_PENDING", "CONTROL_CHANNEL_LOST"
    };
    private readonly string? _executable;
    private readonly string _directory;
    private readonly object _gate = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly Channel<DesiredConfiguration> _updates = Channel.CreateBounded<DesiredConfiguration>(new BoundedChannelOptions(1)
        { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
    private readonly Channel<bool> _retry = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
        { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
    private JsonObject _state = new() { ["state"] = "NotAttached", ["layoutAttached"] = false };
    private DesiredConfiguration _desired;
    private NativeRecoveryBudget? _budget;
    private Task? _supervisor;
    private int _disposed;
    private long _request;

    public NativeSession(string? executable, string? stateDirectory = null, int initialWidthDip = 260, long initialRevision = 1)
    {
        _executable = executable;
        _directory = stateDirectory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WorkFor");
        _desired = new(initialWidthDip, initialRevision);
    }

    public JsonObject Snapshot()
    {
        JsonObject value;
        lock (_gate) value = (JsonObject)_state.DeepClone();
        if (_budget is not null) value["recovery"] = _budget.Describe();
        return value;
    }

    public void Configure(int widthDip, long revision)
    {
        if (widthDip is < 180 or > 360 || revision < 1) throw new ArgumentOutOfRangeException(nameof(widthDip));
        lock (_gate)
        {
            if (revision < _desired.Revision) return;
            if (revision == _desired.Revision && widthDip != _desired.WidthDip)
                throw new ArgumentException("A configuration revision cannot identify different native widths.");
            if (_desired.WidthDip == widthDip && _desired.Revision == revision) return;
            _desired = new(widthDip, revision);
            _updates.Writer.TryWrite(_desired);
        }
    }

    public void Retry()
    {
        if (_executable is null) throw new ConfigException("NATIVE_NOT_CONFIGURED", "当前运行方式没有配置任务栏组件。");
        try { _budget?.Reset(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { throw new ConfigException("NATIVE_RECOVERY_IO", "无法保存原生组件重试状态。"); }
        _retry.Writer.TryWrite(true);
        Start();
    }

    public void Start()
    {
        lock (_gate)
        {
            if (_executable is null || _supervisor is { IsCompleted: false } || _stop.IsCancellationRequested) return;
            _supervisor = Task.Run(SuperviseAsync);
        }
    }

    private async Task SuperviseAsync()
    {
        try
        {
            var path = Path.GetFullPath(_executable!);
            if (!File.Exists(path) || !string.Equals(Path.GetFileName(path), "WorkFor.NativeBridge.exe", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Native bridge is not installed.");
            var dll = Path.Combine(Path.GetDirectoryName(path)!, "WorkFor.Taskbar.Live.dll");
            if (!File.Exists(dll)) throw new InvalidDataException("Native taskbar DLL is not installed.");
            var fingerprint = Hash(path) + Hash(dll);
            _budget = new(_directory, fingerprint);
            var failures = 0;
            while (!_stop.IsCancellationRequested)
            {
                if (_budget.IsQuarantined)
                {
                    SetState("Quarantined", false, "RETRY_BUDGET_EXCEEDED");
                    await _retry.Reader.ReadAsync(_stop.Token);
                }
                var outcome = await RunOnceAsync(path, _stop.Token);
                if (_stop.IsCancellationRequested) break;
                if (outcome.Code is "UNSUPPORTED_BUILD" or "TARGET_IDENTITY_MISMATCH" or "LOAD_POLICY_BLOCKED"
                    or "NATIVE_ABI_MISMATCH" or "NATIVE_BINARY_MISMATCH")
                {
                    SetState("Unsupported", false, outcome.Code);
                    await _retry.Reader.ReadAsync(_stop.Token);
                    failures = 0;
                    continue;
                }
                try { _budget.RecordFailure(outcome.Code); }
                catch (IOException)
                {
                    SetState("Quarantined", outcome.LayoutAttached, "RECOVERY_STATE_WRITE_FAILED");
                    await _retry.Reader.ReadAsync(_stop.Token);
                    continue;
                }
                if (_budget.IsQuarantined) continue;
                if (PermanentCodes.Contains(outcome.Code))
                {
                    SetState(outcome.LayoutAttached ? "DetachPending" : "NativeError", outcome.LayoutAttached, outcome.Code);
                    await _retry.Reader.ReadAsync(_stop.Token);
                    failures = 0;
                    continue;
                }
                SetState("Reconnecting", false, outcome.Code);
                await Task.Delay(TimeSpan.FromSeconds(Math.Min(30, 1 << Math.Min(failures++, 5))), _stop.Token);
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch (Exception error) when (error is IOException or InvalidDataException or ArgumentException
            or InvalidOperationException or System.ComponentModel.Win32Exception or UnauthorizedAccessException)
        { SetState("Unsupported", false, "NATIVE_START_FAILED"); }
    }

    private sealed record Outcome(string Code, bool LayoutAttached);

    private async Task<Outcome> RunOnceAsync(string path, CancellationToken cancellationToken)
    {
        DesiredConfiguration desired;
        lock (_gate) desired = _desired;
        var info = new ProcessStartInfo(path)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        info.ArgumentList.Add("--run-explorer");
        info.ArgumentList.Add("--width");
        info.ArgumentList.Add(desired.WidthDip.ToString(System.Globalization.CultureInfo.InvariantCulture));
        using var process = Process.Start(info) ?? throw new InvalidOperationException("Cannot start bridge.");
        SetState("Connecting", false, "CONNECTING");
        using var session = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var writer = WriteCommandsAsync(process, desired, session.Token);
        // Keep reading until process EOF during shutdown so the confirmed Dormant
        // result is not discarded when the writer's cancellation token is set.
        var reader = ObserveAsync(process, CancellationToken.None);
        var drain = DrainAsync(process.StandardError);
        var exiting = process.WaitForExitAsync(cancellationToken);
        var unconfirmedShutdown = false;
        try
        {
            var completed = await Task.WhenAny(reader, writer, exiting);
            // Await a faulted operation so malformed output cannot be silently ignored.
            if (completed == reader) await reader;
            if (completed == writer) await writer;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (OperationCanceledException)
        { SetState("NativeError", Snapshot()["layoutAttached"]?.GetValue<bool>() ?? false, "CONTROL_CHANNEL_LOST"); }
        catch (Exception error) when (error is IOException or InvalidDataException or System.Text.Json.JsonException or InvalidOperationException)
        { SetState("NativeError", Snapshot()["layoutAttached"]?.GetValue<bool>() ?? false, "BRIDGE_PROTOCOL_ERROR"); }
        finally
        {
            await session.CancelAsync();
            try { await writer; } catch (Exception e) when (e is OperationCanceledException or IOException or InvalidOperationException or ObjectDisposedException) { }
            // EOF remains a shutdown signal even when the command reader failed.
            try { process.StandardInput.Close(); } catch (InvalidOperationException) { }
            if (!process.HasExited)
            {
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                try { await process.WaitForExitAsync(deadline.Token); }
                catch (OperationCanceledException)
                {
                    unconfirmedShutdown = true;
                    SetState("DetachPending", Snapshot()["layoutAttached"]?.GetValue<bool>() ?? true, "DETACH_PENDING");
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync();
                }
            }
            try { await reader; } catch (Exception e) when (e is OperationCanceledException or IOException or InvalidDataException
                or System.Text.Json.JsonException or InvalidOperationException or ObjectDisposedException) { }
            await drain;
        }
        var last = Snapshot();
        var attached = last["layoutAttached"]?.GetValue<bool>() ?? false;
        var code = last["code"]?.GetValue<string>();
        if (unconfirmedShutdown)
        {
            SetState("DetachPending", attached, "DETACH_PENDING");
            return new("DETACH_PENDING", attached);
        }
        if (cancellationToken.IsCancellationRequested)
        {
            if (!attached) SetState("Dormant", false, "SHUTDOWN");
            return new(code ?? "SHUTDOWN", attached);
        }
        // A cleanly stopped bridge cannot keep the host displaying Attached.
        if (!attached) SetState("NativeDisconnected", false, code ?? "BRIDGE_EXITED");
        return new(code is null or "ATTACHED" or "CONFIGURED" or "CONNECTING" or "SHUTDOWN" ? "BRIDGE_EXITED" : code, attached);
    }

    private async Task WriteCommandsAsync(Process process, DesiredConfiguration initial, CancellationToken cancellationToken)
    {
        await SendAsync(initial);
        await foreach (var desired in _updates.Reader.ReadAllAsync(cancellationToken)) await SendAsync(desired);
        async Task SendAsync(DesiredConfiguration config)
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(2));
            var requestId = Interlocked.Increment(ref _request).ToString(System.Globalization.CultureInfo.InvariantCulture);
            await FramedJson.WriteAsync(process.StandardInput.BaseStream,
                FramedJson.Message("native.configure", requestId, new JsonObject { ["widthDip"] = config.WidthDip, ["configRevision"] = config.Revision }),
                FramedJson.DisplayLimit, deadline.Token);
        }
    }

    private async Task ObserveAsync(Process process, CancellationToken cancellationToken)
    {
        while (await ReadLineAsync(process.StandardOutput, 16384, cancellationToken) is { } line)
        {
            var message = FramedJson.Parse(System.Text.Encoding.UTF8.GetBytes(line));
            var type = message["type"]?.GetValue<string>();
            if ((type is "bridge.liveState" or "bridge.configureResult") && message["payload"] is JsonObject payload)
            {
                ValidateState(payload);
                lock (_gate) _state = (JsonObject)payload.DeepClone();
            }
            else if (type == "bridge.liveStopped")
            {
                var previousCode = Snapshot()["code"]?.GetValue<string>();
                SetState("Dormant", false, previousCode is null or "ATTACHED" or "CONFIGURED" or "CONNECTING" or "INSUFFICIENT_SPACE"
                    ? "SHUTDOWN" : previousCode);
            }
        }
    }

    private void SetState(string state, bool attached, string code)
    {
        lock (_gate) _state = new() { ["state"] = state, ["layoutAttached"] = attached, ["code"] = code };
    }

    private static void ValidateState(JsonObject payload)
    {
        if (payload["state"] is not JsonValue state || !state.TryGetValue<string>(out var name)
            || string.IsNullOrEmpty(name) || name.Length > 48
            || payload["layoutAttached"] is not JsonValue attached || !attached.TryGetValue<bool>(out _))
            throw new InvalidDataException("Invalid native state fields.");
        if (payload["code"] is { } code && (code is not JsonValue codeValue
            || !codeValue.TryGetValue<string>(out var text) || text.Length > 80))
            throw new InvalidDataException("Invalid native state code.");
        if (payload["widthDip"] is { } width && (width is not JsonValue widthValue
            || !widthValue.TryGetValue<double>(out var number) || !double.IsFinite(number) || number is < 0 or > 360))
            throw new InvalidDataException("Invalid native width.");
        if (payload["configRevision"] is { } revision && (revision is not JsonValue revisionValue
            || !revisionValue.TryGetValue<long>(out var version) || version is < 0 or > 9_007_199_254_740_990))
            throw new InvalidDataException("Invalid native revision.");
    }

    private static async Task<string?> ReadLineAsync(StreamReader reader, int limit, CancellationToken cancellationToken)
    {
        var text = new System.Text.StringBuilder();
        var buffer = new char[1];
        while (await reader.ReadAsync(buffer.AsMemory(), cancellationToken) != 0)
        {
            if (buffer[0] == '\n') return text.ToString().TrimEnd('\r');
            if (text.Length >= limit) throw new InvalidDataException("Native status exceeds limit.");
            text.Append(buffer[0]);
        }
        return text.Length == 0 ? null : text.ToString();
    }

    private static async Task DrainAsync(StreamReader reader)
    {
        var buffer = new char[1024];
        try { while (await reader.ReadAsync(buffer) > 0) { } }
        catch (Exception error) when (error is IOException or ObjectDisposedException) { }
    }

    private static string Hash(string path)
    {
        using var file = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(file));
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        await _stop.CancelAsync();
        if (_supervisor is not null) await _supervisor;
        _stop.Dispose();
    }
}
