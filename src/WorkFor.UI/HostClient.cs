using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Principal;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace WorkFor.UI;

/// <summary>
/// The UI consumes host snapshots. It never runs a second earnings accumulator.
/// Each control connection owns one request, so replies cannot cross between callers.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class HostClient : IAsyncDisposable
{
    private const int DisplayLimit = 16 * 1024;
    private const int ControlLimit = 256 * 1024;
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _subscription;
    private readonly Func<string, string> _pipeName;
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private long _requestSequence;
    private int _disposed;

    public event Action<DisplaySnapshot>? SnapshotReceived;
    public event Action<string>? ConnectionChanged;

    public HostClient(Func<string, string>? pipeName = null)
    {
        _pipeName = pipeName ?? PipeName;
        _subscription = Task.Run(SubscribeAsync);
    }

    public static string PipeName(string channel)
    {
        using var identity = WindowsIdentity.GetCurrent();
        var sid = identity.User?.Value
            ?? throw new InvalidOperationException("无法确定当前用户。");
        return $"workfor-{channel}-v1-{sid}-{Process.GetCurrentProcess().SessionId}";
    }

    public async Task<JsonObject> RequestAsync(string type, JsonObject? payload = null,
        CancellationToken cancellationToken = default)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token, cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(15));
        await using var pipe = NewPipe("control");
        await pipe.ConnectAsync(2500, deadline.Token);
        var requestId = Interlocked.Increment(ref _requestSequence).ToString(System.Globalization.CultureInfo.InvariantCulture);
        await WriteFrameAsync(pipe, new JsonObject
        {
            ["protocolVersion"] = 1,
            ["type"] = type,
            ["requestId"] = requestId,
            ["payload"] = payload
        }, ControlLimit, deadline.Token);
        var response = await ReadFrameAsync(pipe, ControlLimit, deadline.Token);
        if (response["protocolVersion"]?.GetValue<int>() != 1)
            throw new HostRequestException("PROTOCOL_MISMATCH", "宿主协议版本不兼容，请更新完整应用。");
        if (response["requestId"]?.GetValue<string>() != requestId)
            throw new HostRequestException("REQUEST_MISMATCH", "宿主返回了不匹配的请求编号。");
        var result = response["payload"] as JsonObject ?? new JsonObject();
        var responseType = response["type"]?.GetValue<string>() ?? "";
        if (responseType.EndsWith(".error", StringComparison.Ordinal) || responseType == "error")
        {
            var message = result["message"]?.GetValue<string>() ?? "宿主未能完成请求。";
            if (result["line"] is JsonValue line && line.TryGetValue<int>(out var lineNumber))
                message += $"（第 {lineNumber} 行，第 {result["column"]?.GetValue<int>() ?? 1} 列）";
            throw new HostRequestException(result["code"]?.GetValue<string>() ?? "HOST_ERROR", message);
        }
        return result;
    }

    private async Task SubscribeAsync()
    {
        var retry = TimeSpan.FromMilliseconds(500);
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                ConnectionChanged?.Invoke("正在连接计算宿主");
                await using var pipe = NewPipe("display");
                await pipe.ConnectAsync(2500, _stop.Token);
                await WriteFrameAsync(pipe, new JsonObject
                {
                    ["protocolVersion"] = 1, ["type"] = "display.subscribe",
                    ["requestId"] = "ui-display", ["payload"] = new JsonObject()
                }, DisplayLimit, _stop.Token);
                string? generation = null;
                long sequence = -1;
                retry = TimeSpan.FromMilliseconds(500);
                while (!_stop.IsCancellationRequested)
                {
                    using var staleDeadline = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                    staleDeadline.CancelAfter(TimeSpan.FromSeconds(6));
                    var message = await ReadFrameAsync(pipe, DisplayLimit, staleDeadline.Token);
                    if (message["protocolVersion"]?.GetValue<int>() != 1)
                        throw new InvalidDataException("宿主协议不兼容。");
                    if (message["type"]?.GetValue<string>() != "display.snapshot") continue;
                    var payload = message["payload"] as JsonObject
                        ?? throw new InvalidDataException("金额快照缺少数据。");
                    var incoming = DisplaySnapshot.Parse(payload);
                    if (generation == incoming.Generation && incoming.Sequence <= sequence) continue;
                    generation = incoming.Generation;
                    sequence = incoming.Sequence;
                    SnapshotReceived?.Invoke(incoming);
                    ConnectionChanged?.Invoke("");
                }
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { return; }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or TimeoutException or OperationCanceledException
                or JsonException or InvalidDataException or InvalidOperationException)
            {
                ConnectionChanged?.Invoke("未连接计算宿主，正在重试");
            }
            try { await Task.Delay(retry, _stop.Token); }
            catch (OperationCanceledException) { return; }
            retry = TimeSpan.FromMilliseconds(Math.Min(retry.TotalMilliseconds * 2, 10000));
        }
    }

    private NamedPipeClientStream NewPipe(string channel) => new(".", _pipeName(channel),
        PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

    private static async Task<JsonObject> ReadFrameAsync(Stream stream, int limit, CancellationToken cancellationToken)
    {
        var header = new byte[4];
        await stream.ReadExactlyAsync(header, cancellationToken);
        var length = BinaryPrimitives.ReadUInt32LittleEndian(header);
        if (length == 0 || length > limit) throw new InvalidDataException("宿主消息长度不合法。");
        var bytes = new byte[(int)length];
        await stream.ReadExactlyAsync(bytes, cancellationToken);
        return JsonNode.Parse(bytes, documentOptions: new JsonDocumentOptions { MaxDepth = 16 }) as JsonObject
            ?? throw new InvalidDataException("宿主消息不是 JSON 对象。");
    }

    private static async Task WriteFrameAsync(Stream stream, JsonObject value, int limit,
        CancellationToken cancellationToken)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, Json);
        if (bytes.Length > limit) throw new InvalidDataException("请求内容太长。");
        var header = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(header, (uint)bytes.Length);
        await stream.WriteAsync(header, cancellationToken);
        await stream.WriteAsync(bytes, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        await _stop.CancelAsync();
        try { await _subscription; } catch (OperationCanceledException) { }
        _stop.Dispose();
    }
}

