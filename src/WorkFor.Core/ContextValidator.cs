using System.Globalization;
using System.Text.Json;

namespace WorkFor.Core;

public static class ContextValidator
{
    private const long MaxSafe = 9_007_199_254_740_991;
    public static void Validate(WorkForContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.ApiVersion != 1) throw new ArgumentException("Unsupported context API version.");
        if (context.ActivePeriod is not ("day" or "month")) throw new ArgumentException("Invalid active period.");
        if (context.EvaluationId is null || context.EvaluationId.Length is < 1 or > 40 || context.EvaluationId.Any(c => c is < '0' or > '9')) throw new ArgumentException("Invalid evaluation ID.");
        if (context.ConfigRevision < 0 || context.CalendarRevision < 0 || context.ConfigRevision > MaxSafe || context.CalendarRevision > MaxSafe) throw new ArgumentException("Invalid context revision.");
        if (context.Now is null || context.Currency is null || context.Periods?.Day is null || context.Periods.Month is null || context.Schedule?.Segments is null) throw new ArgumentException("Context is missing required objects.");
        CurrencyCatalog.Validate(context.Currency);
        if (context.Parameters.ValueKind != JsonValueKind.Object) throw new ArgumentException("Parameters must be an object.");
        ValidateJson(context.Parameters, 0);
        var now = context.Now;
        Safe(now.EpochMs);
        var zone = TimeZoneInfo.FindSystemTimeZoneById(now.TimeZone);
        var local = TimeZoneInfo.ConvertTime(DateTimeOffset.FromUnixTimeMilliseconds(now.EpochMs), zone);
        var weekday = local.DayOfWeek == DayOfWeek.Sunday ? 7 : (int)local.DayOfWeek;
        if (now.LocalDate != local.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) || now.LocalTime != local.ToString("HH:mm:ss", CultureInfo.InvariantCulture) || now.IsoWeekday != weekday || now.UtcOffsetMinutes != (int)local.Offset.TotalMinutes) throw new ArgumentException("Context local time does not match its timestamp and zone.");
        var segments = context.Schedule.Segments;
        if (segments.Count > 512) throw new ArgumentException("Schedule exceeds 512 segments.");
        long previous = long.MinValue;
        foreach (var s in segments)
        {
            if (s is null) throw new ArgumentException("Null schedule segment.");
            Safe(s.StartMs); Safe(s.EndMs);
            if (s.EndMs <= s.StartMs || s.StartMs < previous) throw new ArgumentException("Schedule must be sorted and non-overlapping.");
            previous = s.EndMs;
        }
        var day = context.Periods.Day; var month = context.Periods.Month;
        if (day.Kind != "day" || month.Kind != "month" || day.Key != now.LocalDate || month.Key != now.LocalDate[..7] || day.StartMs < month.StartMs || day.EndMs > month.EndMs) throw new ArgumentException("Invalid period identity.");
        foreach (var p in new[] { day, month })
        {
            Safe(p.StartMs); Safe(p.EndMs); Safe(p.ScheduledMs); Safe(p.ElapsedScheduledMs); Safe(p.ElapsedWallMs);
            if (p.EndMs <= p.StartMs || p.ScheduledMs < 0 || p.ElapsedScheduledMs < 0 || p.ElapsedScheduledMs > p.ScheduledMs || p.ElapsedWallMs != Math.Clamp(now.EpochMs - p.StartMs, 0, p.EndMs - p.StartMs)) throw new ArgumentException("Invalid period values.");
            if (p.ScheduledMs != CalendarContextBuilder.SumIntersection(segments, p.StartMs, p.EndMs) || p.ElapsedScheduledMs != CalendarContextBuilder.SumIntersection(segments, p.StartMs, Math.Clamp(now.EpochMs, p.StartMs, p.EndMs))) throw new ArgumentException("Period totals do not match schedule.");
        }
        if (segments.Any(s => s.StartMs < month.StartMs || s.EndMs > month.EndMs) || month.ScheduledBeforeDayMs != CalendarContextBuilder.SumIntersection(segments, month.StartMs, day.StartMs)) throw new ArgumentException("Invalid monthly schedule.");
    }

    public static void ValidateJson(JsonElement item, int depth = 0)
    {
        if (depth > 16) throw new ArgumentException("JSON nesting exceeds limit.");
        if (item.ValueKind == JsonValueKind.Object)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in item.EnumerateObject())
            {
                if (!seen.Add(property.Name)) throw new ArgumentException("Duplicate JSON field.");
                ValidateJson(property.Value, depth + 1);
            }
        }
        else if (item.ValueKind == JsonValueKind.Array)
        {
            foreach (var value in item.EnumerateArray()) ValidateJson(value, depth + 1);
        }
        else if (item.ValueKind == JsonValueKind.Number)
        {
            if (!item.TryGetDouble(out var value) || !double.IsFinite(value) || Math.Abs(value) > MaxSafe) throw new ArgumentException("JSON number outside safe range.");
        }
    }
    private static void Safe(long value) { if (value < -MaxSafe || value > MaxSafe) throw new ArgumentException("Unsafe integer."); }
}
