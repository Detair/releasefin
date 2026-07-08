using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.ReleaseFin.Configuration;

public class PluginConfiguration : BasePluginConfiguration
{
    public ReleaseSchedule[] Schedules { get; set; } = [];

    /// <summary>Optional webhook endpoint; when non-empty, a JSON payload is POSTed on every release.</summary>
    public string WebhookUrl { get; set; } = string.Empty;
}
