using Jellyfin.Data.Enums;
using Jellyfin.Plugin.ReleaseFin.Configuration;
using Jellyfin.Plugin.ReleaseFin.Core;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ReleaseFin;

/// <summary>Applies drip-release decisions to the library and user policies. All mutations are
/// serialized behind a semaphore so timer ticks, API calls, and library events cannot interleave.
/// SAFETY: only ever creates/removes releasefin-* tags and this plugin's own blocked-tag entries.</summary>
public class ReleaseManager(
    ILibraryManager libraryManager,
    IUserManager userManager,
    IUserDataManager userDataManager,
    ReleaseNotifier notifier,
    ILogger<ReleaseManager> logger)
{
    private readonly SemaphoreSlim _mutex = new(1, 1);

    /// <summary>On schedule creation: lock every item after the initial offset, then block the tag for each user.</summary>
    public async Task ApplyAsync(ReleaseSchedule schedule, CancellationToken ct)
    {
        await _mutex.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var tag = ReleaseFinTag.For(schedule.Id);
            EpisodeKey? offset = schedule.InitialSeason is int s && schedule.InitialEpisode is int e
                ? new EpisodeKey(s, e)
                : null;

            // The import-classification frontier starts at the initial offset (null when no offset).
            schedule.ReleasedUpToSeason = offset?.Season;
            schedule.ReleasedUpToEpisode = offset?.Episode;

            foreach (var (item, key) in GetOrderedItems(schedule))
            {
                if (offset is { } o && key.IsAtOrBefore(o))
                {
                    continue;
                }

                await SetTagAsync(item, tag, present: true, ct).ConfigureAwait(false);
            }

            await SetUserBlockAsync(schedule.UserIds, tag, blocked: true).ConfigureAwait(false);
            logger.LogInformation("ReleaseFin: applied schedule {Name} ({Id})", schedule.Name, schedule.Id);
        }
        finally
        {
            _mutex.Release();
        }
    }

    /// <summary>Release the next N still-locked items in release order. Returns how many were released.</summary>
    public Task<int> ReleaseNextAsync(ReleaseSchedule schedule, int count, CancellationToken ct) =>
        ReleaseWhileAsync(schedule, (_, releasedSoFar) => releasedSoFar < count, ct);

    /// <summary>One-off override: release every still-locked episode at or before the target
    /// (aired order). Returns how many were released.</summary>
    public Task<int> ReleaseUpToAsync(ReleaseSchedule schedule, EpisodeKey target, CancellationToken ct) =>
        ReleaseWhileAsync(schedule, (key, _) => key.IsAtOrBefore(target), ct);

    /// <summary>Releases still-locked items in release order while the predicate (item key,
    /// count released so far) holds. This is the single choke point for every release path
    /// (scheduler ticks, catch-up, ReleaseNow, ReleaseUpTo, Resume), so notifications fire here —
    /// after the semaphore is released, once per batch, and never failing the release.</summary>
    private async Task<int> ReleaseWhileAsync(
        ReleaseSchedule schedule, Func<EpisodeKey, int, bool> keepReleasing, CancellationToken ct)
    {
        var released = new List<(int Season, int Episode, string Name)>();
        await _mutex.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var tag = ReleaseFinTag.For(schedule.Id);

            foreach (var (item, key) in GetOrderedItems(schedule))
            {
                if (!keepReleasing(key, released.Count))
                {
                    break;
                }

                if (!item.Tags.Contains(tag, StringComparer.OrdinalIgnoreCase))
                {
                    continue;
                }

                await SetTagAsync(item, tag, present: false, ct).ConfigureAwait(false);
                released.Add((key.Season, key.Episode, item.Name ?? string.Empty));

                // Advance the persisted import-classification frontier past this item.
                var frontier = GetFrontier(schedule);
                if (frontier is not { } f || key.CompareTo(f) > 0)
                {
                    schedule.ReleasedUpToSeason = key.Season;
                    schedule.ReleasedUpToEpisode = key.Episode;
                }
            }

            if (released.Count > 0)
            {
                logger.LogInformation(
                    "ReleaseFin: released {Count} item(s) for schedule {Name}", released.Count, schedule.Name);
            }
        }
        finally
        {
            _mutex.Release();
        }

        if (released.Count > 0)
        {
            // Outside the semaphore: the notifier may do slow I/O (webhook) and must never
            // block or fail library mutations. Nothing after a successful release may throw —
            // callers still need the return value to persist frontier/LastRunUtc state.
            try
            {
                var seriesName = libraryManager.GetItemById(schedule.SeriesId)?.Name ?? "(unknown series)";
                await notifier.NotifyAsync(schedule, seriesName, released, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "ReleaseFin: post-release notification failed for {Name}", schedule.Name);
            }
        }

        return released.Count;
    }

    /// <summary>Scheduler entry point: releases as many items as the schedule's pacing mode
    /// allows for the given due ticks — unless the schedule is (or just became) season-paused.
    /// The unplayed count is read before taking the semaphore (inside ReleaseNextAsync);
    /// benign race — the scheduler is the single writer.</summary>
    public async Task<int> ReleaseDueAsync(ReleaseSchedule schedule, int dueTicks, CancellationToken ct)
    {
        if (schedule.SeasonPaused)
        {
            return 0; // waiting for a manual Resume; due ticks are forfeited, never banked
        }

        if (await PauseIfSeasonBoundaryAsync(schedule, ct).ConfigureAwait(false))
        {
            return 0; // the caller (scheduler tick) persists the SeasonPaused flip
        }

        // Accumulate never looks at play state; skip the per-user data cost entirely.
        var unplayed = schedule.Pacing == PacingMode.Accumulate ? 0 : CountUnplayedReleased(schedule);
        var count = PacingPolicy.ComputeReleaseCount(
            schedule.Pacing, schedule.EpisodesPerTick, dueTicks, unplayed, schedule.BacklogCap);
        return count > 0 ? await ReleaseNextAsync(schedule, count, ct).ConfigureAwait(false) : 0;
    }

    /// <summary>Season-end gate (Series kind with PauseAtSeasonEnd): when the next still-locked
    /// episode (in aired order) starts a later season than the release frontier, flips
    /// SeasonPaused and notifies instead of releasing. Tag state is read outside the semaphore
    /// like CountUnplayedReleased: benign race, the scheduler is the single writer.</summary>
    private async Task<bool> PauseIfSeasonBoundaryAsync(ReleaseSchedule schedule, CancellationToken ct)
    {
        if (!schedule.PauseAtSeasonEnd || schedule.Kind != ScheduleKind.Series
            || GetFrontier(schedule) is not { } frontier)
        {
            return false;
        }

        var tag = ReleaseFinTag.For(schedule.Id);
        var next = GetOrderedItems(schedule)
            .FirstOrDefault(p => p.Item.Tags.Contains(tag, StringComparer.OrdinalIgnoreCase));
        if (next.Item is null || next.Key.Season <= frontier.Season)
        {
            return false;
        }

        schedule.SeasonPaused = true;
        logger.LogInformation(
            "ReleaseFin: schedule {Name} paused at the end of season {Season}",
            schedule.Name, frontier.Season);
        var seriesName = libraryManager.GetItemById(schedule.SeriesId)?.Name ?? "(unknown series)";
        await notifier.NotifySeasonPausedAsync(schedule, seriesName, next.Key.Season, ct).ConfigureAwait(false);
        return true;
    }

    /// <summary>Items the plugin itself released (untagged AND after the initial offset) that at
    /// least one assigned user has not played yet — "played" means played by ALL assigned users.
    /// Deleted users are skipped; items released via the initial offset never gate.</summary>
    private int CountUnplayedReleased(ReleaseSchedule schedule)
    {
        var tag = ReleaseFinTag.For(schedule.Id);
        EpisodeKey? offset = schedule.InitialSeason is int s && schedule.InitialEpisode is int e
            ? new EpisodeKey(s, e)
            : null;
        var users = schedule.UserIds
            .Select(userManager.GetUserById)
            .Where(u => u is not null)
            .ToArray();
        if (users.Length == 0)
        {
            return 0;
        }

        var unplayed = 0;
        foreach (var (item, key) in GetOrderedItems(schedule))
        {
            if (offset is { } o && key.IsAtOrBefore(o))
            {
                continue; // pre-released by the initial offset, not by the plugin
            }

            if (item.Tags.Contains(tag, StringComparer.OrdinalIgnoreCase))
            {
                continue; // still locked
            }

            if (users.Any(u => !userDataManager.GetUserData(u!, item).Played))
            {
                unplayed++;
            }
        }

        return unplayed;
    }

    /// <summary>On schedule deletion: untag everything and unblock the tag for all users (not just
    /// currently assigned ones, in case assignments changed).</summary>
    public async Task RemoveAsync(ReleaseSchedule schedule, CancellationToken ct)
    {
        await _mutex.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var tag = ReleaseFinTag.For(schedule.Id);

            foreach (var (item, _) in GetOrderedItems(schedule))
            {
                await SetTagAsync(item, tag, present: false, ct).ConfigureAwait(false);
            }

            var allUserIds = userManager.Users.Select(u => u.Id).ToArray();
            await SetUserBlockAsync(allUserIds, tag, blocked: false).ConfigureAwait(false);
            logger.LogInformation("ReleaseFin: removed schedule {Name} ({Id})", schedule.Name, schedule.Id);
        }
        finally
        {
            _mutex.Release();
        }
    }

    /// <summary>Maintenance sweep: removes every releasefin-* tag that belongs to no existing
    /// schedule (including unparseable releasefin-* tags) from all episodes and movies in the
    /// library and from every user's blocked-tags preference. The documented uninstall story.
    /// SAFETY: only releasefin-prefixed entries are ever considered; other tags are never touched.</summary>
    public async Task<(int ItemsCleaned, int UsersCleaned)> CleanStrayTagsAsync(CancellationToken ct)
    {
        await _mutex.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var knownIds = Plugin.Instance!.Configuration.Schedules.Select(s => s.Id).ToHashSet();
            bool IsStray(string tag) =>
                ReleaseFinTag.IsReleaseFinTag(tag)
                && (!ReleaseFinTag.TryGetScheduleId(tag, out var id) || !knownIds.Contains(id));

            var itemsCleaned = 0;
            var allItems = libraryManager.GetItemList(new InternalItemsQuery
            {
                IncludeItemTypes = [BaseItemKind.Episode, BaseItemKind.Movie],
                Recursive = true
            });
            foreach (var item in allItems)
            {
                var kept = item.Tags.Where(t => !IsStray(t)).ToArray();
                if (kept.Length == item.Tags.Length)
                {
                    continue;
                }

                item.Tags = kept;
                await libraryManager
                    .UpdateItemAsync(item, item.GetParent(), ItemUpdateType.MetadataEdit, ct)
                    .ConfigureAwait(false);
                itemsCleaned++;
            }

            var usersCleaned = 0;
            foreach (var user in userManager.Users.ToArray())
            {
                var current = user.GetPreference(PreferenceKind.BlockedTags) ?? [];
                var kept = current.Where(t => !IsStray(t)).ToArray();
                if (kept.Length == current.Length)
                {
                    continue;
                }

                user.SetPreference(PreferenceKind.BlockedTags, kept);
                await userManager.UpdateUserAsync(user).ConfigureAwait(false);
                usersCleaned++;
            }

            logger.LogInformation(
                "ReleaseFin: stray-tag cleanup touched {Items} item(s) and {Users} user(s)",
                itemsCleaned, usersCleaned);
            return (itemsCleaned, usersCleaned);
        }
        finally
        {
            _mutex.Release();
        }
    }

    /// <summary>A newly imported item arrives locked unless it sorts at or before the schedule's
    /// persisted release frontier (back-fill inside the released region). Classification never
    /// reads churning tag state, so multi-item imports are fully order-independent; with no
    /// frontier (nothing released yet) the new item is always locked — anti-binge-safe.
    /// Series kind expects an Episode, Collection kind a Movie already linked into the BoxSet
    /// (its pseudo key is its ordinal in the collection's current premiere ordering).
    /// <paramref name="isNewItem"/> gates Collection classification to true first-sight events
    /// only (ItemAdded): a movie's ordinal is computed live from the collection's current
    /// membership, so re-running this on an unrelated ItemUpdated for an EXISTING movie (e.g. a
    /// routine metadata refresh) could see its ordinal shifted past the frontier by some other
    /// import and re-lock an already-released movie — silently revoking access. Restricting to
    /// ItemAdded trades away one narrow case (a pre-existing movie linked into an already-created
    /// schedule's collection later, without itself being freshly imported, stays unclassified)
    /// for safety in the common case. Episodes are unaffected: season/episode numbering is
    /// intrinsic to the file, not derived from other items, so re-evaluating on ItemUpdated is
    /// safe and still needed (series linkage isn't always populated yet at ItemAdded time).</summary>
    public async Task LockNewItemAsync(ReleaseSchedule schedule, BaseItem newItem, bool isNewItem, CancellationToken ct)
    {
        if (schedule.Kind == ScheduleKind.Collection && !isNewItem)
        {
            return;
        }

        await _mutex.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            (BaseItem Item, EpisodeKey Key) target;
            if (schedule.Kind == ScheduleKind.Series && newItem is Episode episode)
            {
                if (!EpisodeKey.TryCreate(episode.ParentIndexNumber, episode.IndexNumber, out var key)
                    || key.IsSpecial)
                {
                    return;
                }

                target = (episode, key);
            }
            else if (schedule.Kind == ScheduleKind.Collection && newItem is Movie)
            {
                target = GetOrderedItems(schedule).FirstOrDefault(p => p.Item.Id == newItem.Id);
                if (target.Item is null)
                {
                    return; // not (yet) linked into the collection; nothing to classify
                }
            }
            else
            {
                return;
            }

            if (GetFrontier(schedule) is { } f && target.Key.IsAtOrBefore(f))
            {
                return; // back-fill inside the already-released region stays visible
            }

            var tag = ReleaseFinTag.For(schedule.Id);
            var wrote = await SetTagAsync(target.Item, tag, present: true, ct).ConfigureAwait(false);
            if (wrote)
            {
                logger.LogInformation(
                    "ReleaseFin: locked new item S{Season}E{Episode} for schedule {Name}",
                    target.Key.Season, target.Key.Episode, schedule.Name);
            }
        }
        finally
        {
            _mutex.Release();
        }
    }

    /// <summary>(released, total) counts of drip-eligible items for the schedule's target.</summary>
    public (int Released, int Total) GetProgress(ReleaseSchedule schedule)
    {
        var tag = ReleaseFinTag.For(schedule.Id);
        var items = GetOrderedItems(schedule).ToList();
        var locked = items.Count(p => p.Item.Tags.Contains(tag, StringComparer.OrdinalIgnoreCase));
        return (items.Count - locked, items.Count);
    }

    /// <summary>The schedule's persisted release high-water mark, or null when nothing released yet.</summary>
    private static EpisodeKey? GetFrontier(ReleaseSchedule schedule) =>
        schedule.ReleasedUpToSeason is int s && schedule.ReleasedUpToEpisode is int e
            ? new EpisodeKey(s, e)
            : null;

    /// <summary>Drip-eligible items of the schedule's target in release order. Series: episodes
    /// in aired order (orderable, non-special). Collection: the BoxSet's movies in premiere order
    /// (see MovieOrderKey), mapped to pseudo keys S1E(ordinal) — never special, so all existing
    /// offset/frontier/pacing logic applies unchanged.</summary>
    private IEnumerable<(BaseItem Item, EpisodeKey Key)> GetOrderedItems(ReleaseSchedule schedule)
    {
        if (schedule.Kind == ScheduleKind.Collection)
        {
            if (libraryManager.GetItemById(schedule.SeriesId) is not BoxSet boxSet)
            {
                return [];
            }

            return boxSet.GetLinkedChildren()
                .OfType<Movie>()
                .OrderBy(m => new MovieOrderKey(m.PremiereDate, m.ProductionYear, m.SortName))
                .Select((m, index) => ((BaseItem)m, new EpisodeKey(1, index + 1)));
        }

        return libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = [BaseItemKind.Episode],
            AncestorIds = [schedule.SeriesId],
            Recursive = true
        })
        .OfType<Episode>()
        .Select(e => (Episode: e,
            HasKey: EpisodeKey.TryCreate(e.ParentIndexNumber, e.IndexNumber, out var key),
            Key: key))
        .Where(t => t.HasKey && !t.Key.IsSpecial)
        .OrderBy(t => t.Key)
        .Select(t => ((BaseItem)t.Episode, t.Key));
    }

    /// <summary>Returns true when a metadata write actually happened.</summary>
    private async Task<bool> SetTagAsync(BaseItem item, string tag, bool present, CancellationToken ct)
    {
        var updated = present
            ? ReleaseFinTag.Add(item.Tags, tag)
            : ReleaseFinTag.Remove(item.Tags, tag);
        if (ReferenceEquals(updated, item.Tags) || updated.Length == item.Tags.Length)
        {
            if (present == item.Tags.Contains(tag, StringComparer.OrdinalIgnoreCase))
            {
                return false; // already in desired state; skip a pointless metadata write
            }
        }

        item.Tags = updated;
        await libraryManager
            .UpdateItemAsync(item, item.GetParent(), ItemUpdateType.MetadataEdit, ct)
            .ConfigureAwait(false);
        return true;
    }

    private async Task SetUserBlockAsync(Guid[] userIds, string tag, bool blocked)
    {
        foreach (var userId in userIds)
        {
            var user = userManager.GetUserById(userId);
            if (user is null)
            {
                continue; // user deleted; nothing to clean
            }

            var current = user.GetPreference(PreferenceKind.BlockedTags) ?? [];
            var updated = blocked ? ReleaseFinTag.Add(current, tag) : ReleaseFinTag.Remove(current, tag);
            if (updated.Length == current.Length)
            {
                continue;
            }

            user.SetPreference(PreferenceKind.BlockedTags, updated);
            await userManager.UpdateUserAsync(user).ConfigureAwait(false);
        }
    }
}
