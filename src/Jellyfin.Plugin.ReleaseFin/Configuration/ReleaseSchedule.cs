using Jellyfin.Plugin.ReleaseFin.Core;

namespace Jellyfin.Plugin.ReleaseFin.Configuration;

/// <summary>One drip-release assignment: a series or movie collection, the restricted users,
/// and the cadence.</summary>
public class ReleaseSchedule
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string Name { get; set; } = string.Empty;

    /// <summary>Id of the scheduled series (Kind=Series) or collection/BoxSet (Kind=Collection).
    /// Keeps its historical name for XML-config compatibility.</summary>
    public Guid SeriesId { get; set; }

    /// <summary>Whether SeriesId names a TV series or a movie collection.</summary>
    public ScheduleKind Kind { get; set; } = ScheduleKind.Series;

    public Guid[] UserIds { get; set; } = [];

    /// <summary>5-field cron, evaluated in the server's local time zone.</summary>
    public string CronExpression { get; set; } = "0 16 * * *";

    public int EpisodesPerTick { get; set; } = 1;

    public PacingMode Pacing { get; set; } = PacingMode.Accumulate;

    /// <summary>Max released-but-unplayed episodes; only used when Pacing is BacklogCap.</summary>
    public int BacklogCap { get; set; } = 2;

    /// <summary>Episodes at or before S(InitialSeason)E(InitialEpisode) start released. Null = everything locked.</summary>
    public int? InitialSeason { get; set; }

    public int? InitialEpisode { get; set; }

    public bool Enabled { get; set; } = true;

    /// <summary>Series kind only: when the next unreleased episode starts a later season than the
    /// release frontier, set SeasonPaused instead of releasing across the boundary.</summary>
    public bool PauseAtSeasonEnd { get; set; }

    /// <summary>State flag: the schedule hit a season boundary and waits for a manual Resume.
    /// Distinct from Enabled (manual disable); reset on edit (fresh apply = fresh state).</summary>
    public bool SeasonPaused { get; set; }

    /// <summary>Highest episode ever released for this schedule (persisted high-water mark);
    /// classifies new imports. Null = nothing released yet.</summary>
    public int? ReleasedUpToSeason { get; set; }

    public int? ReleasedUpToEpisode { get; set; }

    /// <summary>Last time the scheduler evaluated this schedule; cron occurrences after this are due.</summary>
    public DateTime LastRunUtc { get; set; } = DateTime.UtcNow;
}
