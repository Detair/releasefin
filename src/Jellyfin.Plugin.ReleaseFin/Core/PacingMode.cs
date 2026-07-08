namespace Jellyfin.Plugin.ReleaseFin.Core;

/// <summary>How a schedule paces releases when ticks come due. Serialized by name in both the
/// plugin XML configuration and the JSON API (Jellyfin serializes enums as strings).</summary>
public enum PacingMode
{
    /// <summary>Every due tick releases; missed ticks accumulate (original behavior).</summary>
    Accumulate = 0,

    /// <summary>A due tick releases only when all assigned users have played every episode the
    /// plugin has released so far; missed ticks never stack.</summary>
    WatchGated = 1,

    /// <summary>Due ticks release only while released-but-unplayed episodes stay under the cap.</summary>
    BacklogCap = 2,
}
