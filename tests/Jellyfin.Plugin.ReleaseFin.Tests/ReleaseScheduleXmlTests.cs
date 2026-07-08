using System.Xml.Serialization;
using Jellyfin.Plugin.ReleaseFin.Configuration;
using Jellyfin.Plugin.ReleaseFin.Core;
using Xunit;

namespace Jellyfin.Plugin.ReleaseFin.Tests;

public class ReleaseScheduleXmlTests
{
    /// <summary>Pre-1.1 configs carry no Kind/PauseAtSeasonEnd/SeasonPaused elements; they must
    /// deserialize to the original series semantics.</summary>
    [Fact]
    public void OldConfigXml_DefaultsToSeriesKindWithoutPause()
    {
        const string xml = """
            <ReleaseSchedule>
              <Id>5aa9d1f0-a48c-3b4a-5e9f-2d6b0c4d8e1a</Id>
              <Name>old</Name>
              <SeriesId>e7d1f0a4-8c3b-4a5e-9f2d-6b0c4d8e1a23</SeriesId>
              <CronExpression>0 16 * * *</CronExpression>
              <EpisodesPerTick>1</EpisodesPerTick>
              <Enabled>true</Enabled>
            </ReleaseSchedule>
            """;

        var serializer = new XmlSerializer(typeof(ReleaseSchedule));
        using var reader = new StringReader(xml);
        var schedule = Assert.IsType<ReleaseSchedule>(serializer.Deserialize(reader));

        Assert.Equal(ScheduleKind.Series, schedule.Kind);
        Assert.False(schedule.PauseAtSeasonEnd);
        Assert.False(schedule.SeasonPaused);
    }
}
