using System.Globalization;
using System.Text.Json;

namespace WorkFor.Core;

public sealed record Shift(TimeOnly Start, TimeOnly End, bool EndNextDay = false);
public sealed record CalendarSettings(string TimeZone, IReadOnlyList<int> Weekdays, IReadOnlyList<Shift> Shifts, IReadOnlyDictionary<DateOnly, IReadOnlyList<Shift>>? DateOverrides = null)
{
    public static CalendarSettings Default { get; } = new("Asia/Shanghai", [1, 2, 3, 4, 5], [new(new(9, 0), new(12, 0)), new(new(13, 0), new(18, 0))]);
}

/// <summary>Builds a complete normalized current-month schedule and evaluates absolute-time metrics.</summary>
public static class CalendarContextBuilder
{
    public static WorkForContext Build(DateTimeOffset now, CalendarSettings settings, CurrencyInfo currency, JsonElement parameters, string activePeriod = "day", string evaluationId = "1", long configRevision = 1, long calendarRevision = 1)
    {
        if (activePeriod is not ("day" or "month")) throw new ArgumentException("activePeriod must be day or month.");
        CurrencyCatalog.Validate(currency);
        if (settings.Weekdays.Any(d => d < 1 || d > 7)) throw new ArgumentException("Invalid ISO weekday.");
        var zone = TimeZoneInfo.FindSystemTimeZoneById(settings.TimeZone);
        var localNow = TimeZoneInfo.ConvertTime(now, zone);
        var date = DateOnly.FromDateTime(localNow.DateTime);
        var first = new DateOnly(date.Year, date.Month, 1);
        var next = first.AddMonths(1);
        var monthStart = At(first, TimeOnly.MinValue, zone);
        var monthEnd = At(next, TimeOnly.MinValue, zone);
        var dayStart = At(date, TimeOnly.MinValue, zone);
        var dayEnd = At(date.AddDays(1), TimeOnly.MinValue, zone);
        var all = new List<Segment>();
        // Include previous day's overnight shift at the month boundary.
        for (var d = first.AddDays(-1); d < next; d = d.AddDays(1))
        {
            IReadOnlyList<Shift> shifts = settings.DateOverrides?.TryGetValue(d, out var overridden) == true
                ? overridden : settings.Weekdays.Contains(IsoDay(d.DayOfWeek)) ? settings.Shifts : [];
            foreach (var shift in shifts)
            {
                if (shift.Start == shift.End && !shift.EndNextDay) throw new ArgumentException("Equal shift endpoints require explicit EndNextDay for an all-day shift.");
                if (shift.EndNextDay && shift.End > shift.Start) throw new ArgumentException("A shift cannot exceed 24 local hours.");
                var endDate = shift.EndNextDay || shift.End < shift.Start ? d.AddDays(1) : d;
                var start = Math.Max(At(d, shift.Start, zone), monthStart);
                var end = Math.Min(At(endDate, shift.End, zone), monthEnd);
                if (end > start) all.Add(new(start, end));
            }
        }
        var merged = Merge(all);
        var segments = new List<Segment>();
        // Preserve local-day partitions, including 23/25-hour DST days.
        for (var d = first; d < next; d = d.AddDays(1))
        {
            var start = At(d, TimeOnly.MinValue, zone);
            var end = At(d.AddDays(1), TimeOnly.MinValue, zone);
            foreach (var s in merged)
            {
                var clippedStart = Math.Max(start, s.StartMs);
                var clippedEnd = Math.Min(end, s.EndMs);
                if (clippedEnd > clippedStart) segments.Add(new(clippedStart, clippedEnd));
            }
        }
        if (segments.Count > 512) throw new ArgumentException("Schedule exceeds 512 segments.");
        var timestamp = now.ToUnixTimeMilliseconds();
        var day = Metrics("day", date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), dayStart, dayEnd, timestamp, segments);
        var month = Metrics("month", first.ToString("yyyy-MM", CultureInfo.InvariantCulture), monthStart, monthEnd, timestamp, segments) with
        { ScheduledBeforeDayMs = SumIntersection(segments, monthStart, dayStart) };
        return new(1, evaluationId, configRevision, calendarRevision,
            new(timestamp, settings.TimeZone, date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), localNow.ToString("HH:mm:ss", CultureInfo.InvariantCulture), IsoDay(localNow.DayOfWeek), (int)localNow.Offset.TotalMinutes),
            activePeriod, currency, parameters.Clone(), new(day, month), new(segments));
    }

    public static long SumIntersection(IEnumerable<Segment> segments, long start, long end) => segments.Aggregate(0L,
        (sum, s) => checked(sum + Math.Max(0, Math.Min(end, s.EndMs) - Math.Max(start, s.StartMs))));

    private static PeriodMetrics Metrics(string kind, string key, long start, long end, long now, IReadOnlyList<Segment> segments) =>
        new(kind, key, start, end, SumIntersection(segments, start, end), SumIntersection(segments, start, Math.Clamp(now, start, end)), Math.Clamp(now - start, 0, end - start));

    private static int IsoDay(DayOfWeek d) => d == DayOfWeek.Sunday ? 7 : (int)d;
    private static long At(DateOnly date, TimeOnly time, TimeZoneInfo zone)
    {
        var local = DateTime.SpecifyKind(date.ToDateTime(time), DateTimeKind.Unspecified);
        if (zone.IsInvalidTime(local)) throw new ArgumentException($"Nonexistent local time: {date:yyyy-MM-dd} {time:HH:mm} ({zone.Id}).");
        // Larger offset corresponds to the first occurrence during a fall-back transition.
        var offset = zone.IsAmbiguousTime(local) ? zone.GetAmbiguousTimeOffsets(local).Max() : zone.GetUtcOffset(local);
        return new DateTimeOffset(local, offset).ToUnixTimeMilliseconds();
    }
    private static IReadOnlyList<Segment> Merge(List<Segment> source)
    {
        var merged = new List<Segment>();
        foreach (var s in source.OrderBy(x => x.StartMs).ThenBy(x => x.EndMs))
        {
            if (merged.Count == 0 || s.StartMs > merged[^1].EndMs) merged.Add(s);
            else merged[^1] = merged[^1] with { EndMs = Math.Max(s.EndMs, merged[^1].EndMs) };
        }
        return merged;
    }
}
