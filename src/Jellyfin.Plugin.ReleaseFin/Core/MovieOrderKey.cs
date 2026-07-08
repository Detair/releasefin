namespace Jellyfin.Plugin.ReleaseFin.Core;

/// <summary>Premiere-order sort key for movies in a scheduled collection: premiere date, then
/// production year, then sort name — unknown values sort last on every field, so undated/unnamed
/// movies drip after dated ones and the sort-name fallback still gives every movie a stable
/// ordinal. Movies
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

        // Null sorts last here too, consistent with the date/year fields above.
        if (SortName is null || other.SortName is null)
        {
            return (SortName is null ? 1 : 0) - (other.SortName is null ? 1 : 0);
        }

        return string.CompareOrdinal(SortName, other.SortName);
    }
}
