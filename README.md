# releasefin

ReleaseFin is a Jellyfin plugin (10.10.x and 10.11.x) that drip-releases episodes of a series — or movies in a collection — to selected accounts on a schedule, like weekly TV. You pick a series or collection, the users it applies to, a cadence (daily, weekly, or raw cron), and optionally how strict the pacing should be; unreleased items are hidden from those users until their release time passes. Example: your kids' account gets one episode of a show every day at 16:00 instead of the whole box set at once — everyone else on the server still sees everything.

## How hiding works

ReleaseFin does not delete or move anything. Each schedule tags its unreleased episodes with a `releasefin-<schedule-id>` tag and adds that tag to the assigned users' "Block items with tags" parental control. For those users the episodes simply don't appear; for admins and unassigned users nothing changes. You can see (and, if you must, hand-edit) the tags in the metadata editor — the plugin only ever touches tags starting with `releasefin-` and its own blocked-tag entries.

At each release time the scheduler removes the tag from the next item(s) — episodes in aired order, or movies in premiere order for collections — making them visible. The schedule is evaluated every minute against the cron expression, in the **server's local time zone**, subject to the schedule's pacing mode (see below).

## Install

**Via plugin repository (recommended):**

1. Dashboard → Plugins → Repositories → **+** and add:
   ```
   https://raw.githubusercontent.com/Detair/releasefin/main/manifest.json
   ```
2. Catalog → **ReleaseFin** → Install, then restart Jellyfin.

Updates show up in the catalog automatically.

