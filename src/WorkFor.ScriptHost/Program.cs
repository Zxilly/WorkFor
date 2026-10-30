using System.Buffers.Binary;
using System.Text.Json;
using WorkFor.Core;
using WorkFor.ScriptHost;

using var input = Console.OpenStandardInput();
using var output = Console.OpenStandardOutput();
var evaluator = new RuleEvaluator();
while (true)
{
    byte[]? frame;
    try { frame = await Framing.ReadAsync(input); }
    catch (Exception ex) when (ex is IOException or InvalidDataException) { return 2; }
    if (frame is null) return 0;
    string requestId = "";
    object response;
    try
    {
        using var document = JsonDocument.Parse(frame, new JsonDocumentOptions { MaxDepth = 16 });
        ContextValidator.ValidateJson(document.RootElement);
        var request = document.RootElement.Deserialize<ScriptRequest>(WorkForJson.Options) ?? throw new JsonException();
        requestId = request.RequestId;
        if (request.ProtocolVersion != 1 || string.IsNullOrEmpty(requestId) || requestId.Length > 80 || request.Payload.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("Unsupported request.");
        if (request.Type == "evaluation.evaluate")
        {
            var payload = request.Payload.Deserialize<EvaluatePayload>(WorkForJson.Options) ?? throw new JsonException();
            var outcome = evaluator.Evaluate(payload.Source, payload.Context);
            response = outcome.Success
                ? new { protocolVersion = 1, type = "evaluation.result", requestId, payload = (object)outcome.Result! }
                : new { protocolVersion = 1, type = "evaluation.error", requestId, payload = (object)outcome.Error! };
        }
        else if (request.Type == "rule.validate")
        {
            var payload = request.Payload.Deserialize<ValidationPayload>(WorkForJson.Options) ?? throw new JsonException();
            var error = RulePackageValidator.Validate(payload.Manifest, payload.Source, payload.Parameters);
            response = new { protocolVersion = 1, type = "rule.validation", requestId, payload = new { valid = error is null, error } };
        }
        else throw new ArgumentException("Unsupported request type.");
    }
    catch (Exception ex) when (ex is JsonException or ArgumentException or NullReferenceException)
    {
        response = new { protocolVersion = 1, type = "evaluation.error", requestId, payload = new EvaluationError("REQUEST_INVALID", "Invalid evaluation request.") };
    }
    await Framing.WriteAsync(output, JsonSerializer.SerializeToUtf8Bytes(response, WorkForJson.Options));
}

internal sealed record ScriptRequest(int ProtocolVersion, string Type, string RequestId, JsonElement Payload);
internal sealed record EvaluatePayload(string Source, WorkForContext Context);
internal sealed record ValidationPayload(JsonElement Manifest, string Source, JsonElement Parameters);

internal static class Framing
{
    private const int Maximum = 256 * 1024;
    public static async Task<byte[]?> ReadAsync(Stream stream)
    {
        var header = new byte[4];
        var first = await stream.ReadAsync(header.AsMemory(0, 1));
        if (first == 0) return null;
        await stream.ReadExactlyAsync(header.AsMemory(1));
        var length = BinaryPrimitives.ReadUInt32LittleEndian(header);
        if (length is 0 or > Maximum) throw new InvalidDataException("Frame exceeds limit.");
        var content = new byte[length];
        await stream.ReadExactlyAsync(content);
        return content;
    }
    public static async Task WriteAsync(Stream stream, byte[] content)
    {
        if (content.Length > Maximum) throw new InvalidDataException("Frame exceeds limit.");
        var header = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(header, content.Length);
        await stream.WriteAsync(header); await stream.WriteAsync(content); await stream.FlushAsync();
    }
}
