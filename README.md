# releasefin

ReleaseFin is a Jellyfin 10.10 plugin that drip-releases episodes of a series to selected accounts on a schedule, like weekly TV. You pick a series, the users it applies to, and a cadence (daily, weekly, or raw cron); unreleased episodes are hidden from those users until their release time passes. Example: your kids' account gets one episode of a show every day at 16:00 instead of the whole box set at once — everyone else on the server still sees everything.

## How hiding works

ReleaseFin does not delete or move anything. Each schedule tags its unreleased episodes with a `releasefin-<schedule-id>` tag and adds that tag to the assigned users' "Block items with tags" parental control. For those users the episodes simply don't appear; for admins and unassigned users nothing changes. You can see (and, if you must, hand-edit) the tags in the metadata editor — the plugin only ever touches tags starting with `releasefin-` and its own blocked-tag entries.

At each release time the scheduler removes the tag from the next episode(s) in aired order, making them visible. The schedule is evaluated every minute against the cron expression, in the **server's local time zone**.

## Install

There is no plugin repository entry yet — install manually:

1. Build the plugin:
   ```
   dotnet publish src/Jellyfin.Plugin.ReleaseFin -c Release -o publish
   ```
2. Copy **both** `Jellyfin.Plugin.ReleaseFin.dll` and `Cronos.dll` from the publish output into a `ReleaseFin` folder inside your Jellyfin plugin directory (e.g. `config/plugins/ReleaseFin/`). Cronos.dll is required — the plugin will not load without it.
3. Restart Jellyfin. The plugin appears under Dashboard → Plugins as **ReleaseFin**.

Requires Jellyfin 10.10.x.

## Usage

Open Dashboard → Plugins → ReleaseFin and click **New schedule**:

- **Name** — a label for the schedule list.
- **Series** — the series to drip.
- **Users** — the accounts the drip applies to. Everyone else is unaffected.
- **Schedule** — *Daily* or *Weekly* presets (pick a time of day, and a weekday for weekly), or *Advanced* for a raw 5-field cron expression (`min hour dom mon dow`). A preview shows the next three release times.
- **Episodes per release** — how many episodes each tick unlocks (default 1).
- **Already released up to** — an initial offset like `S01E05`: episodes up to and including it start visible; leave empty to start with everything locked. A series with zero released episodes appears empty (not hidden) to the assigned users until the first release.
- **Enabled** — disabled schedules keep their tags in place but stop releasing.

Saving applies the schedule immediately: episodes past the offset are tagged, and the tag is added to each selected user's blocked list. The schedule list shows progress ("12/48 released"), the next release time, and status. **Release now** unlocks the next batch on demand; **Delete** (with confirmation) removes the schedule and makes everything visible again.

### Things worth knowing

- **Schedules are per-series, shared by their users.** All accounts on one schedule see the same set of released episodes and advance together. If two kids should watch at different paces, give each their own schedule (the tags are per-schedule, so they don't conflict).
- **Missed release times catch up.** If the server is off for three days, the next tick counts all three missed occurrences and releases three days' worth of episodes at once.
- **New downloads arrive locked.** When an episode of a scheduled series is imported past the released frontier, it is tagged immediately, so a season pack landing overnight doesn't leak. Back-filled episodes inside the already-released range stay visible.
- **Editing a schedule re-applies it from scratch.** Episodes past the new offset are re-locked, even ones the drip had already released — set the offset to where the users currently are if you don't want them to lose progress.
- **Specials and unnumbered episodes are never dripped.** Season 0 and episodes missing season/episode numbers are ignored by the drip logic and stay visible.
- **Cron runs in the server's time zone**, not the client's.

## Uninstall / cleanup

Delete all schedules in the plugin page **first** — deleting a schedule removes its `releasefin-*` tags from every episode and cleans the ReleaseFin entries out of every user's blocked-tags list (all users, not just the currently assigned ones). Then uninstall the plugin. If you uninstall without deleting schedules, the tags and blocked-tag entries remain and episodes stay hidden; you'd have to remove them by hand in the metadata editor and each user's parental controls.

## FAQ

**A series looks completely empty for a user — is that a bug?**
No: a schedule whose offset is empty starts with *all* episodes locked, so the series shows no episodes for assigned users until the first release time (or "Release now").

**Can different users on the same schedule be at different points?**
No. A schedule releases episodes for all its users together. Use one schedule per user (or per group of users watching together) for independent pacing.

**The server was down over the weekend — are the episodes lost?**
No. Missed occurrences accumulate: on the next tick after startup, all releases that were due during the downtime happen at once.

**Why 16:00 releases at a different hour than I expect?**
Cron expressions are evaluated in the Jellyfin *server's* local time zone. Check the server clock/`TZ`, not your client device.

**The schedule list says "ORPHANED (series deleted)".**
The series was removed from the library. The schedule no longer does anything useful; delete it to clean up the blocked-tag entries.

**Known limitation:** if every user assigned to a schedule is deleted from Jellyfin, the schedule still shows "Active" and keeps releasing on its cadence (harmless — with no blocked users the episodes are visible to everyone anyway). Delete or edit the schedule to tidy up.

## Development

```
dotnet build --nologo && dotnet test --nologo
```

Pure decision logic (tag math, episode ordering, cron counting) lives in `src/Jellyfin.Plugin.ReleaseFin/Core/` and is unit-tested; the Jellyfin glue is verified against a real server using the manual checklist in [`dev/README.md`](dev/README.md) (dockerized Jellyfin 10.10.7).
