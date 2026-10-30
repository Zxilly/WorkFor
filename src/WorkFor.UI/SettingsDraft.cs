using System.Text.Json;
using System.Text.Json.Nodes;

namespace WorkFor.UI;

/// <summary>A UI draft is isolated from the active host configuration until activation succeeds.</summary>
internal sealed record SettingsDraft(string Period, string Currency, int WidthDip, bool StartWithWindows,
    string ParametersJson, string Source, string ManifestJson, string TimeZone, string Weekdays,
    string ShiftsJson, string DateOverridesJson)
{
    public JsonObject BuildCandidate(JsonObject current)
    {
        var parameters = Object(ParametersJson, "规则参数");
        var manifest = Object(ManifestJson, "规则清单");
        var calendar = BuildCalendar(current["calendar"] as JsonObject, TimeZone, Weekdays, ShiftsJson, DateOverridesJson);
        if (Period is not ("day" or "month")) throw new InvalidDataException("请选择今日或本月收入。");
        if (WidthDip is < 180 or > 360) throw new InvalidDataException("任务栏宽度应为 180 至 360 DIP。");

        var display = current["display"]?.DeepClone() as JsonObject ?? new JsonObject();
        display["period"] = Period;
        display["currency"] = Currency.Trim().ToUpperInvariant();
        display["taskbarWidthDip"] = WidthDip;
        var rule = new JsonObject();
        if (manifest["id"] is JsonValue id) rule["id"] = id.DeepClone();
        if (manifest["version"] is JsonValue version) rule["version"] = version.DeepClone();
        return new JsonObject
        {
            ["expectedRevision"] = current["revision"]?.GetValue<long>() ?? 0,
            ["parameters"] = parameters,
            ["source"] = Source,
            ["manifest"] = manifest,
            ["rule"] = rule,
            ["display"] = display,
            ["calendar"] = calendar,
            ["startWithWindows"] = StartWithWindows
        };
    }

    public static JsonObject BuildCalendar(JsonObject? current, string timeZone, string weekdayText,
        string shiftsText, string datesText)
    {
        var shifts = Parse(shiftsText) as JsonArray
            ?? throw new InvalidDataException("计薪时段必须是 JSON 数组。");
        var dates = Object(datesText, "日期覆盖");
        var weekdays = weekdayText.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(value => int.TryParse(value, System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out var day) ? day : 0).ToArray();
        if (weekdays.Length == 0 || weekdays.Any(day => day is < 1 or > 7)
            || weekdays.Distinct().Count() != weekdays.Length)
            throw new InvalidDataException("计薪工作日应为不重复的 1 至 7，并用英文逗号分隔。");
        var calendar = current?.DeepClone() as JsonObject ?? new JsonObject();
        calendar["timeZone"] = timeZone.Trim();
        calendar["weekdays"] = new JsonArray(weekdays.Select(day => JsonValue.Create(day)).ToArray<JsonNode?>());
        calendar["shifts"] = shifts;
        calendar["dateOverrides"] = dates;
        return calendar;
    }

    private static JsonNode? Parse(string value) => JsonNode.Parse(value,
        documentOptions: new JsonDocumentOptions { MaxDepth = 16 });

    private static JsonObject Object(string value, string label) => Parse(value) as JsonObject
        ?? throw new InvalidDataException($"{label}必须是 JSON 对象。");
}

internal sealed record RuleCatalogItem(string Id, string Name, string Source, JsonObject Manifest,
    JsonObject DefaultParameters)
{
    public static IReadOnlyList<RuleCatalogItem> Parse(JsonObject catalog)
    {
        var rules = catalog["rules"] as JsonArray
            ?? throw new InvalidDataException("宿主规则目录缺少规则列表。");
        if (rules.Count > 32) throw new InvalidDataException("宿主规则目录过大。");
        return rules.Select(node =>
        {
            var rule = node as JsonObject ?? throw new InvalidDataException("规则目录项目不合法。");
            return new RuleCatalogItem(
                rule["id"]!.GetValue<string>(), rule["name"]!.GetValue<string>(),
                rule["source"]!.GetValue<string>(),
                rule["manifest"]!.DeepClone().AsObject(),
                rule["defaultParameters"]!.DeepClone().AsObject());
        }).ToArray();
    }
}

internal static class UiMessages
{
    public static string Error(string code, string fallback) => code switch
    {
        "PARAMETER_INVALID" => "规则参数不符合清单定义。请检查参数值或清单中的参数要求。",
        "MANIFEST_INVALID" => "规则清单格式不正确。请检查 API 版本、入口和参数定义。",
        "CONFIG_CONFLICT" => "配置已在其他窗口更新。请重新打开设置后再应用。",
        "CONFIG_INVALID" => "设置内容不合法。请检查时区、日历、币种和任务栏宽度。",
        "SCRIPT_TIMEOUT" => "计算超过时间限制。请检查脚本中的循环或递归。",
        "SCRIPT_LIMIT" => "脚本使用的资源超过限制。请简化脚本后重试。",
        "CURRENCY_MISMATCH" => "脚本返回的币种与设置不一致。请统一币种后重试。",
        "MODULE_NOT_ALLOWED" => "脚本引用了不支持的模块。请只使用 @workfor/sdk。",
        _ => fallback
    };
}
