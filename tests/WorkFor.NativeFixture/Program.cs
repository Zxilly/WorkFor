using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;

// Contract peer used only by NativeSessionTests. No Windows hook, DLL load,
// named pipe or Explorer operation exists in this program.
var directory = AppContext.BaseDirectory;
var marker = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(directory, "WorkFor.Taskbar.Live.dll")));
if (marker?["fixture"]?.GetValue<bool>() != true) return 2;
var mode = marker["mode"]?.GetValue<string>() ?? "normal";
using var identity = Process.GetCurrentProcess();
await LogAsync(new() { ["event"] = "start", ["mode"] = mode, ["processId"] = Environment.ProcessId,
    ["creationTimeTicks"] = identity.StartTime.ToUniversalTime().Ticks });
using var input = Console.OpenStandardInput();
var first = await ReadFrameAsync(input);
if (first is null) return 0;
await LogAsync(new() { ["event"] = "command", ["message"] = first.DeepClone() });

if (mode == "exit-attached")
{
    await EmitStateAsync("Attached", true, "ATTACHED", first);
    return 0;
}
if (mode == "unsupported")
{
    await EmitStateAsync("Unsupported", false, "UNSUPPORTED_BUILD", first);
    return 0;
}
if (mode is "malformed" or "oversized")
{
    await Console.Out.WriteLineAsync(mode == "malformed" ? "{" : new string('X', 16_500));
    await Console.Out.FlushAsync();
    while (await ReadFrameAsync(input) is not null) { }
    await LogAsync(new() { ["event"] = "stdin.eof" });
    return 0;
}
if (mode == "malformed-stubborn")
{
    await Console.Out.WriteLineAsync("{");
    await Console.Out.FlushAsync();
    await Task.Delay(Timeout.InfiniteTimeSpan);
    return 0;
}
if (mode == "invalid-state")
{
    await EmitAsync(new()
    {
        ["type"] = "bridge.liveState",
        ["payload"] = new JsonObject { ["state"] = 42, ["layoutAttached"] = "yes", ["code"] = new JsonArray() }
    });
    await LogAsync(new() { ["event"] = "invalid-state-emitted" });
    while (await ReadFrameAsync(input) is not null) { }
    return 0;
}

await EmitStateAsync("Attached", true, "ATTACHED", first);
await EmitConfigurationAsync(first);
while (await ReadFrameAsync(input) is { } command)
{
    await LogAsync(new() { ["event"] = "command", ["message"] = command.DeepClone() });
    await EmitConfigurationAsync(command);
}
await LogAsync(new() { ["event"] = "stdin.eof" });
await EmitStateAsync("Dormant", false, "SHUTDOWN");
await EmitAsync(new() { ["type"] = "bridge.liveStopped", ["layoutAttached"] = false });
return 0;

async Task EmitConfigurationAsync(JsonObject command)
{
    var payload = command["payload"]!.AsObject();
    await EmitAsync(new()
    {
        ["type"] = "bridge.configureResult", ["requestId"] = command["requestId"]!.DeepClone(),
        ["payload"] = new JsonObject
        {
            ["state"] = "Attached", ["layoutAttached"] = true, ["code"] = "CONFIGURED",
            ["widthDip"] = payload["widthDip"]!.DeepClone(),
            ["configRevision"] = payload["configRevision"]!.DeepClone()
        }
    });
}

async Task EmitStateAsync(string state, bool attached, string code, JsonObject? command = null)
{
    var payload = new JsonObject { ["state"] = state, ["layoutAttached"] = attached, ["code"] = code };
    if (command?["payload"] is JsonObject config)
    {
        payload["widthDip"] = config["widthDip"]?.DeepClone();
        payload["configRevision"] = config["configRevision"]?.DeepClone();
    }
    await EmitAsync(new() { ["type"] = "bridge.liveState", ["payload"] = payload });
}

static async Task EmitAsync(JsonObject message)
{
    await Console.Out.WriteLineAsync(message.ToJsonString());
    await Console.Out.FlushAsync();
}

async Task LogAsync(JsonObject message)
{
    var bytes = Encoding.UTF8.GetBytes(message.ToJsonString() + "\n");
    await using var log = new FileStream(Path.Combine(directory, "fixture-events.jsonl"), FileMode.Append,
        FileAccess.Write, FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.Asynchronous);
    await log.WriteAsync(bytes);
    await log.FlushAsync();
}

static async Task<JsonObject?> ReadFrameAsync(Stream stream)
{
    var header = new byte[4];
    var first = await stream.ReadAsync(header.AsMemory(0, 1));
    if (first == 0) return null;
    await stream.ReadExactlyAsync(header.AsMemory(1));
    var count = BinaryPrimitives.ReadUInt32LittleEndian(header);
    if (count is 0 or > 16_384) throw new InvalidDataException("Fixture frame exceeds contract limit.");
    var body = new byte[(int)count];
    await stream.ReadExactlyAsync(body);
    return JsonNode.Parse(body) as JsonObject ?? throw new InvalidDataException("Fixture frame must be an object.");
}
