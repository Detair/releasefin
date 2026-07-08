using Jellyfin.Plugin.ReleaseFin.Core;
using Xunit;

namespace Jellyfin.Plugin.ReleaseFin.Tests;

public class MovieOrderKeyTests
{
    [Fact]
    public void PremiereDate_WinsOverYearAndSortName()
    {
        var earlier = new MovieOrderKey(new DateTime(2001, 5, 1), 2003, "Zebra");
        var later = new MovieOrderKey(new DateTime(2002, 5, 1), 2001, "Alpha");
        Assert.True(earlier.CompareTo(later) < 0);
    }

    [Fact]
    public void MissingPremiereDate_SortsAfterDated()
    {
        var dated = new MovieOrderKey(new DateTime(2099, 1, 1), null, "Alpha");
        var undated = new MovieOrderKey(null, 1950, "Alpha");
        Assert.True(dated.CompareTo(undated) < 0);
    }

    [Fact]
    public void ProductionYear_BreaksPremiereTies()
    {
        var older = new MovieOrderKey(null, 2001, "Zebra");
        var newer = new MovieOrderKey(null, 2002, "Alpha");
        Assert.True(older.CompareTo(newer) < 0);
    }

    [Fact]
    public void MissingYear_SortsAfterDated()
    {
        var dated = new MovieOrderKey(null, 2999, "Zebra");
        var undated = new MovieOrderKey(null, null, "Alpha");
        Assert.True(dated.CompareTo(undated) < 0);
    }

    [Fact]
    public void SortName_IsTheFinalFallback_NullAsEmpty()
    {
        var alpha = new MovieOrderKey(null, null, "Alpha");
        var zebra = new MovieOrderKey(null, null, "Zebra");
        var unnamed = new MovieOrderKey(null, null, null);
        Assert.True(alpha.CompareTo(zebra) < 0);
        Assert.True(unnamed.CompareTo(alpha) < 0); // null sorts like the empty string
        Assert.Equal(0, alpha.CompareTo(alpha));
    }

    [Fact]
    public void OrderBy_ProducesPremiereOrder()
    {
        MovieOrderKey[] keys =
        [
            new(null, null, "B"),
            new(new DateTime(2002, 1, 1), 2002, "Two"),
            new(null, 2003, "Three"),
            new(new DateTime(2001, 1, 1), 2001, "One"),
        ];
        var sorted = keys.OrderBy(k => k).Select(k => k.SortName ?? string.Empty).ToArray();
        Assert.Equal(["One", "Two", "Three", "B"], sorted);
    }
}
