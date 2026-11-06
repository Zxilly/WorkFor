using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace WorkFor.UI;

/// <summary>One editor's local stdio LSP process and isolated language-service workspace.</summary>
internal sealed class LanguageServerSession : IDisposable
{
    private readonly string _workspace;
    private readonly Process _process;
    private readonly Microsoft.Win32.SafeHandles.SafeFileHandle _job;
    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _writes = new(1);
    private readonly Action<JsonObject> _received;
    private readonly Action<string> _failed;
    private readonly Task _reader;
    private readonly Task _stderr;
    private bool _disposed;
    private string? _schemaKey;
    public string WorkspaceUri => new Uri(_workspace + Path.DirectorySeparatorChar).AbsoluteUri;
    public string DocumentUri { get; }
    public string TypeScriptServerPath { get; }

    public LanguageServerSession(string assets, bool javascript, JsonNode? schema, Action<JsonObject> received, Action<string> failed)
    {
        _received = received;
        _failed = failed;
        _workspace = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WorkFor", "EditorWorkspaces", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_workspace);
        DocumentUri = new Uri(Path.Combine(_workspace, javascript ? "document.js" : "document.json")).AbsoluteUri;
        File.WriteAllText(Path.Combine(_workspace, javascript ? "document.js" : "document.json"), "");
        File.WriteAllText(Path.Combine(_workspace, "tsconfig.json"), """
            {"compilerOptions":{"allowJs":true,"checkJs":true,"noEmit":true,"target":"ES2022","module":"NodeNext","moduleResolution":"NodeNext","lib":["ES2022"],"types":[]},"typeAcquisition":{"enable":false},"include":["*.js","*.d.ts"]}
            """);
        File.WriteAllText(Path.Combine(_workspace, "package.json"), "{\"type\":\"module\"}");
        File.Copy(Path.Combine(assets, "workfor-api.d.ts"), Path.Combine(_workspace, "workfor-api.d.ts"));
        UpdateSchema(schema);
        var modules = Path.Combine(assets, "lsp", "node_modules");
        TypeScriptServerPath = Path.Combine(modules, "typescript", "lib", "tsserver.js");
        var entry = javascript ? Path.Combine(modules, "typescript-language-server", "lib", "cli.mjs")
            : Path.Combine(modules, "vscode-json-languageserver", "bin", "vscode-json-languageserver");
        var info = new ProcessStartInfo(Path.Combine(assets, "lsp", "node.exe"))
        {
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = _workspace,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
        };
        info.Environment.Remove("NODE_OPTIONS");
        foreach (var key in info.Environment.Keys.Where(key => key.Contains("TOKEN", StringComparison.OrdinalIgnoreCase)
            || key.Contains("SECRET", StringComparison.OrdinalIgnoreCase) || key.Contains("API_KEY", StringComparison.OrdinalIgnoreCase)).ToArray()) info.Environment.Remove(key);
        info.ArgumentList.Add(entry);
        info.ArgumentList.Add("--stdio");
        _process = Process.Start(info) ?? throw new IOException("无法启动代码提示服务。");
        try { _job = LanguageServerJob.Attach(_process); }
        catch { if (!_process.HasExited) _process.Kill(entireProcessTree: true); _process.Dispose(); throw; }
        _reader = ReadAsync();
        _stderr = Task.Run(async () =>
        {
            var buffer = new char[1024];
            try { while (await _process.StandardError.ReadAsync(buffer, _stop.Token) != 0) { } }
            catch (Exception error) when (error is IOException or OperationCanceledException or ObjectDisposedException) { }
        });
    }

    public void UpdateSchema(JsonNode? schema)
    {
        var key = schema?.ToJsonString() ?? "";
        if (key == _schemaKey) return;
        _schemaKey = key;
        File.WriteAllText(Path.Combine(_workspace, "parameters.d.ts"), "type WorkForParameters = " + SchemaType(schema, 0) + ";");
    }

    private static string SchemaType(JsonNode? node, int depth)
    {
        if (node is not JsonObject schema || depth > 8) return "Record<string, unknown>";
        if (schema["enum"] is JsonArray values && values.Count <= 128) return string.Join(" | ", values.Select(value => value?.ToJsonString() ?? "null"));
        if (schema["properties"] is JsonObject properties)
        {
            var required = (schema["required"] as JsonArray)?.Select(item => item?.ToString()).ToHashSet() ?? [];
            return "{ " + string.Join(" ", properties.Select(item => "readonly " + JsonSerializer.Serialize(item.Key)
                + (required.Contains(item.Key) ? "" : "?") + ": " + SchemaType(item.Value, depth + 1) + ";")) + " }";
        }
        return schema["type"]?.ToString() switch
        {
            "string" => "string", "number" or "integer" => "number", "boolean" => "boolean", "null" => "null",
            "array" => "ReadonlyArray<" + SchemaType(schema["items"], depth + 1) + ">", _ => "unknown",
        };
    }

    public async Task SendAsync(JsonObject message)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(message);
        if (bytes.Length > 2 * 1024 * 1024) throw new InvalidDataException("代码提示消息过长。");
        await _writes.WaitAsync(_stop.Token);
        try
        {
            await _process.StandardInput.BaseStream.WriteAsync(Encoding.ASCII.GetBytes($"Content-Length: {bytes.Length}\r\n\r\n"), _stop.Token);
            await _process.StandardInput.BaseStream.WriteAsync(bytes, _stop.Token);
            await _process.StandardInput.BaseStream.FlushAsync(_stop.Token);
        }
        finally { _writes.Release(); }
    }

    private async Task ReadAsync()
    {
        try
        {
            var stream = _process.StandardOutput.BaseStream;
            while (!_stop.IsCancellationRequested)
            {
                var header = new List<byte>();
                var single = new byte[1];
                while (true)
                {
                    await stream.ReadExactlyAsync(single, _stop.Token);
                    header.Add(single[0]);
                    if (header.Count > 8192) throw new InvalidDataException("Invalid LSP header.");
                    if (header.Count >= 4 && header[^4] == 13 && header[^3] == 10 && header[^2] == 13 && header[^1] == 10) break;
                }
                var lengthText = Encoding.ASCII.GetString(header.ToArray()).Split("\r\n").First(line => line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))[15..].Trim();
                var length = int.Parse(lengthText, CultureInfo.InvariantCulture);
                if (length is <= 0 or > 2 * 1024 * 1024) throw new InvalidDataException("Invalid LSP frame length.");
                var body = new byte[length];
                await stream.ReadExactlyAsync(body, _stop.Token);
                if (JsonNode.Parse(body) is JsonObject message) _received(message);
            }
        }
        catch (Exception error) when (error is IOException or OperationCanceledException or ObjectDisposedException or JsonException or InvalidOperationException or FormatException)
        { if (!_stop.IsCancellationRequested) _failed("代码提示服务已断开，可重试连接。"); }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _stop.Cancel();
        _job.Dispose();
        try { if (!_process.HasExited) _process.Kill(entireProcessTree: true); }
        catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception) { }
        _process.Dispose();
        foreach (var file in new[] { "document.js", "document.json", "tsconfig.json", "package.json", "workfor-api.d.ts", "parameters.d.ts" })
            try { File.Delete(Path.Combine(_workspace, file)); } catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        try { Directory.Delete(_workspace); } catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }
}
