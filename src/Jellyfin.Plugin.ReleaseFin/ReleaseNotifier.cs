using System.Text;
using System.Text.Json;
using Jellyfin.Data.Entities;
using Jellyfin.Plugin.ReleaseFin.Configuration;
using MediaBrowser.Common.Net;
using MediaBrowser.Model.Activity;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ReleaseFin;

/// <summary>Publishes release notifications: an activity log entry always, plus an optional JSON
/// webhook POST when a webhook URL is configured. Every failure is logged and swallowed — a
/// notification must never fail (or delay-fail) a release.</summary>
public class ReleaseNotifier(
    IActivityManager activityManager,
    IHttpClientFactory httpClientFactory,
    ILogger<ReleaseNotifier> logger)
{
    /// <summary>ActivityLog.Name column limit (10.10 schema).</summary>
    private const int ActivityTextMax = 512;

    private static readonly TimeSpan WebhookTimeout = TimeSpan.FromSeconds(10);

    public async Task NotifyAsync(
        ReleaseSchedule schedule,
        string seriesName,
        IReadOnlyList<(int Season, int Episode, string Name)> released,
        CancellationToken ct)
    {
        if (released.Count == 0)
        {
            return;
        }

        await WriteActivityLogAsync(schedule, seriesName, released).ConfigureAwait(false);
        await PostWebhookAsync(schedule, seriesName, released, ct).ConfigureAwait(false);
    }

    private async Task WriteActivityLogAsync(
        ReleaseSchedule schedule,
        string seriesName,
        IReadOnlyList<(int Season, int Episode, string Name)> released)
    {
        try
        {
            var episodes = string.Join(", ", released.Select(e => $"S{e.Season:D2}E{e.Episode:D2} '{e.Name}'"));
            var name = Truncate(
                $"ReleaseFin: released {episodes} of {seriesName} ({schedule.Name})", ActivityTextMax);
            var entry = new ActivityLog(name, "ReleaseFin.EpisodeReleased", Guid.Empty)
            {
                ShortOverview = Truncate(
                    $"{released.Count} episode(s) of {seriesName} released", ActivityTextMax)
            };
            await activityManager.CreateAsync(entry).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "ReleaseFin: failed to write activity log entry for schedule {Name}", schedule.Name);
        }
    }

    private async Task PostWebhookAsync(
        ReleaseSchedule schedule,
        string seriesName,
        IReadOnlyList<(int Season, int Episode, string Name)> released,
        CancellationToken ct)
    {
        var url = Plugin.Instance?.Configuration.WebhookUrl;
        if (string.IsNullOrWhiteSpace(url))
        {
            return;
        }

        try
        {
            var payload = new
            {
                schedule = schedule.Name,
                series = seriesName,
                episodes = released
                    .Select(e => new { season = e.Season, episode = e.Episode, name = e.Name })
                    .ToArray(),
                users = schedule.UserIds
            };
            using var content = new StringContent(
                JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(WebhookTimeout);
            var client = httpClientFactory.CreateClient(NamedClient.Default);
            using var response = await client.PostAsync(new Uri(url), content, cts.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning(
                    "ReleaseFin: webhook returned HTTP {Status} for schedule {Name}",
                    (int)response.StatusCode, schedule.Name);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "ReleaseFin: webhook POST failed for schedule {Name}", schedule.Name);
        }
    }

    private static string Truncate(string text, int max) =>
        text.Length <= max ? text : text[..(max - 1)] + "…";
}
