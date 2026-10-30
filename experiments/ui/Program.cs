using System.Buffers.Binary;
using System.IO.Pipes;
using System.Text.Json;
using System.Text.Json.Nodes;
using WorkFor.UI;

if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Windows named pipe probe.");
VerifyParameterForms();
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
    foreach (var type in new[] { "config.get", "rule.catalog", "host.shutdown" })
    {
        await using var server = Server(prefix + "-control");
        await server.WaitForConnectionAsync(token);
        var request = await ReadFrame(server, token);
        Check(request["type"]?.GetValue<string>() == type, type + " request");
        var payload = type switch
        {
            "config.get" => new JsonObject { ["revision"] = 7 },
            "host.shutdown" => new JsonObject { ["stopping"] = true },
            _ => CatalogFixture()
        };
        await WriteFragmented(server, new JsonObject
        {
            ["protocolVersion"] = 1, ["type"] = "control.result",
            ["requestId"] = request["requestId"]!.GetValue<string>(), ["payload"] = payload
        }, token);
    }
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
    var catalog = RuleCatalogItem.Parse(await client.RequestAsync("rule.catalog", cancellationToken: token));
    Check(catalog.Count == 2 && catalog[1].Id == "builtin.monthly", "hourly/monthly rule catalog");
    VerifySettingsDraft(catalog[1]);
    var stopping = await client.RequestAsync("host.shutdown", cancellationToken: token);
    Check(stopping["stopping"]?.GetValue<bool>() == true, "graceful shutdown acknowledged before UI closes");
    await completed.Task.WaitAsync(token);
    Check(received.SequenceEqual(new[] { "¥1.00", "¥1.01", "¥2.00" }), "duplicate / backwards sequence rejected; new generation accepted");
    await Task.WhenAll(display, control);
}
Console.WriteLine("PASS: UI host client reads fragmented frames, subscribes, drops duplicate/backwards snapshots, accepts host restart, and correlates control replies.");

static JsonObject CatalogFixture() => new()
{
    ["rules"] = new JsonArray(
        new JsonObject
        {
            ["id"] = "builtin.hourly", ["name"] = "时薪", ["source"] = "export default () => ({});",
            ["manifest"] = new JsonObject { ["id"] = "builtin.hourly", ["version"] = "1.0.0" },
            ["defaultParameters"] = new JsonObject { ["hourlyRate"] = "16.00" }
        },
        new JsonObject
        {
            ["id"] = "builtin.monthly", ["name"] = "月薪", ["source"] = "export default () => ({});",
            ["manifest"] = new JsonObject { ["id"] = "builtin.monthly", ["version"] = "1.0.0" },
            ["defaultParameters"] = new JsonObject { ["monthlySalary"] = "16000.00" }
        })
};

static void VerifySettingsDraft(RuleCatalogItem monthly)
{
    var active = JsonNode.Parse("""
        {"revision":7,"display":{"period":"day","currency":"CNY","locale":"zh-CN","monitors":"primary","taskbarWidthDip":260},
         "calendar":{"timeZone":"Asia/Shanghai","weekdays":[1,2,3,4,5],"shifts":[],"dateOverrides":{}},
         "rule":{"id":"builtin.hourly","source":"unchanged","parameters":{"hourlyRate":"16.00"}},"startWithWindows":false}
        """)!.AsObject();
    var original = active.ToJsonString();
    var draft = new SettingsDraft("month", "usd", 320, true, monthly.DefaultParameters.ToJsonString(),
        monthly.Source, monthly.Manifest.ToJsonString(), "Asia/Shanghai", "1,2,3,4,5",
        "[{\"start\":\"00:00\",\"end\":\"00:00\",\"endNextDay\":true}]", "{\"2026-10-01\":[]}");
    var candidate = draft.BuildCandidate(active);
    Check(active.ToJsonString() == original, "switching rules builds an isolated draft without mutating active config");
    Check(candidate["rule"]?["id"]?.GetValue<string>() == "builtin.monthly"
        && candidate["parameters"]?["monthlySalary"]?.GetValue<string>() == "16000.00", "monthly candidate source/metadata/parameters stay together");
    Check(candidate["calendar"]?["dateOverrides"]?["2026-10-01"] is JsonArray { Count: 0 }
        && candidate["calendar"]?["shifts"]?[0]?["endNextDay"]?.GetValue<bool>() == true, "date overrides and cross-midnight calendar are submitted");
    Check(candidate["display"]?["currency"]?.GetValue<string>() == "USD"
        && candidate["display"]?["taskbarWidthDip"]?.GetValue<int>() == 320
        && candidate["startWithWindows"]?.GetValue<bool>() == true, "currency/width/startup candidate values are preserved");
    try { (draft with { Weekdays = "1,1" }).BuildCandidate(active); throw new Exception("Duplicate weekdays accepted."); }
    catch (InvalidDataException) { Console.WriteLine("PASS: duplicate weekdays rejected"); }

    var snapshot = DisplaySnapshot.Parse(JsonNode.Parse("""
        {"generation":"g","sequence":1,"title":"本月收入","amountText":"$2.00","currency":"USD","status":"ruleError",
         "statusText":"计算失败","rateText":"$1.00 / 小时","rateLabel":"折算时薪","error":{"code":"SCRIPT_TIMEOUT","message":"Execution timeout"}}
        """)!.AsObject());
    Check(snapshot.Title == "本月收入" && snapshot.Currency == "USD" && snapshot.RateLabel == "折算时薪"
        && snapshot.ErrorMessage.Contains("计算超过时间限制"), "details preserve dynamic title/currency/monthly rate and explain errors");
}

