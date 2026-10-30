using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using WorkFor.Core;
using WorkFor.Contracts;

namespace WorkFor.Host;

internal sealed partial class ConfigurationStore
{
    public const int CurrentSchemaVersion = 1;
    private const long MaximumRevision = 9_007_199_254_740_990;
    private readonly string _directory;
    private readonly StartupRegistration? _startup;
    private readonly Func<JsonObject, EvaluationError?>? _persistedValidator;
    private readonly SemaphoreSlim _commit = new(1);
    private JsonObject _current;
    private long _revision;

    public string? RecoveryNotice { get; private set; }
    public long? RecoveredRevision { get; private set; }
    public string? StartupNotice { get; private set; }

    public ConfigurationStore(string directory, StartupRegistration? startup = null, Func<JsonObject, EvaluationError?>? persistedValidator = null)
    {
        _directory = Path.GetFullPath(directory);
        _startup = startup;
        _persistedValidator = persistedValidator;
        Directory.CreateDirectory(_directory);
        var file = Path.Combine(_directory, "settings.json");
        var backup = Path.Combine(_directory, "settings.backup.json");
        if (File.Exists(file))
        {
            try
            {
                _current = ReadPersisted(file);
            }
            catch (ConfigException error) when (error.Code is "SCHEMA_VERSION_UNSUPPORTED" or "CONFIG_VALIDATION_UNAVAILABLE") { throw; }
            catch (Exception error) when (Recoverable(error)) { _current = Recover(file, backup); }
        }
        else if (File.Exists(backup)) _current = Recover(file, backup);
        else
        {
            _current = Defaults();
            _current["rule"]!["manifest"] = DefaultManifest();
            NormalizeAndValidate(_current);
            ValidatePersisted(_current);
        }
        _revision = _current["revision"]!.GetValue<long>();
    }

    public long Revision => Interlocked.Read(ref _revision);
    public JsonObject Snapshot() => (JsonObject)Volatile.Read(ref _current).DeepClone();

    /// <summary>Call explicitly after owning the Host mutex. Construction never touches HKCU.</summary>
    public void ReconcileStartup()
    {
        var enabled = _current["startWithWindows"]!.GetValue<bool>();
        if (_startup is null)
        {
            if (enabled) throw new ConfigException("STARTUP_NOT_AVAILABLE", "配置要求登录启动，但没有可信启动入口。");
            return;
        }
        try { _startup.SetEnabled(enabled); StartupNotice = null; }
        catch (ConfigException error) { StartupNotice = error.Message; throw; }
    }

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
        if (patch.ContainsKey("startWithWindows"))
            candidate["startWithWindows"] = patch["startWithWindows"]?.DeepClone();
        NormalizeAndValidate(candidate);
        return candidate;
    }

    public async Task<JsonObject> ActivateAsync(JsonObject candidate, long expectedRevision, CancellationToken cancellationToken)
    {
        await _commit.WaitAsync(cancellationToken);
        try
        {
            if (Revision != expectedRevision) throw new ConfigException("CONFIG_CONFLICT", "配置已被更新，请重新读取。");
            if (expectedRevision >= MaximumRevision) throw new ConfigException("CONFIG_INVALID", "配置版本超过安全整数范围。");
            candidate = (JsonObject)candidate.DeepClone();
            candidate["revision"] = expectedRevision + 1;
            NormalizeAndValidate(candidate);
            var source = candidate["rule"]!["source"]!.GetValue<string>();
            var hash = candidate["rule"]!["contentHash"]!.GetValue<string>()[7..];
            var ruleDirectory = Path.Combine(_directory, "rules", hash);
            Directory.CreateDirectory(ruleDirectory);
            var sourceFile = Path.Combine(ruleDirectory, "index.mjs");
            if (File.Exists(sourceFile))
            {
                if (new FileInfo(sourceFile).Length > 65536 || await File.ReadAllTextAsync(sourceFile, cancellationToken) != source)
                    throw new ConfigException("RULE_ARCHIVE_CORRUPT", "相同哈希的规则归档内容不匹配，未应用配置。");
            }
            else await File.WriteAllTextAsync(sourceFile, source, new UTF8Encoding(false), cancellationToken);
            var startupChanged = candidate["startWithWindows"]!.GetValue<bool>() != _current["startWithWindows"]!.GetValue<bool>();
            string? previousStartup = null;
            var startupTouched = false;
            try
            {
                if (startupChanged)
                {
                    if (_startup is null) throw new ConfigException("STARTUP_NOT_AVAILABLE", "没有可信启动入口，未更改登录启动项。");
                    try { previousStartup = _startup.Capture(); }
                    catch (Exception error) when (Recoverable(error)) { throw new ConfigException("STARTUP_REGISTRATION_FAILED", "无法读取登录启动项，未应用配置。"); }
                    startupTouched = true;
                    _startup.SetEnabled(candidate["startWithWindows"]!.GetValue<bool>());
                }
                await AtomicJsonFile.WriteAsync(Path.Combine(_directory, "settings.json"), candidate,
                    Path.Combine(_directory, "settings.backup.json"), cancellationToken);
            }
            catch (Exception error)
            {
                if (startupTouched)
                    try { _startup!.Restore(previousStartup); }
                    catch (Exception rollbackError) when (Recoverable(rollbackError))
                    {
                        StartupNotice = "配置未提交，但登录启动项回滚失败，请在系统设置中检查 WorkFor 登录启动状态。";
                        throw new ConfigException("STARTUP_ROLLBACK_FAILED", StartupNotice);
                    }
                if (error is IOException or UnauthorizedAccessException)
                    throw new ConfigException("CONFIG_WRITE_FAILED", "配置无法写入磁盘，未切换活动规则；登录启动项已恢复原值。");
                throw;
            }
            Volatile.Write(ref _current, candidate);
            Interlocked.Exchange(ref _revision, expectedRevision + 1);
            return Snapshot();
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
        if (value["schemaVersion"]?.GetValue<int>() != CurrentSchemaVersion) throw new ConfigException("SCHEMA_VERSION_UNSUPPORTED", "不支持的配置格式。");
        if (value["revision"]?.GetValue<long>() is not (>= 1 and <= MaximumRevision)) throw new ConfigException("CONFIG_INVALID", "无效配置版本。");
        _ = value["startWithWindows"]!.GetValue<bool>();
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
        ValidateCalendar(calendar);
        var source = rule["source"]?.GetValue<string>() ?? throw new ConfigException("CONFIG_INVALID", "缺少规则源文。");
        if (Encoding.UTF8.GetByteCount(source) is 0 or > 65536) throw new ConfigException("CONFIG_INVALID", "脚本大小应为1至65536字节。");
        if (rule["parameters"] is not JsonObject) throw new ConfigException("PARAMETER_INVALID", "参数必须为 JSON 对象。");
        if (rule["manifest"] is not JsonObject) throw new ConfigException("MANIFEST_INVALID", "规则必须包含清单。");
        if (Encoding.UTF8.GetByteCount(rule["parameters"]!.ToJsonString()) > 32768) throw new ConfigException("PARAMETER_INVALID", "参数超过大小限制。");
        ContextValidator.ValidateJson(JsonSerializer.SerializeToElement(rule["parameters"]));
        ValidateManifest(rule);
        rule["contentHash"] = Hash(source);
        if (Encoding.UTF8.GetByteCount(value.ToJsonString()) > 256 * 1024) throw new ConfigException("CONFIG_INVALID", "配置超过大小限制。");
        _ = Context(value, DateTimeOffset.UtcNow, "0");
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
