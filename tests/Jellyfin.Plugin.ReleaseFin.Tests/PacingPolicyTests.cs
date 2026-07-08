using Jellyfin.Plugin.ReleaseFin.Core;
using Xunit;

namespace Jellyfin.Plugin.ReleaseFin.Tests;

public class PacingPolicyTests
{
    [Theory]
    [InlineData(PacingMode.Accumulate)]
    [InlineData(PacingMode.WatchGated)]
    [InlineData(PacingMode.BacklogCap)]
    public void NoDueTicks_ReleasesNothing_InEveryMode(PacingMode mode)
    {
        Assert.Equal(0, PacingPolicy.ComputeReleaseCount(mode, episodesPerTick: 2, dueTicks: 0, unplayedReleasedCount: 0, backlogCap: 5));
        Assert.Equal(0, PacingPolicy.ComputeReleaseCount(mode, episodesPerTick: 2, dueTicks: -1, unplayedReleasedCount: 0, backlogCap: 5));
    }

    [Fact]
    public void Accumulate_ReleasesEveryDueTick()
    {
        Assert.Equal(6, PacingPolicy.ComputeReleaseCount(PacingMode.Accumulate, episodesPerTick: 2, dueTicks: 3, unplayedReleasedCount: 0, backlogCap: 1));
    }

    [Fact]
    public void Accumulate_IgnoresUnplayedBacklog()
    {
        Assert.Equal(2, PacingPolicy.ComputeReleaseCount(PacingMode.Accumulate, episodesPerTick: 2, dueTicks: 1, unplayedReleasedCount: 99, backlogCap: 1));
    }

    [Fact]
    public void WatchGated_ReleasesOneTick_WhenEverythingPlayed()
    {
        Assert.Equal(2, PacingPolicy.ComputeReleaseCount(PacingMode.WatchGated, episodesPerTick: 2, dueTicks: 1, unplayedReleasedCount: 0, backlogCap: 1));
    }

    [Fact]
    public void WatchGated_MissedTicksDoNotStack()
    {
        Assert.Equal(2, PacingPolicy.ComputeReleaseCount(PacingMode.WatchGated, episodesPerTick: 2, dueTicks: 5, unplayedReleasedCount: 0, backlogCap: 1));
    }

    [Fact]
    public void WatchGated_ReleasesNothing_WhileAnythingUnplayed()
    {
        Assert.Equal(0, PacingPolicy.ComputeReleaseCount(PacingMode.WatchGated, episodesPerTick: 2, dueTicks: 3, unplayedReleasedCount: 1, backlogCap: 1));
    }

    [Fact]
    public void BacklogCap_ReleasesUpToHeadroom()
    {
        // 3 due ticks x 1 per tick = 3 wanted, but only cap(2) - unplayed(0) = 2 headroom.
        Assert.Equal(2, PacingPolicy.ComputeReleaseCount(PacingMode.BacklogCap, episodesPerTick: 1, dueTicks: 3, unplayedReleasedCount: 0, backlogCap: 2));
    }

    [Fact]
    public void BacklogCap_ReleasesDueCount_WhenHeadroomIsLarger()
    {
        Assert.Equal(2, PacingPolicy.ComputeReleaseCount(PacingMode.BacklogCap, episodesPerTick: 2, dueTicks: 1, unplayedReleasedCount: 1, backlogCap: 10));
    }

    [Fact]
    public void BacklogCap_AtCap_ReleasesNothing()
    {
        Assert.Equal(0, PacingPolicy.ComputeReleaseCount(PacingMode.BacklogCap, episodesPerTick: 1, dueTicks: 2, unplayedReleasedCount: 2, backlogCap: 2));
    }

    [Fact]
    public void BacklogCap_OverCap_ClampsToZero_NotNegative()
    {
        Assert.Equal(0, PacingPolicy.ComputeReleaseCount(PacingMode.BacklogCap, episodesPerTick: 1, dueTicks: 2, unplayedReleasedCount: 5, backlogCap: 2));
    }
}