static void VerifyParameterForms()
{
    var schema = JsonNode.Parse("""
        {"type":"object","required":["hourlyRate"],"properties":{
          "hourlyRate":{"type":"string","title":"时薪","description":"主货币单位","default":"16.00"},
          "multiplier":{"type":"number","default":1.5},"count":{"type":"integer","default":2},
          "includeBonus":{"type":"boolean","default":false},
          "mode":{"type":"string","enum":["regular","overtime"],"default":"regular"},
          "nullableChoice":{"enum":[null,"custom"]}
        }}
        """)!.AsObject();
    var original = JsonNode.Parse("""
        {"hourlyRate":"19.75","unknown":{"keep":true},"mode":"overtime"}
        """)!.AsObject();
    var originalText = original.ToJsonString();
    Check(ParameterFormModel.TryCreate(schema, original, out var model, out _), "flat primitive schema creates native-form metadata");
    Check(model!.Fields.Select(field => field.Kind).SequenceEqual(new[]
        { ParameterFieldKind.String, ParameterFieldKind.Number, ParameterFieldKind.Integer,
          ParameterFieldKind.Boolean, ParameterFieldKind.Enumeration, ParameterFieldKind.Enumeration }),
        "string/number/integer/boolean/primitive enum map to native kinds");
    var hourly = model.Fields[0];
    Check(hourly.Title == "时薪" && hourly.Description == "主货币单位" && hourly.Required,
        "schema title/description/required are preserved");
    Check(model.Snapshot().ToJsonString() == originalText && !model.Contains("multiplier")
        && !model.Contains("includeBonus"), "rendering does not insert optional/default values or change salary");
    model.SetValue("hourlyRate", JsonValue.Create("20.25"));
    Check(model.Snapshot()["unknown"]?["keep"]?.GetValue<bool>() == true
        && model.Value("mode")?.GetValue<string>() == "overtime"
        && original.ToJsonString() == originalText, "editing a native field retains unknown and untouched values without mutating input");
    model.UseDefault("includeBonus");
    Check(model.Contains("includeBonus") && model.Value("includeBonus")?.GetValue<bool>() == false,
        "explicit use-default can store false distinctly from an absent parameter");
    model.SetValue("nullableChoice", null);
    Check(model.Contains("nullableChoice") && model.Value("nullableChoice") is null,
        "null enum selection is distinct from absence");
    model.Remove("includeBonus");
    Check(!model.Contains("includeBonus"), "explicit optional removal removes only that parameter");
    var replacement = JsonNode.Parse("""{"hourlyRate":"21.00","count":3,"extra":"retained"}""")!.AsObject();
    model.ReplaceParameters(replacement);
    Check(model.Value("count")?.GetValue<int>() == 3 && model.Snapshot()["extra"]?.GetValue<string>() == "retained",
        "advanced JSON updates existing native-form state without adding defaults");
    foreach (var complex in new[]
    {
        "true", "{\"type\":\"object\",\"properties\":{\"value\":{\"$ref\":\"#/$defs/value\"}}}",
        "{\"type\":\"object\",\"properties\":{\"list\":{\"type\":\"array\"}}}",
        "{\"type\":\"object\",\"properties\":{\"value\":{\"type\":[\"string\",\"null\"]}}}",
        "{\"type\":\"object\",\"properties\":{\"nested\":{\"type\":\"object\"}}}",
        "{\"type\":\"object\",\"properties\":{\"never\":false}}"
    }) Check(!ParameterFormModel.TryCreate(JsonNode.Parse(complex), new JsonObject(), out _, out _),
        "complex/ref/union/boolean schema stays in advanced JSON");
    var unsafeNumber = JsonNode.Parse("""{"count":9007199254740993,"hourlyRate":"10.00"}""")!.AsObject();
    Check(!ParameterFormModel.TryCreate(schema, unsafeNumber, out _, out _),
        "numeric values outside NumberBox precision are kept in JSON without coercion");
}

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
