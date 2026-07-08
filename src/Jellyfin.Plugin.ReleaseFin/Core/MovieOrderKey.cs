namespace Jellyfin.Plugin.ReleaseFin.Core;

/// <summary>Premiere-order sort key for movies in a scheduled collection: premiere date, then
/// production year, then sort name — unknown dates/years sort last so undated movies drip after
/// dated ones, and the sort-name fallback guarantees every movie gets a stable ordinal. Movies
/// map to EpisodeKey(1, ordinal) by position under this ordering, so inserting an older movie
/// later shifts ordinals; acceptable because already-released movies stay untagged and the
/// frontier only classifies new imports.</summary>
public readonly record struct MovieOrderKey(DateTime? PremiereDate, int? ProductionYear, string? SortName)
    : IComparable<MovieOrderKey>
{
    public int CompareTo(MovieOrderKey other)
    {
        var byDate = (PremiereDate ?? DateTime.MaxValue).CompareTo(other.PremiereDate ?? DateTime.MaxValue);
        if (byDate != 0)
        {
            return byDate;
        }

        var byYear = (ProductionYear ?? int.MaxValue).CompareTo(other.ProductionYear ?? int.MaxValue);
        if (byYear != 0)
        {
            return byYear;
        }

        return string.CompareOrdinal(SortName ?? string.Empty, other.SortName ?? string.Empty);
    }
}
