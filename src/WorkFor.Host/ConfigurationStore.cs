using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using WorkFor.Core;
using WorkFor.Contracts;

namespace WorkFor.Host;

internal sealed class ConfigurationStore
{
    private readonly string _directory;
    private readonly SemaphoreSlim _commit = new(1);
    private JsonObject _current;
    private long _revision;

    public ConfigurationStore(string directory)
    {
        _directory = Path.GetFullPath(directory);
        Directory.CreateDirectory(_directory);
        var file = Path.Combine(_directory, "settings.json");
        if (File.Exists(file))
        {
            // A corrupt configuration is an error, never silently replace a salary.
            _current = Read(file);
        }
        else _current = Defaults();
        if (_current["rule"] is JsonObject existing && existing["manifest"] is null)
            existing["manifest"] = DefaultManifest();
        NormalizeAndValidate(_current);
        _revision = _current["revision"]!.GetValue<long>();
    }

    public long Revision => Interlocked.Read(ref _revision);
    public JsonObject Snapshot() => (JsonObject)Volatile.Read(ref _current).DeepClone();

    public JsonObject Candidate(JsonObject patch)
    {
        CheckFields(patch, "expectedRevision", "source", "parameters", "display", "calendar", "rule", "startWithWindows", "at", "manifest");
        foreach (var section in new[] { "parameters", "display", "calendar", "rule", "manifest" })
            if (patch.ContainsKey(section) && patch[section] is not JsonObject)
                throw new ConfigException("CONFIG_INVALID", $"{section} 必须为 JSON 对象。");
        if (patch.ContainsKey("source") && (patch["source"] is not JsonValue v || !v.TryGetValue<string>(out _)))
            throw new ConfigException("CONFIG_INVALID", "source 必须为字符串。");
        var candidate = Snapshot();
        foreach (var name in new[] { "display", "calendar", "rule" })
            if (patch[name] is JsonObject section) Merge((JsonObject)candidate[name]!, section);
        if (patch["parameters"] is JsonObject parameters)
            ((JsonObject)candidate["rule"]!)["parameters"] = parameters.DeepClone();
        if (patch["source"] is JsonValue source)
            ((JsonObject)candidate["rule"]!)["source"] = source.DeepClone();
        if (patch["manifest"] is JsonObject manifest)
            ((JsonObject)candidate["rule"]!)["manifest"] = manifest.DeepClone();
        if (patch["startWithWindows"] is not null)
            candidate["startWithWindows"] = patch["startWithWindows"]!.DeepClone();
        NormalizeAndValidate(candidate);
        return candidate;
    }

