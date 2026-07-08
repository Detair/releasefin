# Changelog

All notable changes to ReleaseFin are documented in this file.

Format loosely follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).

## [1.1.0] - 2026-07-08

### Added

- **Pacing modes** per schedule: *Accumulate* (default, unchanged behavior), *Watch-gated*
  (a tick only releases once every assigned user has watched what's already out; missed
  ticks are forfeited, not banked), and *Backlog cap* (never let more than N released items
  sit unwatched).
- **Notifications**: every release writes a Jellyfin Activity Log entry; an optional
  webhook URL (Settings section) POSTs a JSON payload on every release and every
  season-end pause.
- **Movie collections**: schedules can target a Jellyfin collection instead of a series,
  dripping its movies in premiere order (premiere date → production year → sort name).
- **Pause at season end** (series schedules): stop automatically at a season boundary
  instead of releasing into the next season; resume with one click, which re-arms the
  pause for the boundary after that.
- **Release up to…**: jump a schedule straight to a specific `SxxExx` in one action,
  instead of only stepping one batch at a time.
- **NO USERS status**: schedules whose assigned users were all deleted are now flagged
  in the list instead of silently looking normal.
- **Clean up stray tags**: a maintenance button that removes `releasefin-*` tags and
  blocked-tag entries belonging to no existing schedule (e.g. after manual config edits),
  without touching live schedules.
- **Jellyfin 10.11 support**: the plugin now multi-targets net8.0 (Jellyfin 10.10.x) and
  net9.0 (Jellyfin 10.11.x), published as two separate builds under one manifest entry;
  the plugin repository install picks the right one automatically.
- **Automated integration test suite** (`tests/integration/run.sh`): boots a real Jellyfin
  in docker/podman and scripts all of the above end to end; wired into CI on every push.

### Fixed

- New episodes/movies imported into a scheduled series/collection could, in rare
  reordering cases, have their lock decision computed from moving tag state; the release
  frontier is now a persisted value, and movie classification only runs on genuine new
  imports (never on unrelated metadata-refresh events), so an already-released item can
  no longer be silently re-hidden.
- A schedule create/edit could race a concurrent stray-tag cleanup and have its
  just-applied tags stripped before the schedule was registered; the config entry is now
  written before any tags are applied.

## [1.0.0] - 2026-07-07

Initial release.

- Assign a cron-style release schedule (daily/weekly presets or raw cron) to a series for
  selected accounts. Unreleased episodes are hidden via a per-schedule `releasefin-<id>`
  tag added to the users' "block items with tags" parental control — enforced server-side
  in every client.
- Configurable starting offset (`S01E05` = already released up to there).
- Missed release times accumulate and catch up after downtime.
- Newly imported episodes of a scheduled series arrive locked.
- Admin dashboard page: schedule list with progress/next-run/status, create/edit form,
  release-now, delete with full cleanup.
- Distributed via a self-hosted Jellyfin plugin repository manifest with a tagged-release
  GitHub Actions workflow.
