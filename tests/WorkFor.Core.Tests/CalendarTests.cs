using System.Text.Json;
using WorkFor.Core;
using Xunit;

namespace WorkFor.Core.Tests;

public class CalendarTests
{
    private static JsonElement Parameters => JsonSerializer.SerializeToElement(new { hourlyRate = "16.00" });
    private static WorkForContext Build(string instant, CalendarSettings? settings = null) => CalendarContextBuilder.Build(DateTimeOffset.Parse(instant), settings ?? CalendarSettings.Default, CurrencyCatalog.Get("CNY"), Parameters);

    [Fact]
    public void NormalizesFullMonthAndDoesNotCountLunch()
    {
        var morning = Build("2026-10-30T11:00:00+08:00");
        Assert.Equal(22 * 8 * 3_600_000L, morning.Periods.Month.ScheduledMs);
        Assert.Equal(2 * 3_600_000, morning.Periods.Day.ElapsedScheduledMs);
        Assert.Equal(21 * 8 * 3_600_000L, morning.Periods.Month.ScheduledBeforeDayMs);
        var lunch = Build("2026-10-30T12:45:00+08:00");
        Assert.Equal(3 * 3_600_000, lunch.Periods.Day.ElapsedScheduledMs);
        ContextValidator.Validate(morning);
        ContextValidator.Validate(lunch);
    }

    [Fact]
    public void AbsoluteTimeRebuildRecoversAfterSleepWithoutAccumulation()
    {
        Assert.Equal(1 * 3_600_000, Build("2026-10-30T10:00:00+08:00").Periods.Day.ElapsedScheduledMs);
        Assert.Equal(8 * 3_600_000, Build("2026-10-30T22:00:00+08:00").Periods.Day.ElapsedScheduledMs);
        Assert.Equal(0, Build("2026-10-31T22:00:00+08:00").Periods.Day.ElapsedScheduledMs);
    }

    [Fact]
    public void DateOverrideAndOverlapAreAppliedOnce()
    {
        var settings = CalendarSettings.Default with { DateOverrides = new Dictionary<DateOnly, IReadOnlyList<Shift>> { [new(2026, 10, 30)] = [new(new(9, 0), new(14, 0)), new(new(12, 0), new(16, 0))] } };
        var ctx = Build("2026-10-30T18:00:00+08:00", settings);
        Assert.Equal(7 * 3_600_000, ctx.Periods.Day.ScheduledMs);
        Assert.Equal(ctx.Periods.Day.ScheduledMs, ctx.Periods.Day.ElapsedScheduledMs);
    }

    [Fact]
    public void OvernightShiftCrossesMonthBoundary()
    {
        var settings = new CalendarSettings("Asia/Shanghai", [1,2,3,4,5,6,7], [new(new(22,0), new(2,0))]);
        var ctx = Build("2026-11-01T01:00:00+08:00", settings);
        Assert.Equal(3_600_000, ctx.Periods.Day.ElapsedScheduledMs);
        Assert.Equal(4 * 3_600_000, ctx.Periods.Day.ScheduledMs);
        ContextValidator.Validate(ctx);
    }

    [Fact]
    public void DstDayUsesActualMidnightBoundaries()
    {
        var settings = new CalendarSettings("America/New_York", [7], [new(new(0, 0), new(4, 0))]);
        var ctx = Build("2026-03-08T04:00:00-04:00", settings);
        Assert.Equal(23 * 3_600_000, ctx.Periods.Day.EndMs - ctx.Periods.Day.StartMs);
        Assert.Equal(3 * 3_600_000, ctx.Periods.Day.ScheduledMs);
        ContextValidator.Validate(ctx);
    }

    [Fact]
    public void DstNonexistentShiftTimeIsRejected()
    {
        var settings = new CalendarSettings("America/New_York", [], [], new Dictionary<DateOnly, IReadOnlyList<Shift>> { [new(2026,3,8)] = [new(new(2,30), new(4,0))] });
        Assert.Throws<ArgumentException>(() => Build("2026-03-08T04:00:00-04:00", settings));
    }

    [Fact]
    public void ExplicitAllDayCalendarCoversDstDayAndKeepsDayPartitions()
    {
        var settings = new CalendarSettings("America/New_York", [1,2,3,4,5,6,7], [new(TimeOnly.MinValue, TimeOnly.MinValue, true)]);
        var ctx = Build("2026-11-01T12:00:00-05:00", settings);
        Assert.Equal(25 * 3_600_000, ctx.Periods.Day.ScheduledMs);
        Assert.Equal(30, ctx.Schedule.Segments.Count);
        ContextValidator.Validate(ctx);
    }

    [Theory]
    [InlineData("12860", "CNY", "¥128.60")]
    [InlineData("999999999999999999999999", "CNY", "¥9,999,999,999,999,999,999,999.99")]
    [InlineData("-3", "KWD", "-KWD 0.003")]
    [InlineData("12860", "JPY", "JP¥12,860")]
    public void FormatsExactIntegerMoney(string value, string code, string expected) => Assert.Equal(expected, MoneyFormatter.Format(value, CurrencyCatalog.Get(code)));

    [Theory]
    [InlineData("01")][InlineData("-0")][InlineData("1.00")][InlineData("1e6")][InlineData("1000000000000000000000000")]
    public void RejectsNonCanonicalMoney(string value) => Assert.False(MoneyFormatter.IsCanonical(value));
}
