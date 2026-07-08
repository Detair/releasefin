using Jellyfin.Data.Enums;
using Jellyfin.Plugin.ReleaseFin.Configuration;
using Jellyfin.Plugin.ReleaseFin.Core;
using MediaBrowser.Controller.Entities;
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

    /// <summary>On schedule creation: lock every episode after the initial offset, then block the tag for each user.</summary>
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

            foreach (var (episode, key) in GetOrderedEpisodes(schedule.SeriesId))
            {
                if (offset is { } o && key.IsAtOrBefore(o))
                {
                    continue;
                }

                await SetTagAsync(episode, tag, present: true, ct).ConfigureAwait(false);
            }

            await SetUserBlockAsync(schedule.UserIds, tag, blocked: true).ConfigureAwait(false);
            logger.LogInformation("ReleaseFin: applied schedule {Name} ({Id})", schedule.Name, schedule.Id);
        }
        finally
        {
            _mutex.Release();
        }
    }

    /// <summary>Release the next N still-locked episodes in aired order. Returns how many were released.</summary>
    public Task<int> ReleaseNextAsync(ReleaseSchedule schedule, int count, CancellationToken ct) =>
        ReleaseWhileAsync(schedule, (_, releasedSoFar) => releasedSoFar < count, ct);

    /// <summary>One-off override: release every still-locked episode at or before the target
    /// (aired order). Returns how many were released.</summary>
    public Task<int> ReleaseUpToAsync(ReleaseSchedule schedule, EpisodeKey target, CancellationToken ct) =>
        ReleaseWhileAsync(schedule, (key, _) => key.IsAtOrBefore(target), ct);

    /// <summary>Releases still-locked episodes in aired order while the predicate (episode key,
    /// count released so far) holds. This is the single choke point for every release path
    /// (scheduler ticks, catch-up, ReleaseNow, ReleaseUpTo), so notifications fire here — after
    /// the semaphore is released, once per batch, and never failing the release.</summary>
    private async Task<int> ReleaseWhileAsync(
        ReleaseSchedule schedule, Func<EpisodeKey, int, bool> keepReleasing, CancellationToken ct)
    {
        var released = new List<(int Season, int Episode, string Name)>();
        await _mutex.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var tag = ReleaseFinTag.For(schedule.Id);

            foreach (var (episode, key) in GetOrderedEpisodes(schedule.SeriesId))
            {
                if (!keepReleasing(key, released.Count))
                {
                    break;
                }

                if (!episode.Tags.Contains(tag, StringComparer.OrdinalIgnoreCase))
                {
                    continue;
                }

                await SetTagAsync(episode, tag, present: false, ct).ConfigureAwait(false);
                released.Add((key.Season, key.Episode, episode.Name ?? string.Empty));

                // Advance the persisted import-classification frontier past this episode.
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
                    "ReleaseFin: released {Count} episode(s) for schedule {Name}", released.Count, schedule.Name);
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

    /// <summary>Scheduler entry point: releases as many episodes as the schedule's pacing mode
    /// allows for the given due ticks. The unplayed count is read before taking the semaphore
    /// (inside ReleaseNextAsync); benign race — the scheduler is the single writer.</summary>
    public async Task<int> ReleaseDueAsync(ReleaseSchedule schedule, int dueTicks, CancellationToken ct)
    {
        // Accumulate never looks at play state; skip the per-user data cost entirely.
        var unplayed = schedule.Pacing == PacingMode.Accumulate ? 0 : CountUnplayedReleased(schedule);
        var count = PacingPolicy.ComputeReleaseCount(
            schedule.Pacing, schedule.EpisodesPerTick, dueTicks, unplayed, schedule.BacklogCap);
        return count > 0 ? await ReleaseNextAsync(schedule, count, ct).ConfigureAwait(false) : 0;
    }

    /// <summary>Episodes the plugin itself released (untagged AND after the initial offset) that at
    /// least one assigned user has not played yet — "played" means played by ALL assigned users.
    /// Deleted users are skipped; episodes released via the initial offset never gate.</summary>
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
        foreach (var (episode, key) in GetOrderedEpisodes(schedule.SeriesId))
        {
            if (offset is { } o && key.IsAtOrBefore(o))
            {
                continue; // pre-released by the initial offset, not by the plugin
            }

            if (episode.Tags.Contains(tag, StringComparer.OrdinalIgnoreCase))
            {
                continue; // still locked
            }

            if (users.Any(u => !userDataManager.GetUserData(u!, episode).Played))
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

            foreach (var (episode, _) in GetOrderedEpisodes(schedule.SeriesId))
            {
                await SetTagAsync(episode, tag, present: false, ct).ConfigureAwait(false);
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
    /// schedule (including unparseable releasefin-* tags) from all episodes in the library and
    /// from every user's blocked-tags preference. The documented uninstall story. SAFETY: only
    /// releasefin-prefixed entries are ever considered; other tags are never touched.</summary>
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
            var allEpisodes = libraryManager.GetItemList(new InternalItemsQuery
            {
                IncludeItemTypes = [BaseItemKind.Episode],
                Recursive = true
            });
            foreach (var item in allEpisodes)
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

    /// <summary>A newly imported episode arrives locked unless it sorts at or before the schedule's
    /// persisted release frontier (back-fill inside the released region). Classification never
    /// reads churning tag state, so multi-episode imports are fully order-independent; with no
    /// frontier (nothing released yet) the new episode is always locked — anti-binge-safe.</summary>
    public async Task LockNewEpisodeAsync(ReleaseSchedule schedule, Episode newEpisode, CancellationToken ct)
    {
        await _mutex.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!EpisodeKey.TryCreate(newEpisode.ParentIndexNumber, newEpisode.IndexNumber, out var newKey)
                || newKey.IsSpecial)
            {
                return;
            }

            if (GetFrontier(schedule) is { } f && newKey.IsAtOrBefore(f))
            {
                return; // back-fill inside the already-released region stays visible
            }

            var tag = ReleaseFinTag.For(schedule.Id);
            var wrote = await SetTagAsync(newEpisode, tag, present: true, ct).ConfigureAwait(false);
            if (wrote)
            {
                logger.LogInformation(
                    "ReleaseFin: locked new episode S{Season}E{Episode} for schedule {Name}",
                    newKey.Season, newKey.Episode, schedule.Name);
            }
        }
        finally
        {
            _mutex.Release();
        }
    }

    /// <summary>(released, total) counts of drip-eligible episodes for the schedule's series.</summary>
    public (int Released, int Total) GetProgress(ReleaseSchedule schedule)
    {
        var tag = ReleaseFinTag.For(schedule.Id);
        var episodes = GetOrderedEpisodes(schedule.SeriesId).ToList();
        var locked = episodes.Count(p => p.Episode.Tags.Contains(tag, StringComparer.OrdinalIgnoreCase));
        return (episodes.Count - locked, episodes.Count);
    }

    /// <summary>The schedule's persisted release high-water mark, or null when nothing released yet.</summary>
    private static EpisodeKey? GetFrontier(ReleaseSchedule schedule) =>
        schedule.ReleasedUpToSeason is int s && schedule.ReleasedUpToEpisode is int e
            ? new EpisodeKey(s, e)
            : null;

    /// <summary>Drip-eligible episodes of the series in aired order (orderable, non-special).</summary>
    private IEnumerable<(Episode Episode, EpisodeKey Key)> GetOrderedEpisodes(Guid seriesId) =>
        libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = [BaseItemKind.Episode],
            AncestorIds = [seriesId],
            Recursive = true
        })
        .OfType<Episode>()
        .Select(e => (Episode: e,
            HasKey: EpisodeKey.TryCreate(e.ParentIndexNumber, e.IndexNumber, out var key),
            Key: key))
        .Where(t => t.HasKey && !t.Key.IsSpecial)
        .OrderBy(t => t.Key)
        .Select(t => (t.Episode, t.Key));

    /// <summary>Returns true when a metadata write actually happened.</summary>
    private async Task<bool> SetTagAsync(Episode episode, string tag, bool present, CancellationToken ct)
    {
        var updated = present
            ? ReleaseFinTag.Add(episode.Tags, tag)
            : ReleaseFinTag.Remove(episode.Tags, tag);
        if (ReferenceEquals(updated, episode.Tags) || updated.Length == episode.Tags.Length)
        {
            if (present == episode.Tags.Contains(tag, StringComparer.OrdinalIgnoreCase))
            {
                return false; // already in desired state; skip a pointless metadata write
            }
        }

        episode.Tags = updated;
        await libraryManager
            .UpdateItemAsync(episode, episode.GetParent(), ItemUpdateType.MetadataEdit, ct)
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
