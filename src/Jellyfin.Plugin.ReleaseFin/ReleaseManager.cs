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
    public async Task<int> ReleaseNextAsync(ReleaseSchedule schedule, int count, CancellationToken ct)
    {
        await _mutex.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var tag = ReleaseFinTag.For(schedule.Id);
            var released = 0;

            foreach (var (episode, _) in GetOrderedEpisodes(schedule.SeriesId))
            {
                if (released >= count)
                {
                    break;
                }

                if (!episode.Tags.Contains(tag, StringComparer.OrdinalIgnoreCase))
                {
                    continue;
                }

                await SetTagAsync(episode, tag, present: false, ct).ConfigureAwait(false);
                released++;
            }

            if (released > 0)
            {
                logger.LogInformation(
                    "ReleaseFin: released {Count} episode(s) for schedule {Name}", released, schedule.Name);
            }

            return released;
        }
        finally
        {
            _mutex.Release();
        }
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

    /// <summary>A newly imported episode arrives locked unless it sorts strictly before the first
    /// still-tagged episode (back-fill inside the released prefix). Anti-binge-safe: other
    /// just-imported, not-yet-tagged episodes never widen the visible region, and when no tagged
    /// episode exists (drip caught up) the new episode is always locked — a back-filled old episode
    /// then self-heals on the next tick.</summary>
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

            var tag = ReleaseFinTag.For(schedule.Id);
            EpisodeKey? firstTagged = null;
            foreach (var (episode, key) in GetOrderedEpisodes(schedule.SeriesId))
            {
                if (episode.Id != newEpisode.Id
                    && episode.Tags.Contains(tag, StringComparer.OrdinalIgnoreCase))
                {
                    firstTagged = key; // ordered ascending => first hit is the lowest tagged key
                    break;
                }
            }

            if (firstTagged is { } f && newKey.CompareTo(f) < 0)
            {
                return; // back-fill inside the already-released prefix stays visible
            }

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
