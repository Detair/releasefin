using Jellyfin.Plugin.ReleaseFin.Core;
using Xunit;

namespace Jellyfin.Plugin.ReleaseFin.Tests;

public class ScheduleCalculatorTests
{
    private static DateTime Utc(int y, int mo, int d, int h, int mi) =>
        new(y, mo, d, h, mi, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData("0 16 * * *", true)]
    [InlineData("0 16 * * 1,3,5", true)]
    [InlineData("not a cron", false)]
    [InlineData("", false)]
    public void IsValid_ChecksCronSyntax(string expr, bool expected) =>
        Assert.Equal(expected, ScheduleCalculator.IsValid(expr));

    [Fact]
    public void CountDueTicks_ZeroWhenNoOccurrenceElapsed() =>
        Assert.Equal(0, ScheduleCalculator.CountDueTicks(
            "0 16 * * *", Utc(2026, 7, 6, 17, 0), Utc(2026, 7, 7, 15, 0), TimeZoneInfo.Utc));

    [Fact]
    public void CountDueTicks_AccumulatesMissedDays() =>
        // 3 days offline => 3 due ticks ("accumulate freely")
        Assert.Equal(3, ScheduleCalculator.CountDueTicks(
            "0 16 * * *", Utc(2026, 7, 3, 17, 0), Utc(2026, 7, 6, 17, 0), TimeZoneInfo.Utc));

    [Fact]
    public void CountDueTicks_BoundaryIsExclusiveOfLastRun_InclusiveOfNow() =>
        // lastRun exactly at an occurrence must not double-count it; now at an occurrence counts.
        Assert.Equal(1, ScheduleCalculator.CountDueTicks(
            "0 16 * * *", Utc(2026, 7, 5, 16, 0), Utc(2026, 7, 6, 16, 0), TimeZoneInfo.Utc));

    [Fact]
    public void NextOccurrence_ReturnsUpcomingInstant() =>
        Assert.Equal(
            Utc(2026, 7, 6, 16, 0),
            ScheduleCalculator.NextOccurrenceUtc("0 16 * * *", Utc(2026, 7, 6, 12, 0), TimeZoneInfo.Utc));
}
