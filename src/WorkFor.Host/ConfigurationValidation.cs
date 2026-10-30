using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using WorkFor.Core;

namespace WorkFor.Host;

internal sealed partial class ConfigurationStore
{
    private JsonObject Recover(string file, string backup)
    {
        JsonObject recovered;
        try { recovered = ReadPersisted(backup, out _); }
        catch (ConfigException error) when (error.Code == "CONFIG_VALIDATION_UNAVAILABLE") { throw; }
        catch (Exception error) when (Recoverable(error))
        { throw new ConfigException("CONFIG_RECOVERY_FAILED", "配置损坏，且没有经过验证的有效备份；未重置工资或规则，请检查 settings 文件。"); }
        var corrupt = File.Exists(file) ? Path.Combine(_directory, $"settings.corrupt.{DateTimeOffset.UtcNow:yyyyMMddHHmmss}.{Guid.NewGuid():N}.json") : null;
        // Do not rotate the corrupt primary over the known-good backup.
        AtomicJsonFile.WriteAsync(file, recovered, corrupt, CancellationToken.None).GetAwaiter().GetResult();
        RecoveredRevision = recovered["revision"]!.GetValue<long>();
        RecoveryNotice = $"当前配置不可用，已从 settings.backup.json 恢复业务版本 {RecoveredRevision}。请核对收入参数；未使用默认工资替代。";
        return recovered;
    }

    private JsonObject ReadPersisted(string file, out bool migrated)
    {
        var value = Read(file);
        var schema = value["schemaVersion"]?.GetValue<int>() ?? throw new ConfigException("CONFIG_INVALID", "缺少配置格式版本。");
        if (schema is not (1 or CurrentSchemaVersion)) throw new ConfigException("SCHEMA_VERSION_UNSUPPORTED", "此配置格式不受当前版本支持；未回退到可能较旧的工资配置。");
        migrated = schema == 1;
        var rule = value["rule"] as JsonObject ?? throw new ConfigException("CONFIG_INVALID", "缺少规则。");
        var source = rule["source"]?.GetValue<string>() ?? throw new ConfigException("CONFIG_INVALID", "缺少规则源文。");
        var expectedHash = Hash(source);
        if (rule["contentHash"] is { } stored && stored.GetValue<string>() != expectedHash) throw new ConfigException("CONFIG_INVALID", "活动规则哈希与源文不匹配。");
        if (!migrated && rule["contentHash"] is null) throw new ConfigException("CONFIG_INVALID", "配置缺少活动规则哈希。");
        if (migrated && rule["manifest"] is null)
        {
            if (rule["id"]?.GetValue<string>() != "builtin.hourly" || rule["version"]?.GetValue<string>() != "1.0.0") throw new ConfigException("MANIFEST_INVALID", "旧自定义规则缺少清单，无法自动推断其参数含义。");
            rule["manifest"] = DefaultManifest();
        }
        value["schemaVersion"] = CurrentSchemaVersion;
        NormalizeAndValidate(value);
        ValidatePersisted(value);
        return value;
    }

    private void ValidatePersisted(JsonObject value)
    {
        if (_persistedValidator is null) return;
        var error = _persistedValidator((JsonObject)value.DeepClone());
        if (error is null) return;
        if (error.Retryable || error.Code is "SCRIPT_HOST_EXITED" or "SCRIPT_TIMEOUT")
            throw new ConfigException("CONFIG_VALIDATION_UNAVAILABLE", "配置验证进程不可用，未切换到可能较旧的工资备份：" + error.Message);
        throw new ConfigException(error.Code, error.Message, error.Line, error.Column);
    }

    private static void ValidateCalendar(JsonObject calendar)
    {
        var weekdays = calendar["weekdays"]!.AsArray().Select(x => x!.GetValue<int>()).ToArray();
        if (weekdays.Length > 7 || weekdays.Distinct().Count() != weekdays.Length || weekdays.Any(x => x is < 1 or > 7)) throw new ConfigException("CONFIG_INVALID", "工作日必须是1至7之间不重复的整数。");
        var zone = TimeZoneInfo.FindSystemTimeZoneById(calendar["timeZone"]!.GetValue<string>());
        ValidateShifts(calendar["shifts"]!.AsArray());
        if (calendar.ContainsKey("dateOverrides") && calendar["dateOverrides"] is not JsonObject) throw new ConfigException("CONFIG_INVALID", "日期覆盖必须是对象。");
        if (calendar["dateOverrides"] is not JsonObject overrides) return;
        if (overrides.Count > 366) throw new ConfigException("CONFIG_INVALID", "日期覆盖数量超过限制。");
        foreach (var item in overrides)
        {
            var date = DateOnly.ParseExact(item.Key, "yyyy-MM-dd", CultureInfo.InvariantCulture);
            if (item.Value is not JsonArray shifts) throw new ConfigException("CONFIG_INVALID", "日期覆盖必须包含班次数组。");
            ValidateShifts(shifts);
            foreach (var shift in ParseValidatedShifts(shifts))
            {
                var endDate = shift.EndNextDay || shift.End < shift.Start ? date.AddDays(1) : date;
                if (zone.IsInvalidTime(date.ToDateTime(shift.Start)) || zone.IsInvalidTime(endDate.ToDateTime(shift.End))) throw new ConfigException("CONFIG_INVALID", "日期覆盖包含夏令时跳过的本地时刻。");
            }
        }
    }

