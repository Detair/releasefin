using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.ReleaseFin.Configuration;

public class PluginConfiguration : BasePluginConfiguration
{
    public ReleaseSchedule[] Schedules { get; set; } = [];
}
