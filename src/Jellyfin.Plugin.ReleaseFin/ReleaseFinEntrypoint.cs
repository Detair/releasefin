using Jellyfin.Plugin.ReleaseFin.Core;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ReleaseFin;

/// <summary>Runs the drip scheduler (1-minute timer; catch-up after downtime is inherent because
/// due ticks are counted from the persisted LastRunUtc) and locks newly imported episodes.</summary>
public sealed class ReleaseFinEntrypoint(
    ReleaseManager releaseManager,
    ILibraryManager libraryManager,
    ILogger<ReleaseFinEntrypoint> logger) : IHostedService, IDisposable
{
    private readonly CancellationTokenSource _cts = new();
    private Task? _loop;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        libraryManager.ItemAdded += OnItemAdded;
        _loop = Task.Run(() => RunLoopAsync(_cts.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        libraryManager.ItemAdded -= OnItemAdded;
        await _cts.CancelAsync().ConfigureAwait(false);
        if (_loop is not null)
        {
            try
            {
                await _loop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }
    }

    private async Task RunLoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        await TickAsync(ct).ConfigureAwait(false); // immediate pass = startup catch-up
        while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
        {
            await TickAsync(ct).ConfigureAwait(false);
        }
    }

    private async Task TickAsync(CancellationToken ct)
    {
        var plugin = Plugin.Instance;
        if (plugin is null)
        {
            return;
        }

        var changed = false;
        foreach (var schedule in plugin.Configuration.Schedules)
        {
            try
            {
                var now = DateTime.UtcNow;
                if (!schedule.Enabled || !ScheduleCalculator.IsValid(schedule.CronExpression))
                {
                    continue;
                }

                var due = ScheduleCalculator.CountDueTicks(
                    schedule.CronExpression, schedule.LastRunUtc, now, TimeZoneInfo.Local);
                if (due == 0)
                {
                    continue;
                }

                await releaseManager
                    .ReleaseNextAsync(schedule, due * schedule.EpisodesPerTick, ct)
                    .ConfigureAwait(false);
                schedule.LastRunUtc = now;
                changed = true;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "ReleaseFin: tick failed for schedule {Name}", schedule.Name);
            }
        }

        if (changed)
        {
            plugin.SaveConfiguration();
        }
    }

    private void OnItemAdded(object? sender, ItemChangeEventArgs e)
    {
        if (e.Item is not Episode episode || Plugin.Instance is null)
        {
            return;
        }

        var schedules = Plugin.Instance.Configuration.Schedules
            .Where(s => s.Enabled && s.SeriesId == episode.SeriesId)
            .ToArray();
        if (schedules.Length == 0)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            foreach (var schedule in schedules)
            {
                try
                {
                    await releaseManager.LockNewEpisodeAsync(schedule, episode, _cts.Token)
                        .ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "ReleaseFin: failed to lock new episode for {Name}", schedule.Name);
                }
            }
        });
    }

    public void Dispose() => _cts.Dispose();
}
