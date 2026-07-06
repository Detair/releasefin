using Cronos;

namespace Jellyfin.Plugin.ReleaseFin.Core;

/// <summary>Cron evaluation. Instants are UTC; the time zone parameter defines what wall-clock
/// time the cron fields refer to (production uses TimeZoneInfo.Local).</summary>
public static class ScheduleCalculator
{
    public static bool IsValid(string expression)
    {
        try
        {
            CronExpression.Parse(expression);
            return true;
        }
        catch (CronFormatException)
        {
            return false;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    /// <summary>Occurrences in (lastRunUtc, nowUtc]. Missed occurrences all count ("accumulate freely").</summary>
    public static int CountDueTicks(string expression, DateTime lastRunUtc, DateTime nowUtc, TimeZoneInfo zone)
    {
        if (nowUtc <= lastRunUtc)
        {
            return 0;
        }

        return CronExpression.Parse(expression)
            .GetOccurrences(lastRunUtc, nowUtc, zone, fromInclusive: false, toInclusive: true)
            .Count();
    }

    public static DateTime? NextOccurrenceUtc(string expression, DateTime nowUtc, TimeZoneInfo zone) =>
        CronExpression.Parse(expression).GetNextOccurrence(nowUtc, zone);
}
