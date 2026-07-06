namespace Jellyfin.Plugin.ReleaseFin.Core;

/// <summary>Aired-order position of an episode (season, then episode number).</summary>
public readonly record struct EpisodeKey(int Season, int Episode) : IComparable<EpisodeKey>
{
    public bool IsSpecial => Season == 0;

    public int CompareTo(EpisodeKey other) =>
        Season != other.Season ? Season.CompareTo(other.Season) : Episode.CompareTo(other.Episode);

    public bool IsAtOrBefore(EpisodeKey other) => CompareTo(other) <= 0;

    /// <summary>Episodes without both numbers cannot be ordered and are excluded from drip logic.</summary>
    public static bool TryCreate(int? season, int? episode, out EpisodeKey key)
    {
        if (season is null || episode is null)
        {
            key = default;
            return false;
        }

        key = new EpisodeKey(season.Value, episode.Value);
        return true;
    }
}