    public async Task<JsonObject> ActivateAsync(JsonObject candidate, long expectedRevision, CancellationToken cancellationToken)
    {
        await _commit.WaitAsync(cancellationToken);
        try
        {
            if (Revision != expectedRevision) throw new ConfigException("CONFIG_CONFLICT", "配置已被更新，请重新读取。");
            candidate = (JsonObject)candidate.DeepClone();
            candidate["revision"] = checked(expectedRevision + 1);
            NormalizeAndValidate(candidate);
            var source = candidate["rule"]!["source"]!.GetValue<string>();
            var hash = candidate["rule"]!["contentHash"]!.GetValue<string>()[7..];
            var ruleDirectory = Path.Combine(_directory, "rules", hash);
            Directory.CreateDirectory(ruleDirectory);
            var sourceFile = Path.Combine(ruleDirectory, "index.mjs");
            if (!File.Exists(sourceFile)) await File.WriteAllTextAsync(sourceFile, source, new UTF8Encoding(false), cancellationToken);
            var temporary = Path.Combine(_directory, $"settings.{Guid.NewGuid():N}.tmp");
            try
            {
                var bytes = JsonSerializer.SerializeToUtf8Bytes(candidate, new JsonSerializerOptions { WriteIndented = true });
                await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                    4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
                {
                    await stream.WriteAsync(bytes, cancellationToken);
                    stream.Flush(flushToDisk: true);
                }
                var file = Path.Combine(_directory, "settings.json");
                if (File.Exists(file)) File.Replace(temporary, file, Path.Combine(_directory, "settings.backup.json"));
                else File.Move(temporary, file);
                Volatile.Write(ref _current, candidate);
                Interlocked.Exchange(ref _revision, expectedRevision + 1);
                return Snapshot();
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        finally { _commit.Release(); }
    }

    public static WorkForContext Context(JsonObject config, DateTimeOffset now, string evaluationId)
    {
        var calendar = (JsonObject)config["calendar"]!;
        var shifts = ((JsonArray)calendar["shifts"]!).Select(x => new Shift(
            TimeOnly.ParseExact(x!["start"]!.GetValue<string>(), "HH:mm", CultureInfo.InvariantCulture),
            TimeOnly.ParseExact(x["end"]!.GetValue<string>(), "HH:mm", CultureInfo.InvariantCulture),
            x["endNextDay"]?.GetValue<bool>() ?? false)).ToArray();
        var weekdays = ((JsonArray)calendar["weekdays"]!).Select(x => x!.GetValue<int>()).ToArray();
        var overrides = new Dictionary<DateOnly, IReadOnlyList<Shift>>();
        if (calendar["dateOverrides"] is JsonObject dates)
            foreach (var item in dates)
                overrides.Add(DateOnly.ParseExact(item.Key, "yyyy-MM-dd", CultureInfo.InvariantCulture),
                    ((JsonArray)item.Value!).Select(x => new Shift(
                        TimeOnly.ParseExact(x!["start"]!.GetValue<string>(), "HH:mm", CultureInfo.InvariantCulture),
                        TimeOnly.ParseExact(x["end"]!.GetValue<string>(), "HH:mm", CultureInfo.InvariantCulture),
                        x["endNextDay"]?.GetValue<bool>() ?? false)).ToArray());
        var display = (JsonObject)config["display"]!;
        return CalendarContextBuilder.Build(now, new(calendar["timeZone"]!.GetValue<string>(), weekdays, shifts, overrides),
            CurrencyCatalog.Get(display["currency"]!.GetValue<string>()),
            JsonSerializer.SerializeToElement(config["rule"]!["parameters"]),
            display["period"]!.GetValue<string>(), evaluationId,
            config["revision"]!.GetValue<long>(), config["revision"]!.GetValue<long>());
    }

    public static void NormalizeAndValidate(JsonObject value)
    {
        try { Validate(value); }
        catch (ConfigException) { throw; }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or FormatException
            or NullReferenceException or JsonException or TimeZoneNotFoundException or InvalidTimeZoneException or OverflowException)
        { throw new ConfigException("CONFIG_INVALID", "配置字段、时区或计薪班次不合法。"); }
    }

    private static void Validate(JsonObject value)
    {
        CheckFields(value, "schemaVersion", "revision", "display", "calendar", "rule", "startWithWindows");
        if (value["schemaVersion"]?.GetValue<int>() != 1) throw new ConfigException("CONFIG_INVALID", "不支持的配置格式。");
        if (value["revision"]?.GetValue<long>() < 1) throw new ConfigException("CONFIG_INVALID", "无效配置版本。");
        var display = value["display"] as JsonObject ?? throw new ConfigException("CONFIG_INVALID", "缺少显示配置。");
        CheckFields(display, "period", "locale", "currency", "taskbarWidthDip", "monitors");
        if (display["period"]?.GetValue<string>() is not ("day" or "month")) throw new ConfigException("CONFIG_INVALID", "统计范围应为 day 或 month。");
        if (display["taskbarWidthDip"]!.GetValue<int>() is < 180 or > 360) throw new ConfigException("CONFIG_INVALID", "任务栏宽度必须为180至360 DIP。");
        if (display["monitors"]!.GetValue<string>() != "primary") throw new ConfigException("CONFIG_INVALID", "当前只支持主显示器。");
        _ = CultureInfo.GetCultureInfo(display["locale"]!.GetValue<string>());
        _ = CurrencyCatalog.Get(display["currency"]!.GetValue<string>());
        var rule = value["rule"] as JsonObject ?? throw new ConfigException("CONFIG_INVALID", "缺少规则。");
        CheckFields(rule, "id", "version", "source", "parameters", "contentHash", "manifest");
        var calendar = value["calendar"]!.AsObject();
        CheckFields(calendar, "timeZone", "weekdays", "shifts", "dateOverrides");
        if (calendar["weekdays"]!.AsArray().Count > 7 || calendar["shifts"]!.AsArray().Count > 16)
            throw new ConfigException("CONFIG_INVALID", "工作日或班次数量超过限制。");
        if (calendar["dateOverrides"] is JsonObject overrides && overrides.Count > 366)
            throw new ConfigException("CONFIG_INVALID", "日期覆盖数量超过限制。");
        foreach (var item in calendar["shifts"]!.AsArray()) CheckFields(item!.AsObject(), "start", "end", "endNextDay");
        var source = rule["source"]?.GetValue<string>() ?? throw new ConfigException("CONFIG_INVALID", "缺少规则源文。");
        if (Encoding.UTF8.GetByteCount(source) is 0 or > 65536) throw new ConfigException("CONFIG_INVALID", "脚本大小应为1至65536字节。");
        if (rule["parameters"] is not JsonObject) throw new ConfigException("PARAMETER_INVALID", "参数必须为 JSON 对象。");
        if (rule["manifest"] is not JsonObject) throw new ConfigException("MANIFEST_INVALID", "规则必须包含清单。");
        if (Encoding.UTF8.GetByteCount(rule["parameters"]!.ToJsonString()) > 32768) throw new ConfigException("PARAMETER_INVALID", "参数超过大小限制。");
        rule["contentHash"] = "sha256:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(source)));
        _ = Context(value, DateTimeOffset.UtcNow, "0");
        if (value["startWithWindows"]?.GetValue<bool>() == true)
            throw new ConfigException("STARTUP_NOT_AVAILABLE", "此开发版本尚未安装登录启动项，请保持此选项关闭。");
    }

    private static JsonObject Read(string file)
    {
        if (new FileInfo(file).Length > 256 * 1024) throw new InvalidDataException("配置超过大小限制。");
        return FramedJson.Parse(File.ReadAllBytes(file));
    }

    private static void CheckFields(JsonObject value, params string[] fields)
    {
        if (value.Any(x => !fields.Contains(x.Key, StringComparer.Ordinal)))
            throw new ConfigException("CONFIG_INVALID", "配置包含未知字段。");
    }

    private static void Merge(JsonObject target, JsonObject patch)
    {
        foreach (var property in patch)
            if (target[property.Key] is JsonObject nested && property.Value is JsonObject objectValue) Merge(nested, objectValue);
            else target[property.Key] = property.Value?.DeepClone();
    }

    private static JsonObject DefaultManifest() => JsonNode.Parse("""
      {"manifestVersion":1,"apiVersion":1,"id":"builtin.hourly","name":"时薪规则","version":"1.0.0","entry":"index.mjs",
       "parametersSchema":{"$schema":"https://json-schema.org/draft/2020-12/schema","type":"object","required":["hourlyRate"],"additionalProperties":false,
         "properties":{"hourlyRate":{"title":"时薪","type":"string","pattern":"^(0|[1-9][0-9]{0,11})(\\.[0-9]{1,4})?$"}}}}
      """)!.AsObject();

    private static JsonObject Defaults() => JsonNode.Parse("""
      {"schemaVersion":1,"revision":1,
       "display":{"period":"day","locale":"zh-CN","currency":"CNY","taskbarWidthDip":260,"monitors":"primary"},
       "calendar":{"timeZone":"Asia/Shanghai","weekdays":[1,2,3,4,5],"shifts":[{"start":"09:00","end":"12:00"},{"start":"13:00","end":"18:00"}]},
       "rule":{"id":"builtin.hourly","version":"1.0.0","parameters":{"hourlyRate":"16.00"},"source":"import { money } from '@workfor/sdk';\nexport default function calculate(ctx) {\n const rate = money.fromDecimal(ctx.parameters.hourlyRate, ctx.currency.fractionDigits);\n return {apiVersion:1,currency:ctx.currency.code,amountMinor:money.ratio(rate,ctx.periods[ctx.activePeriod].elapsedScheduledMs,3600000,'floor'),ratePerHourMinor:rate};\n}"},
       "startWithWindows":false}
      """)!.AsObject();
}

internal sealed class ConfigException(string code, string message, int? line = null, int? column = null) : Exception(message)
{
    public string Code { get; } = code;
    public int? Line { get; } = line;
    public int? Column { get; } = column;
}
