using System.Diagnostics;
using System.Text.Json.Nodes;

namespace WorkFor.Host;

/// <summary>Owns only our native bridge. EOF requests logical detach before process termination.</summary>
internal sealed class NativeSession(string? executable) : IAsyncDisposable
{
    private readonly object _gate = new();
    private JsonObject _state = new() { ["state"] = "NotAttached", ["layoutAttached"] = false };
    private Process? _process;
    private Task? _reader;
    private Task? _stderr;
    private bool _stopping;

    public JsonObject Snapshot() { lock (_gate) return (JsonObject)_state.DeepClone(); }

    public void Start()
    {
        if (executable is null) return;
        try { StartCore(); }
        catch (Exception error) when (error is ArgumentException or IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            lock (_gate) _state = new() { ["state"] = "Unsupported", ["layoutAttached"] = false, ["code"] = "NATIVE_START_FAILED" };
        }
    }

    private void StartCore()
    {
        if (executable is null) return;
        var path = Path.GetFullPath(executable);
        if (!File.Exists(path) || Path.GetFileName(path) != "WorkFor.NativeBridge.exe")
            throw new ArgumentException("Native bridge must be the installed WorkFor.NativeBridge.exe.");
        var info = new ProcessStartInfo(path)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        info.ArgumentList.Add("--run-explorer");
        _process = Process.Start(info) ?? throw new InvalidOperationException("Cannot start native bridge.");
        _reader = ObserveAsync(_process);
        var stderr = _process.StandardError;
        _stderr = Task.Run(async () =>
        {
            var buffer = new char[1024];
            try { while (await stderr.ReadAsync(buffer) > 0) { } }
            catch (Exception error) when (error is IOException or ObjectDisposedException) { }
        });
    }

    private async Task ObserveAsync(Process process)
    {
        try
        {
            while (await ReadLineAsync(process.StandardOutput, 16384) is { } line)
            {
                var message = JsonNode.Parse(line, documentOptions: new() { MaxDepth = 16 }) as JsonObject;
                if (message?["payload"] is JsonObject payload)
                    lock (_gate) _state = (JsonObject)payload.DeepClone();
                else if (message?["type"]?.GetValue<string>() == "bridge.liveStopped")
                    lock (_gate) _state = new() { ["state"] = "Dormant", ["layoutAttached"] = false };
            }
        }
        catch (Exception error) when (error is IOException or InvalidDataException or System.Text.Json.JsonException)
        { lock (_gate) _state = new() { ["state"] = "NativeError", ["layoutAttached"] = false, ["code"] = "BRIDGE_PROTOCOL_ERROR" }; }
        await process.WaitForExitAsync();
        if (!_stopping && process.ExitCode != 0)
            lock (_gate) _state = new() { ["state"] = "Unsupported", ["layoutAttached"] = false, ["code"] = "NATIVE_ATTACH_FAILED" };
        else
            lock (_gate) _state = new() { ["state"] = _stopping ? "Dormant" : "NativeDisconnected", ["layoutAttached"] = false };
    }

    private static async Task<string?> ReadLineAsync(StreamReader reader, int limit)
    {
        var content = new System.Text.StringBuilder();
        var buffer = new char[1];
        while (await reader.ReadAsync(buffer) != 0)
        {
            if (buffer[0] == '\n') return content.ToString().TrimEnd('\r');
            if (content.Length >= limit) throw new InvalidDataException("Native status line exceeds limit.");
            content.Append(buffer[0]);
        }
        return content.Length == 0 ? null : content.ToString();
    }

    public async ValueTask DisposeAsync()
    {
        if (_process is not { } process) return;
        _stopping = true;
        try
        {
            if (!process.HasExited)
            {
                process.StandardInput.Close();
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                try { await process.WaitForExitAsync(deadline.Token); }
                catch (OperationCanceledException)
                {
                    lock (_gate) _state = new() { ["state"] = "DetachPending", ["layoutAttached"] = false };
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync();
                }
            }
            if (_reader is not null) await _reader;
            if (_stderr is not null) await _stderr;
        }
        finally { process.Dispose(); _process = null; }
    }
}
