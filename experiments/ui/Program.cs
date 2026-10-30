using System.Buffers.Binary;
using System.IO.Pipes;
using System.Text.Json;
using System.Text.Json.Nodes;
using WorkFor.UI;

if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Windows named pipe probe.");
var prefix = $"workfor-ui-probe-{Environment.ProcessId}-{Guid.NewGuid():N}";
using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
var token = timeout.Token;
var received = new List<string>();
var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
var display = Task.Run(async () =>
{
    await using var server = Server(prefix + "-display");
    await server.WaitForConnectionAsync(token);
    var subscription = await ReadFrame(server, token);
    Check(subscription["type"]?.GetValue<string>() == "display.subscribe", "display subscription");
    foreach (var (generation, sequence, amount) in new[]
    {
        ("first", 1, "¥1.00"), ("first", 1, "¥999.00"), ("first", 0, "¥999.00"),
        ("first", 2, "¥1.01"), ("restarted", 1, "¥2.00")
    })
    {
        await WriteFragmented(server, new JsonObject
        {
            ["protocolVersion"] = 1, ["type"] = "display.snapshot", ["requestId"] = "probe",
            ["payload"] = new JsonObject
            {
                ["generation"] = generation, ["sequence"] = sequence, ["title"] = "今日收入",
                ["amountText"] = amount, ["status"] = "fresh", ["statusText"] = "实时更新",
                ["rateText"] = "¥16.00 / 小时", ["scheduledElapsedText"] = "2 小时", ["evaluatedAtMs"] = 1
            }
        }, token);
    }
    await completed.Task.WaitAsync(token);
}, token);
var control = Task.Run(async () =>
{
    await using var server = Server(prefix + "-control");
    await server.WaitForConnectionAsync(token);
    var request = await ReadFrame(server, token);
    Check(request["type"]?.GetValue<string>() == "config.get", "control request");
    await WriteFragmented(server, new JsonObject
    {
        ["protocolVersion"] = 1, ["type"] = "config.result",
        ["requestId"] = request["requestId"]!.GetValue<string>(),
        ["payload"] = new JsonObject { ["revision"] = 7 }
    }, token);
}, token);
await using (var client = new HostClient(channel => prefix + "-" + channel))
{
    client.SnapshotReceived += snapshot =>
    {
        received.Add(snapshot.AmountText);
        if (received.Count == 3) completed.TrySetResult();
    };
    var config = await client.RequestAsync("config.get", cancellationToken: token);
    Check(config["revision"]?.GetValue<int>() == 7, "correlated control response");
    await completed.Task.WaitAsync(token);
    Check(received.SequenceEqual(new[] { "¥1.00", "¥1.01", "¥2.00" }), "duplicate / backwards sequence rejected; new generation accepted");
    await Task.WhenAll(display, control);
}
Console.WriteLine("PASS: UI host client reads fragmented frames, subscribes, drops duplicate/backwards snapshots, accepts host restart, and correlates control replies.");

static NamedPipeServerStream Server(string name) => new(name, PipeDirection.InOut, 1,
    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

static async Task<JsonObject> ReadFrame(Stream stream, CancellationToken token)
{
    var header = new byte[4]; await stream.ReadExactlyAsync(header, token);
    var length = BinaryPrimitives.ReadUInt32LittleEndian(header);
    if (length is 0 or > 262144) throw new InvalidDataException("Invalid frame.");
    var data = new byte[(int)length]; await stream.ReadExactlyAsync(data, token);
    return JsonNode.Parse(data)!.AsObject();
}

static async Task WriteFragmented(Stream stream, JsonObject value, CancellationToken token)
{
    var data = JsonSerializer.SerializeToUtf8Bytes(value);
    var header = new byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(header, (uint)data.Length);
    // Splitting both header and body catches clients that assume one Read == one frame.
    foreach (var part in new[] { header[..2], header[2..], data[..3], data[3..] })
    {
        await stream.WriteAsync(part, token); await stream.FlushAsync(token);
    }
}

static void Check(bool ok, string name)
{
    if (!ok) throw new InvalidOperationException("FAIL: " + name);
    Console.WriteLine("PASS: " + name);
}
