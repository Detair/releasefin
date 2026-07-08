using System.Net.Mime;
using Jellyfin.Plugin.ReleaseFin.Configuration;
using Jellyfin.Plugin.ReleaseFin.Core;
using MediaBrowser.Common.Api;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.ReleaseFin.Api;

public class ScheduleDto
{
    public Guid Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public Guid SeriesId { get; set; }

    public string SeriesName { get; set; } = string.Empty;

    public Guid[] UserIds { get; set; } = [];

    public string CronExpression { get; set; } = string.Empty;

    public int EpisodesPerTick { get; set; }

    public PacingMode Pacing { get; set; }

    public int BacklogCap { get; set; }

    public int? InitialSeason { get; set; }

    public int? InitialEpisode { get; set; }

    public bool Enabled { get; set; }

    public int Released { get; set; }

    public int Total { get; set; }

    public DateTime? NextRunUtc { get; set; }

    public bool Orphaned { get; set; }
}

public class SettingsDto
{
    public string WebhookUrl { get; set; } = string.Empty;
}

[ApiController]
[Authorize(Policy = Policies.RequiresElevation)]
[Route("ReleaseFin")]
[Produces(MediaTypeNames.Application.Json)]
public class ReleaseFinController(ReleaseManager releaseManager, ILibraryManager libraryManager)
    : ControllerBase
{
    private static readonly object ConfigLock = new();

    [HttpGet("Schedules")]
    public ActionResult<IEnumerable<ScheduleDto>> GetSchedules() =>
        Ok(Config.Schedules.Select(ToDto));

    [HttpPost("Schedules")]
    public async Task<ActionResult<ScheduleDto>> Create([FromBody] ReleaseSchedule schedule, CancellationToken ct)
    {
        var error = Validate(schedule);
        if (error is not null)
        {
            return BadRequest(error);
        }

        schedule.Id = Guid.NewGuid();
        schedule.LastRunUtc = DateTime.UtcNow;
        await releaseManager.ApplyAsync(schedule, ct).ConfigureAwait(false);
        lock (ConfigLock)
        {
            Config.Schedules = [.. Config.Schedules, schedule];
            Plugin.Instance!.SaveConfiguration();
        }

        return Ok(ToDto(schedule));
    }

    [HttpPut("Schedules/{id}")]
    public async Task<ActionResult<ScheduleDto>> Update(Guid id, [FromBody] ReleaseSchedule updated, CancellationToken ct)
    {
        var existing = Config.Schedules.FirstOrDefault(s => s.Id == id);
        if (existing is null)
        {
            return NotFound();
        }

        var error = Validate(updated);
        if (error is not null)
        {
            return BadRequest(error);
        }

        // Simplest correct semantics: tear down the old assignment, apply the new one.
        // Already-released episodes get re-locked if they're past the new offset — acceptable for edits.
        await releaseManager.RemoveAsync(existing, ct).ConfigureAwait(false);
        updated.Id = id;
        updated.LastRunUtc = DateTime.UtcNow;
        await releaseManager.ApplyAsync(updated, ct).ConfigureAwait(false);
        lock (ConfigLock)
        {
            Config.Schedules = [.. Config.Schedules.Where(s => s.Id != id), updated];
            Plugin.Instance!.SaveConfiguration();
        }

        return Ok(ToDto(updated));
    }

    [HttpDelete("Schedules/{id}")]
    public async Task<ActionResult> Delete(Guid id, CancellationToken ct)
    {
        var existing = Config.Schedules.FirstOrDefault(s => s.Id == id);
        if (existing is null)
        {
            return NotFound();
        }

        await releaseManager.RemoveAsync(existing, ct).ConfigureAwait(false);
        lock (ConfigLock)
        {
            Config.Schedules = Config.Schedules.Where(s => s.Id != id).ToArray();
            Plugin.Instance!.SaveConfiguration();
        }

        return NoContent();
    }

    [HttpPost("Schedules/{id}/ReleaseNow")]
    public async Task<ActionResult<ScheduleDto>> ReleaseNow(Guid id, CancellationToken ct)
    {
        var existing = Config.Schedules.FirstOrDefault(s => s.Id == id);
        if (existing is null)
        {
            return NotFound();
        }

        await releaseManager.ReleaseNextAsync(existing, existing.EpisodesPerTick, ct).ConfigureAwait(false);
        lock (ConfigLock)
        {
            Plugin.Instance!.SaveConfiguration(); // persist the advanced release frontier
        }

        return Ok(ToDto(existing));
    }

    [HttpGet("Settings")]
    public ActionResult<SettingsDto> GetSettings() =>
        Ok(new SettingsDto { WebhookUrl = Config.WebhookUrl });

    [HttpPut("Settings")]
    public ActionResult<SettingsDto> UpdateSettings([FromBody] SettingsDto settings)
    {
        var webhookUrl = settings.WebhookUrl?.Trim() ?? string.Empty;
        if (webhookUrl.Length > 0
            && (!Uri.TryCreate(webhookUrl, UriKind.Absolute, out var uri)
                || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)))
        {
            return BadRequest("Webhook URL must be an absolute http(s) URL, or empty to disable.");
        }

        lock (ConfigLock)
        {
            // Merge only this field: schedules are owned by the schedule endpoints.
            Config.WebhookUrl = webhookUrl;
            Plugin.Instance!.SaveConfiguration();
        }

        return Ok(new SettingsDto { WebhookUrl = webhookUrl });
    }

    [HttpGet("CronPreview")]
    public ActionResult<IEnumerable<DateTime>> CronPreview([FromQuery] string expression)
    {
        if (!ScheduleCalculator.IsValid(expression))
        {
            return BadRequest("Invalid cron expression.");
        }

        var occurrences = new List<DateTime>(3);
        var cursor = DateTime.UtcNow;
        for (var i = 0; i < 3; i++)
        {
            var next = ScheduleCalculator.NextOccurrenceUtc(expression, cursor, TimeZoneInfo.Local);
            if (next is null)
            {
                break;
            }

            occurrences.Add(next.Value);
            cursor = next.Value;
        }

        return Ok(occurrences);
    }

    private static PluginConfiguration Config => Plugin.Instance!.Configuration;

    private static string? Validate(ReleaseSchedule s)
    {
        if (!ScheduleCalculator.IsValid(s.CronExpression))
        {
            return "Invalid cron expression.";
        }

        if (s.SeriesId == Guid.Empty)
        {
            return "A series must be selected.";
        }

        if (s.UserIds.Length == 0)
        {
            return "At least one user must be selected.";
        }

        if (s.EpisodesPerTick < 1)
        {
            return "Episodes per tick must be at least 1.";
        }

        if (s.Pacing == PacingMode.BacklogCap && s.BacklogCap < 1)
        {
            return "Backlog cap must be at least 1.";
        }

        return null;
    }

    private ScheduleDto ToDto(ReleaseSchedule s)
    {
        var (released, total) = releaseManager.GetProgress(s);
        var series = libraryManager.GetItemById(s.SeriesId);
        return new ScheduleDto
        {
            Id = s.Id,
            Name = s.Name,
            SeriesId = s.SeriesId,
            SeriesName = series?.Name ?? "(deleted series)",
            UserIds = s.UserIds,
            CronExpression = s.CronExpression,
            EpisodesPerTick = s.EpisodesPerTick,
            Pacing = s.Pacing,
            BacklogCap = s.BacklogCap,
            InitialSeason = s.InitialSeason,
            InitialEpisode = s.InitialEpisode,
            Enabled = s.Enabled,
            Released = released,
            Total = total,
            NextRunUtc = ScheduleCalculator.IsValid(s.CronExpression)
                ? ScheduleCalculator.NextOccurrenceUtc(s.CronExpression, DateTime.UtcNow, TimeZoneInfo.Local)
                : null,
            Orphaned = series is null
        };
    }
}