internal sealed record DisplaySnapshot(string Generation, long Sequence, string Title, string AmountText,
    string Status, string StatusText, string RateText, string ScheduledElapsedText, string Note,
    long EvaluatedAtMs, string Currency, string RateLabel, string ErrorMessage)
{
    public static DisplaySnapshot Parse(JsonObject value) => new(
        Text(value, "generation", ""),
        value["sequence"]?.GetValue<long>() ?? 0,
        Text(value, "title", "今日收入"),
        Text(value, "amountText", "--"),
        Text(value, "status", "stale"),
        Text(value, "statusText", "等待更新"),
        Text(value, "rateText", ""),
        Text(value, "scheduledElapsedText", "--"),
        Text(value, "note", ""),
        value["evaluatedAtMs"]?.GetValue<long>() ?? 0,
        Text(value, "currency", ""),
        Text(value, "rateLabel", "当前时薪"),
        Error(value));

    private static string Error(JsonObject value)
    {
        if (value["error"] is not JsonObject error) return "";
        var message = UiMessages.Error(Text(error, "code", ""), Text(error, "message", "计算未成功，请检查规则。"));
        if (error["line"] is JsonValue line && line.TryGetValue<int>(out var lineNumber))
            message += $"（第 {lineNumber} 行，第 {error["column"]?.GetValue<int>() ?? 1} 列）";
        return message;
    }

    private static string Text(JsonObject value, string name, string fallback)
    {
        var text = value[name]?.GetValue<string>() ?? fallback;
        return text.Length <= 512 ? text : throw new InvalidDataException("快照文本超过限制。");
    }
}

internal sealed class HostRequestException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}
