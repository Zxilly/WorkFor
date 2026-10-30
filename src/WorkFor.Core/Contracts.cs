using System.Text.Json;
using System.Text.Json.Serialization;

namespace WorkFor.Core;

public sealed record CurrencyInfo(string Code, int FractionDigits);
public sealed record Segment(long StartMs, long EndMs);
public sealed record EvaluationNow(long EpochMs, string TimeZone, string LocalDate, string LocalTime, int IsoWeekday, int UtcOffsetMinutes);
public sealed record PeriodMetrics(string Kind, string Key, long StartMs, long EndMs, long ScheduledMs, long ElapsedScheduledMs, long ElapsedWallMs, [property:JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? ScheduledBeforeDayMs = null);
public sealed record ContextPeriods(PeriodMetrics Day, PeriodMetrics Month);
public sealed record EvaluationSchedule(IReadOnlyList<Segment> Segments);
public sealed record WorkForContext(int ApiVersion, string EvaluationId, long ConfigRevision, long CalendarRevision, EvaluationNow Now, string ActivePeriod, CurrencyInfo Currency, JsonElement Parameters, ContextPeriods Periods, EvaluationSchedule Schedule);
public sealed record RuleMetric(string Label, string Value);
public sealed record RuleResult(int ApiVersion, string Currency, string AmountMinor, [property:JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] string? RatePerHourMinor = null, [property:JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] string? Note = null, [property:JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<RuleMetric>? Metrics = null);
public sealed record EvaluationError(string Code, string Message, string? File = null, int? Line = null, int? Column = null, bool Retryable = false);
public sealed record EvaluationOutcome(RuleResult? Result, EvaluationError? Error)
{
    public bool Success => Result is not null && Error is null;
    public static EvaluationOutcome Failed(string code, string message, bool retryable = false) => new(null, new(code, message, Retryable: retryable));
}
public static class WorkForJson
{
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web)
    {
        MaxDepth = 16,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = false
    };
}
