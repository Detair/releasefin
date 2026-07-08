# ReleaseFin Roadmap

Target: everything below ships as **v1.1.0**. Items execute in order; each lands on main
only after review and (where it touches runtime behavior) live verification against the
dockerized Jellyfin.

## 1. Automated integration tests in CI

Turn the manual `dev/README.md` checklist into `tests/integration/run.sh` (+ helpers):
starts a Jellyfin 10.10.7 container, completes the setup wizard via API, generates a tiny
ffmpeg library, then asserts the five checklist scenarios (offset visibility per user,
release-now, downtime catch-up, new-episode locking, delete cleanup) plus the two
regressions found in live testing (out-of-order import locking, no stray tags after
delete). Runs locally via podman/docker and in CI as a separate workflow job on every PR.

## 2. Stricter pacing modes

Per-schedule pacing option (default keeps today's behavior):
- `Accumulate` — current: every due tick releases.
- `WatchGated` — a due tick releases only if ALL assigned users have played every
  currently released episode (strictest anti-binge; missed ticks do not stack).
- `BacklogCap(N)` — a due tick releases only while released-but-unplayed (by all
  assigned users) episodes number fewer than N.

"Played" = Jellyfin user data `Played` flag per assigned user. UI: mode dropdown + cap
field. Decision: watched means watched by *all* users on the schedule.

## 3. Release notifications

Jellyfin 10.10 has no core notification system (moved to the Webhook plugin), so:
- Write an **activity log** entry on every release (visible in dashboard, and the Webhook
  plugin can forward ItemAdded/activity events).
- Optional **webhook URL** in plugin configuration: POST a JSON payload
  (schedule, series, episode, users) on each release. Covers Gotify/ntfy/Discord via
  their generic endpoints.

## 4. UI & status polish

- Flag schedules whose assigned users were all deleted: `NoUsers` status in DTO + list.
- Maintenance: "Clean up stray tags" button — removes `releasefin-*` tags that belong to
  no existing schedule, across the whole library (also the documented uninstall story).
- Per-episode override: "release up to S/E now" action for one-off exceptions.
- (Deliberately skipped: calendar view — low value for the complexity; the list already
  shows next-run per schedule.)

## 5. Scope extensions

- **Movie collections (BoxSets):** schedule a collection; drip its movies in
  premiere-date order. Model generalizes `SeriesId` → item + kind.
- **Pause at season end:** per-schedule toggle; when the next unreleased episode starts a
  new season, the schedule auto-disables and flags "season finished — re-enable to
  continue".
- (Multi-series schedules stay out: one schedule per series is the model; create several.)

## 6. Jellyfin 10.11 readiness

Research current 10.11 status (breaking changes known from earlier research: `User`
entity namespace move, net9.0). Deliverable depends on availability: if 10.11 is
released/RC, a compatibility branch with multi-target packaging and manifest `targetAbi`
entries per server version; otherwise a documented migration checklist in
`docs/10.11-migration.md`.

## Documentation (cross-cutting, done with each item + final pass)

README feature docs for each shipped item, `CHANGELOG.md` (missing today — create with
Keep-a-Changelog format, backfill v1.0.0), dev/README updates for the integration test
runner.
