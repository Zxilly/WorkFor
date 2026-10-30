using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace WorkFor.Contracts;

public static class FramedJson
{
    public const int DisplayLimit = 16 * 1024;
    public const int ControlLimit = 256 * 1024;
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { MaxDepth = 16 };

    public static JsonObject Message(string type, string requestId, JsonNode? payload) => new()
    {
        ["protocolVersion"] = 1,
        ["type"] = type,
        ["requestId"] = requestId,
        ["payload"] = payload
    };

    public static async ValueTask<JsonObject> ReadAsync(Stream stream, int limit, CancellationToken cancellationToken)
    {
        var header = new byte[4];
        await stream.ReadExactlyAsync(header, cancellationToken);
        var length = BinaryPrimitives.ReadUInt32LittleEndian(header);
        if (length is 0 || length > limit) throw new InvalidDataException("Invalid frame length.");
        var bytes = new byte[(int)length];
        await stream.ReadExactlyAsync(bytes, cancellationToken);
        return Parse(bytes);
    }

    public static JsonObject Parse(ReadOnlyMemory<byte> bytes)
    {
        using var document = JsonDocument.Parse(bytes, new() { MaxDepth = 16 });
        ValidateUniqueProperties(document.RootElement);
        return JsonNode.Parse(bytes.Span, documentOptions: new() { MaxDepth = 16 }) as JsonObject
            ?? throw new InvalidDataException("Frame must be a JSON object.");
    }

    public static async ValueTask WriteAsync(Stream stream, JsonObject message, int limit, CancellationToken cancellationToken)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(message, Options);
        if (bytes.Length is 0 || bytes.Length > limit) throw new InvalidDataException("Outgoing frame exceeds limit.");
        var header = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(header, (uint)bytes.Length);
        await stream.WriteAsync(header, cancellationToken);
        await stream.WriteAsync(bytes, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    private static void ValidateUniqueProperties(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new InvalidDataException("Duplicate JSON property.");
                ValidateUniqueProperties(property.Value);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var item in value.EnumerateArray()) ValidateUniqueProperties(item);
    }
}