    private static void ValidateShifts(JsonArray shifts)
    {
        if (shifts.Count > 16) throw new ConfigException("CONFIG_INVALID", "每天最多16个班次。");
        foreach (var item in shifts)
        {
            if (item is not JsonObject shift) throw new ConfigException("CONFIG_INVALID", "班次必须是对象。");
            CheckFields(shift, "start", "end", "endNextDay");
        }
        foreach (var shift in ParseValidatedShifts(shifts))
            if (shift.Start == shift.End && !shift.EndNextDay || shift.EndNextDay && shift.End > shift.Start) throw new ConfigException("CONFIG_INVALID", "班次结束时间不合法；全天班次需要 endNextDay=true。");
    }

    private static IReadOnlyList<Shift> ParseValidatedShifts(JsonArray shifts) => shifts.Select(x => new Shift(
        TimeOnly.ParseExact(x!["start"]!.GetValue<string>(), "HH:mm", CultureInfo.InvariantCulture),
        TimeOnly.ParseExact(x["end"]!.GetValue<string>(), "HH:mm", CultureInfo.InvariantCulture),
        x["endNextDay"]?.GetValue<bool>() ?? false)).ToArray();

    private static void ValidateManifest(JsonObject rule)
    {
        var manifest = rule["manifest"]!.AsObject();
        CheckFields(manifest, "manifestVersion", "apiVersion", "id", "name", "version", "entry", "parametersSchema", "description");
        if (manifest["manifestVersion"]?.GetValue<int>() != 1 || manifest["apiVersion"]?.GetValue<int>() != 1 || manifest["entry"]?.GetValue<string>() != "index.mjs") throw new ConfigException("MANIFEST_INVALID", "不支持的规则清单格式或入口。");
        var id = manifest["id"]!.GetValue<string>();
        var version = manifest["version"]!.GetValue<string>();
        if (!RuleId().IsMatch(id) || version.Length > 80 || !RuleVersion().IsMatch(version) || manifest["name"]!.GetValue<string>().Length is < 1 or > 40 || manifest["description"]?.GetValue<string>().Length > 200) throw new ConfigException("MANIFEST_INVALID", "规则清单元数据不合法。");
        if (rule["id"]?.GetValue<string>() != id || rule["version"]?.GetValue<string>() != version) throw new ConfigException("MANIFEST_INVALID", "规则ID或版本与清单不一致。");
        var schema = manifest["parametersSchema"];
        if (schema is not JsonObject && (schema is not JsonValue boolean || !boolean.TryGetValue<bool>(out _)))
            throw new ConfigException("MANIFEST_INVALID", "参数Schema必须为对象或布尔Schema。");
        if (Encoding.UTF8.GetByteCount(schema.ToJsonString()) > 32768 || Encoding.UTF8.GetByteCount(manifest.ToJsonString()) + Encoding.UTF8.GetByteCount(rule["source"]!.GetValue<string>()) > 96 * 1024) throw new ConfigException("MANIFEST_INVALID", "清单或规则包超过大小限制。");
        ContextValidator.ValidateJson(JsonSerializer.SerializeToElement(schema));
        try { SchemaReferencePolicy.Validate(JsonSerializer.SerializeToElement(schema)); }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or KeyNotFoundException)
        { throw new ConfigException("MANIFEST_INVALID", "Schema引用策略不合法或超过结构预算。"); }
        // The shared policy only traverses schema positions. Full Draft 2020-12
        // evaluation still belongs to a disposable ScriptHost under a deadline.
    }

    private static string Hash(string source) => "sha256:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(source)));
    private static bool Recoverable(Exception error) => error is ConfigException or IOException or InvalidDataException or UnauthorizedAccessException or JsonException
        or InvalidOperationException or ArgumentException or FormatException or NullReferenceException or OverflowException
        or System.Security.SecurityException or System.ComponentModel.Win32Exception;
    [GeneratedRegex("^[a-z0-9][a-z0-9._-]{0,79}$", RegexOptions.CultureInvariant)] private static partial Regex RuleId();
    [GeneratedRegex(@"^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(-[0-9A-Za-z.-]+)?(\+[0-9A-Za-z.-]+)?$", RegexOptions.CultureInvariant)] private static partial Regex RuleVersion();
}
