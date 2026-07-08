using Jellyfin.Plugin.ReleaseFin.Core;
using Xunit;

namespace Jellyfin.Plugin.ReleaseFin.Tests;

public class ReleaseFinTagTests
{
    private static readonly Guid Sid = Guid.Parse("11111111-2222-3333-4444-555555555555");

    [Fact]
    public void For_BuildsNamespacedTag() =>
        Assert.Equal("releasefin-11111111222233334444555555555555", ReleaseFinTag.For(Sid));

    [Theory]
    [InlineData("releasefin-abc", true)]
    [InlineData("ReleaseFin-abc", true)]
    [InlineData("kids", false)]
    public void IsReleaseFinTag_ChecksPrefix(string tag, bool expected) =>
        Assert.Equal(expected, ReleaseFinTag.IsReleaseFinTag(tag));

    [Fact]
    public void TryGetScheduleId_ParsesTagBuiltByFor()
    {
        Assert.True(ReleaseFinTag.TryGetScheduleId(ReleaseFinTag.For(Sid), out var id));
        Assert.Equal(Sid, id);
    }

    [Fact]
    public void TryGetScheduleId_IsCaseInsensitive()
    {
        Assert.True(ReleaseFinTag.TryGetScheduleId(
            "RELEASEFIN-11111111222233334444555555555555".ToUpperInvariant(), out var id));
        Assert.Equal(Sid, id);
    }

    [Theory]
    [InlineData("kids-11111111222233334444555555555555")] // wrong prefix
    [InlineData("11111111222233334444555555555555")] // no prefix
    [InlineData("releasefin-")] // empty guid part
    [InlineData("releasefin-not-a-guid")] // malformed guid
    [InlineData("releasefin-1111111122223333444455555555555")] // 31 hex chars
    [InlineData("releasefin-11111111-2222-3333-4444-555555555555")] // dashed, not "N" format
    public void TryGetScheduleId_RejectsNonScheduleTags(string tag)
    {
        Assert.False(ReleaseFinTag.TryGetScheduleId(tag, out var id));
        Assert.Equal(Guid.Empty, id);
    }

    [Fact]
    public void Add_IsIdempotent()
    {
        var once = ReleaseFinTag.Add(["kids"], "releasefin-x");
        var twice = ReleaseFinTag.Add(once, "RELEASEFIN-X");
        Assert.Equal(["kids", "releasefin-x"], once);
        Assert.Equal(once, twice);
    }

    [Fact]
    public void Remove_IsCaseInsensitive_AndKeepsOtherTags()
    {
        var result = ReleaseFinTag.Remove(["kids", "Releasefin-X"], "releasefin-x");
        Assert.Equal(["kids"], result);
        Assert.Equal(["kids"], ReleaseFinTag.Remove(["kids"], "releasefin-x"));
    }
}
