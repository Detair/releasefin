using Jellyfin.Plugin.ReleaseFin.Configuration;
using Jellyfin.Plugin.ReleaseFin.Core;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ReleaseFin;

/// <summary>Runs the drip scheduler (1-minute timer; catch-up after downtime is inherent because
/// due ticks are counted from the persisted LastRunUtc) and locks newly imported episodes/movies.</summary>
public sealed class ReleaseFinEntrypoint(
    ReleaseManager releaseManager,
    ILibraryManager libraryManager,
    ILogger<ReleaseFinEntrypoint> logger) : IHostedService, IDisposable
{
    private readonly CancellationTokenSource _cts = new();
    private Task? _loop;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        // Subscribe to both events: during a library scan ItemAdded can fire before the
        // episode's series linkage and index numbers are populated, so the lock decision
        // must be re-evaluated on ItemUpdated (idempotent via the frontier check).
        libraryManager.ItemAdded += OnItemChanged;
        libraryManager.ItemUpdated += OnItemChanged;
        _loop = Task.Run(() => RunLoopAsync(_cts.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        libraryManager.ItemAdded -= OnItemChanged;
        libraryManager.ItemUpdated -= OnItemChanged;
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
        try
        {
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

                    await releaseManager.ReleaseDueAsync(schedule, due, ct).ConfigureAwait(false);

                    // Advance even when pacing released nothing: gated/capped ticks are
                    // forfeited, never banked (that's what keeps WatchGated non-stacking).
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
        }
        finally
        {
            // Persist on every exit path (incl. shutdown cancellation mid-loop) so already-released
            // ticks never re-fire after a restart (double release).
            if (changed)
            {
                plugin.SaveConfiguration();
            }
        }
    }

    private void OnItemChanged(object? sender, ItemChangeEventArgs e)
    {
        // Pure MetadataEdit updates are this plugin's own tag writes (and manual metadata
        // edits, which are not imports); reacting to them would re-lock episodes that
        // RemoveAsync is untagging during schedule deletion.
        if (e.UpdateReason == ItemUpdateType.MetadataEdit)
        {
            return;
        }

        if (Plugin.Instance is null)
        {
            return;
        }

        // Episodes match a Series schedule by series id; movies match a Collection schedule
        // when the scheduled BoxSet's children contain them (imports are rare, so enumerating
        // the collection per event is fine).
        ReleaseSchedule[] schedules = e.Item switch
        {
            Episode episode => Plugin.Instance.Configuration.Schedules
                .Where(s => s.Enabled && s.Kind == ScheduleKind.Series && s.SeriesId == episode.SeriesId)
                .ToArray(),
            Movie movie => Plugin.Instance.Configuration.Schedules
                .Where(s => s.Enabled && s.Kind == ScheduleKind.Collection
                    && CollectionContains(s.SeriesId, movie.Id))
                .ToArray(),
            _ => [],
        };
        if (schedules.Length == 0)
        {
            return;
        }

        var item = e.Item;
        _ = Task.Run(async () =>
        {
            foreach (var schedule in schedules)
            {
                try
                {
                    await releaseManager.LockNewItemAsync(schedule, item, _cts.Token)
                        .ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "ReleaseFin: failed to lock new item for {Name}", schedule.Name);
                }
            }
        });
    }

    private bool CollectionContains(Guid collectionId, Guid movieId) =>
        libraryManager.GetItemById(collectionId) is BoxSet boxSet
        && boxSet.GetLinkedChildren().Any(c => c.Id == movieId);

    public void Dispose() => _cts.Dispose();
}
