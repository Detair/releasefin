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
