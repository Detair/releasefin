namespace Jellyfin.Plugin.ReleaseFin.Configuration;

/// <summary>One drip-release assignment: a series, the restricted users, and the cadence.</summary>
public class ReleaseSchedule
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string Name { get; set; } = string.Empty;

    public Guid SeriesId { get; set; }

    public Guid[] UserIds { get; set; } = [];

    /// <summary>5-field cron, evaluated in the server's local time zone.</summary>
    public string CronExpression { get; set; } = "0 16 * * *";

    public int EpisodesPerTick { get; set; } = 1;

    /// <summary>Episodes at or before S(InitialSeason)E(InitialEpisode) start released. Null = everything locked.</summary>
    public int? InitialSeason { get; set; }

    public int? InitialEpisode { get; set; }

    public bool Enabled { get; set; } = true;

    /// <summary>Highest episode ever released for this schedule (persisted high-water mark);
    /// classifies new imports. Null = nothing released yet.</summary>
    public int? ReleasedUpToSeason { get; set; }

    public int? ReleasedUpToEpisode { get; set; }

    /// <summary>Last time the scheduler evaluated this schedule; cron occurrences after this are due.</summary>
    public DateTime LastRunUtc { get; set; } = DateTime.UtcNow;
}