**Manually:** download the zip matching your server version from the [latest release](https://github.com/Detair/releasefin/releases/latest) (or build it yourself: `dotnet publish src/Jellyfin.Plugin.ReleaseFin -c Release -f net8.0 -o publish` for Jellyfin 10.10.x, `-f net9.0` for 10.11.x) and extract **both** `Jellyfin.Plugin.ReleaseFin.dll` and `Cronos.dll` into a `ReleaseFin` folder inside your Jellyfin plugin directory (e.g. `config/plugins/ReleaseFin/`). Cronos.dll is required — the plugin will not load without it. Restart Jellyfin.

Supports Jellyfin 10.10.x and 10.11.x, built and released as two separate DLLs (net8.0/targetAbi 10.10.0.0 and net9.0/targetAbi 10.11.0.0) listed as separate versions in the same manifest; installing via the plugin repository picks the right one automatically, same as any other multi-version Jellyfin plugin.

## Usage

Open Dashboard → Plugins → ReleaseFin and click **New schedule**:

- **Name** — a label for the schedule list.
- **Type** — *Series* (drip episodes in aired order) or *Movie collection* (drip a Jellyfin collection's movies in premiere order).
- **Series / Collection** — the item to drip; the picker switches between your series and your collections depending on Type.
- **Users** — the accounts the drip applies to. Everyone else is unaffected.
- **Schedule** — *Daily* or *Weekly* presets (pick a time of day, and a weekday for weekly), or *Advanced* for a raw 5-field cron expression (`min hour dom mon dow`). A preview shows the next three release times.
- **Episodes per release** — how many items each tick unlocks (default 1).
- **Pacing** — how strictly the schedule holds back new releases even when the cron fires (see below).
- **Already released up to** *(Series only)* — an initial offset like `S01E05`: episodes up to and including it start visible; leave empty to start with everything locked. A series with zero released episodes appears empty (not hidden) to the assigned users until the first release.
- **Pause at season end** *(Series only)* — see below.
- **Enabled** — disabled schedules keep their tags in place but stop releasing.

Saving applies the schedule immediately: items past the offset are tagged, and the tag is added to each selected user's blocked list. The schedule list shows progress ("12/48 released"), the next release time, and status. **Release now** unlocks the next batch on demand; **Release up to…** lets you jump straight to a specific `SxxExx` in one action; **Delete** (with confirmation) removes the schedule and makes everything visible again.

### Pacing modes

By default a schedule releases on every due tick regardless of what the users have watched ("accumulate" — missed ticks after downtime all release at once, per below). Two stricter modes are available per schedule:

- **Watch-gated** — a tick only releases the next item if every assigned user has watched everything already released. A missed tick under this mode is **forfeited, not banked**: it does not stack up for later like Accumulate does.
- **Backlog cap (N)** — a tick releases up to the schedule's normal amount, but never lets the number of released-but-unwatched (by all assigned users) items exceed N.

Both modes use Jellyfin's own played/watched state, so marking an episode watched in any client unblocks the next release.

### Notifications

Every release writes an entry to Jellyfin's Activity Log (Dashboard → Activity), naming the schedule and the item(s) released. You can additionally set a **Webhook URL** in the Settings section at the top of the plugin page: ReleaseFin POSTs a small JSON payload (schedule, series/collection, items, users, and an `"event"` field — `"released"` or `"seasonPaused"`) to that URL on every release and every season-end pause. Leave it empty to disable. Point it at something like a ntfy/Gotify endpoint if you want a phone notification when a new episode drops.

### Movie collections

Create a Jellyfin collection (BoxSet) first, then create a schedule with Type *Movie collection* pointing at it. Movies drip in premiere order (premiere date, then production year, then sort name as a tiebreaker) rather than aired order. Everything else — pacing, notifications, cleanup, offsets are not supported for collections (they always start fully locked).

### Pause at season end

For a Series schedule, enabling **Pause at season end** stops the drip automatically the moment the next release would cross into a new season, instead of releasing it. The schedule list shows **SEASON END (resume to continue)**; an activity log entry (and webhook, if configured) records the pause. Click **Resume** to release the first episode of the new season and re-arm the pause for the boundary after that. This is the closest thing to "wait for the next season to actually air" without you having to remember to disable the schedule yourself.

### Maintenance

The **Clean up stray tags** button in Settings removes any `releasefin-*` tag (from items) or blocked-tag entry (from users) that doesn't belong to a currently-existing schedule — leftovers from manually editing the plugin's config file, or from before an uninstall. It reports how many items and users it touched. Live schedules are never affected.

### Things worth knowing

- **Schedules are per-series (or per-collection), shared by their users.** All accounts on one schedule see the same set of released items and advance together. If two kids should watch at different paces, give each their own schedule (the tags are per-schedule, so they don't conflict).
- **Missed release times catch up under Accumulate pacing.** If the server is off for three days, the next tick counts all three missed occurrences and releases three days' worth of items at once. Watch-Gated and Backlog Cap pacing cap this instead of letting it all through — see Pacing modes above.
- **New downloads arrive locked.** When an episode of a scheduled series is imported past the released frontier, it is tagged immediately, so a season pack landing overnight doesn't leak. Back-filled episodes inside the already-released range stay visible. (Movies added to an already-scheduled collection *after* the schedule was created are only auto-classified if the movie file itself is a fresh import — a pre-existing movie merely linked into the collection later needs a schedule edit or "Release up to…" to be swept up.)
- **Editing a schedule re-applies it from scratch.** Items past the new offset are re-locked, even ones the drip had already released, and any season-end pause is cleared — set the offset to where the users currently are if you don't want them to lose progress.
- **A schedule whose users were all deleted shows NO USERS** in the list instead of silently continuing to look normal; delete or reassign it.
- **Specials and unnumbered episodes are never dripped.** Season 0 and episodes missing season/episode numbers are ignored by the drip logic and stay visible.
- **Cron runs in the server's time zone**, not the client's.

## Uninstall / cleanup

Delete all schedules in the plugin page **first** — deleting a schedule removes its `releasefin-*` tags from every item and cleans the ReleaseFin entries out of every user's blocked-tags list (all users, not just the currently assigned ones). Then uninstall the plugin. If you uninstall without deleting schedules, the tags and blocked-tag entries remain and items stay hidden; use the **Clean up stray tags** button (Settings section) before or after uninstalling to sweep up anything left behind, or remove them by hand in the metadata editor and each user's parental controls.

## FAQ

**A series looks completely empty for a user — is that a bug?**
No: a schedule whose offset is empty starts with *all* episodes locked, so the series shows no episodes for assigned users until the first release time (or "Release now").

**Can different users on the same schedule be at different points?**
No. A schedule releases episodes for all its users together. Use one schedule per user (or per group of users watching together) for independent pacing.

**The server was down over the weekend — are the episodes lost?**
No. Missed occurrences accumulate: on the next tick after startup, all releases that were due during the downtime happen at once.

**Why 16:00 releases at a different hour than I expect?**
Cron expressions are evaluated in the Jellyfin *server's* local time zone. Check the server clock/`TZ`, not your client device.

**The schedule list says "ORPHANED (item deleted)".**
The series or collection was removed from the library. The schedule no longer does anything useful; delete it to clean up the blocked-tag entries.

**The schedule list says "NO USERS (all assigned users deleted)".**
Every account this schedule was assigned to has since been deleted from Jellyfin. It's harmless (with no blocked users, items are visible to everyone) but doesn't do anything — delete or edit the schedule to tidy up.

**I edited the plugin's XML config directly and now there are tags/blocked-tag entries with no matching schedule.**
Use the **Clean up stray tags** button in the Settings section — it removes only `releasefin-*` tags and blocked-tag entries that don't belong to a currently-existing schedule, and reports how many items and users it touched.

## Development

```
dotnet build --nologo && dotnet test --nologo
```

`dotnet build`/`dotnet publish` build both the net8.0 (Jellyfin 10.10) and net9.0 (Jellyfin
10.11) targets; `dotnet publish` needs an explicit `-f net8.0`/`-f net9.0` once more than one
target framework is present. See [`dev/README.md`](dev/README.md) for the toolchain note this
requires (an SDK that recognizes net9.0, even for net8.0-only work).

Pure decision logic (tag math, episode ordering, cron counting) lives in `src/Jellyfin.Plugin.ReleaseFin/Core/` and is unit-tested; the Jellyfin glue is verified against a real server using `tests/integration/run.sh` (dockerized Jellyfin, 10.10.7 by default, 10.11.11 also exercised in CI) and the manual checklist in [`dev/README.md`](dev/README.md).
