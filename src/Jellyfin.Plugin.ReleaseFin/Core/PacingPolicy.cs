namespace Jellyfin.Plugin.ReleaseFin.Core;

/// <summary>Pure pacing decision: how many episodes a batch of due ticks may release, given how
/// many previously released episodes are still unplayed (by-all-assigned-users semantics; the
/// caller computes that count).</summary>
public static class PacingPolicy
{
    public static int ComputeReleaseCount(
        PacingMode mode, int episodesPerTick, int dueTicks, int unplayedReleasedCount, int backlogCap)
    {
        if (dueTicks <= 0)
        {
            return 0;
        }

        return mode switch
        {
            PacingMode.WatchGated => unplayedReleasedCount == 0 ? episodesPerTick : 0,
            PacingMode.BacklogCap =>
                Math.Max(0, Math.Min(dueTicks * episodesPerTick, backlogCap - unplayedReleasedCount)),
            _ => dueTicks * episodesPerTick,
        };
    }
}
