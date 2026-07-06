using Jellyfin.Plugin.ReleaseFin.Core;
using Xunit;

namespace Jellyfin.Plugin.ReleaseFin.Tests;

public class EpisodeKeyTests
{
    [Fact]
    public void SortsAiredOrder_SeasonThenEpisode()
    {
        EpisodeKey[] keys = [new(2, 1), new(1, 10), new(1, 2)];
        var sorted = keys.OrderBy(k => k).ToArray();
        Assert.Equal([new(1, 2), new(1, 10), new(2, 1)], sorted);
    }

    [Fact]
    public void Season0_IsSpecial()
    {
        Assert.True(new EpisodeKey(0, 1).IsSpecial);
        Assert.False(new EpisodeKey(1, 1).IsSpecial);
    }

    [Fact]
    public void TryCreate_RejectsMissingNumbers()
    {
        Assert.False(EpisodeKey.TryCreate(null, 1, out _));
        Assert.False(EpisodeKey.TryCreate(1, null, out _));
        Assert.True(EpisodeKey.TryCreate(1, 2, out var key));
        Assert.Equal(new EpisodeKey(1, 2), key);
    }

    [Fact]
    public void IsAtOrBefore_ComparesInAiredOrder()
    {
        Assert.True(new EpisodeKey(1, 5).IsAtOrBefore(new EpisodeKey(1, 5)));
        Assert.True(new EpisodeKey(1, 5).IsAtOrBefore(new EpisodeKey(2, 1)));
        Assert.False(new EpisodeKey(2, 1).IsAtOrBefore(new EpisodeKey(1, 5)));
    }
}
