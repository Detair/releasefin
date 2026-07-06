# ReleaseFin — Jellyfin Episode Drip-Release Plugin

## Context

Kids' accounts on Jellyfin can binge entire series in a weekend. No existing Jellyfin plugin solves this (verified via research 2026-07). ReleaseFin is a Jellyfin server plugin that simulates broadcast-style scheduling: an admin assigns a release schedule (cron-style) to a series for selected accounts, and episodes become visible one at a time as the schedule fires — like weekly TV, on your own server.

Repo: `releasefin` (greenfield — only README + LICENSE exist).

## Decisions made during brainstorming

| Question | Decision |
|---|---|
| Missed ticks (kid didn't watch) | **Accumulate freely** — each tick releases the next episode regardless of watch state |
| Scope | **Configurable per series** — admin assigns schedules to specific series+user combos; unscheduled series are untouched |
| Unreleased episodes | **Completely hidden** from the restricted account |
| Drip start point | **Configurable offset** — default S01E01, admin can mark "already released up to SxxEyy" |
| Schedule input | **Presets + raw cron** — daily/weekly presets plus advanced cron field |
| Enforcement | **Per-schedule tag + BlockedTags user policy** (server-side, works in all clients) |

## Architecture

**Enforcement mechanism (core trick):** Jellyfin's parental controls support `BlockedTags` in a user's policy — items carrying a blocked tag are invisible to that user, enforced server-side across every client (web, mobile, TV). The plugin:

1. On schedule assignment: tags every unreleased episode of the series with a namespaced tag `releasefin-<scheduleId>`, and adds that tag to each assigned user's `BlockedTags` policy.
2. On each schedule tick: removes the tag from the next episode(s) in aired order → they appear for the user.
3. On schedule deletion: removes all its tags and cleans the user policies.

This is a proven pattern (an existing third-party plugin drives BlockedTags from scheduled tasks for date-range visibility).

**Release pointer is derived, not stored:** the "next episode to release" is simply the first still-tagged episode in aired order. Self-healing — if an admin manually removes a tag in the metadata editor, the plugin treats it as released.

## Components

1. **Plugin core** (`Plugin.cs`) — standard Jellyfin plugin scaffold from `jellyfin-plugin-template`, C# / .NET 8, targeting Jellyfin 10.10+. Configuration persisted via Jellyfin's plugin configuration store.

2. **Data model** — `ReleaseSchedule`:
   - `Id`, `Name`, `SeriesId`, `UserIds[]`
   - `CronExpression` (presets in the UI compile down to cron; parsed with the Cronos library)
   - `EpisodesPerTick` (default 1)
   - `InitialReleasedUpTo` (optional SxxEyy offset applied at assignment time)
   - `Enabled`, `LastRunUtc`
   - Two users sharing a schedule advance together; per-user pacing = create separate schedules.

3. **Enforcement engine** (`ReleaseManager`) — applies/removes tags via `ILibraryManager`, updates user policies via `IUserManager`. Only ever touches `releasefin-*` tags and its own BlockedTags entries; never disturbs other parental-control settings. Release order: `ParentIndexNumber`/`IndexNumber` (aired order), specials (season 0) excluded by default.

4. **Scheduler** (`IHostedService` with a timer) — evaluates each enabled schedule's cron expression every minute. On tick: release next `EpisodesPerTick` episodes. **Downtime catch-up:** on startup, count cron occurrences between `LastRunUtc` and now and release that many episodes (consistent with "accumulate freely").

5. **Library watcher** — subscribes to `ILibraryManager.ItemAdded/ItemUpdated`; newly imported episodes of a scheduled series that sort after the release pointer get tagged automatically (ongoing shows stay locked as new episodes arrive).

6. **Admin UI** — plugin dashboard configuration page (HTML/JS, standard plugin config page):
   - Schedule list with status: series, users, next fire time, progress ("12/48 released")
   - Create/edit: series picker, user multi-select, preset schedule builder (daily/weekly + time + weekday) with an "advanced: raw cron" toggle, initial offset, episodes-per-tick
   - Actions: enable/disable, "release next episode now" button, delete (with full tag/policy cleanup)
   - Cron validation with human-readable preview ("Mon/Wed/Fri at 16:00")

## Edge cases & error handling

- **All episodes released** → schedule marked complete, stops ticking, stays listed.
- **Series deleted from library** → schedule flagged orphaned in UI; policy entries cleaned up.
- **User deleted** → drop from schedule; last user removed → schedule disabled + flagged.
- **Invalid cron** → rejected at save time in UI with error message.
- **Plugin uninstall** → documented cleanup task (button: "remove all ReleaseFin tags & policy entries").
- Known cosmetic caveat: a series with zero released episodes may appear empty (not hidden) in the library — acceptable; can later add optional series-level tag to hide it entirely.

## Testing & verification

- **Unit tests (xunit):** cron evaluation & catch-up counting, release-pointer derivation from tag state, aired-order sorting incl. multi-season and specials exclusion, policy update idempotency.
- **Integration/manual:** docker-compose Jellyfin dev instance with a sample library; checklist:
  1. Assign daily schedule to a series for "Kids" → all episodes past offset vanish for Kids (web + a second client), remain visible for admin.
  2. Fire tick (or "release now") → exactly one new episode appears.
  3. Restart server after skipping 3 ticks → 3 episodes released.
  4. Add new episode file to a scheduled series → arrives hidden.
  5. Delete schedule → everything visible again, no stray tags/policy entries.

## Build sequence (high level)

1. Scaffold from jellyfin-plugin-template; CI build producing the plugin DLL.
2. Data model + configuration persistence.
3. ReleaseManager (tagging + policy management) with unit tests.
4. Scheduler with cron (Cronos) + catch-up logic.
5. Library watcher for new episodes.
6. Admin configuration UI.
7. Docs (README: install, usage, cleanup) + plugin repo manifest for easy installation.

## Agent team (token-optimized execution)

Create project agents in `.claude/agents/` so implementation runs subagent-driven: the main session stays a lean orchestrator, each task executes in a fresh scoped context, and cheap models handle mechanical work. Token-optimization rules baked into every agent:

- **Right-sized models:** Haiku for mechanical/lookup work, Sonnet for implementation, main session (orchestrator) reserves itself for architecture decisions and plan sequencing.
- **Minimal tool grants:** each agent gets only the tools it needs (smaller prompts, fewer permission round-trips).
- **Conclusions, not dumps:** every agent's instructions end with "return a concise summary/answer, never paste whole files."
- **Fresh context per task:** implementer agents receive a self-contained task brief (files, acceptance criteria) instead of conversation history.

### Agent roster

| File | Model | Tools | Role |
|---|---|---|---|
| `jellyfin-researcher.md` | haiku | WebSearch, WebFetch, context7 MCP, Read, Grep, Glob | Look up Jellyfin plugin API facts (ILibraryManager, IUserManager, UserPolicy/BlockedTags, plugin-template, Cronos). Returns short API notes with exact signatures/namespaces — no page dumps. |
| `plugin-implementer.md` | sonnet | Read, Edit, Write, Bash, Glob, Grep | Implements ONE task from the implementation plan using TDD (test first, then code, run `dotnet test`). Receives a self-contained brief; returns diff summary + test results. |
| `ui-builder.md` | sonnet | Read, Edit, Write, Bash, Glob, Grep | Builds the dashboard config page (HTML/JS embedded resources, Jellyfin config-page conventions). Kept separate from C# implementer so web-UI context doesn't bloat backend tasks. |
| `build-verifier.md` | haiku | Bash, Read, Grep | Runs `dotnet build` / `dotnet test`, triages output, reports pass/fail with only the failing test names + relevant error lines. |
| `code-reviewer.md` | sonnet | Read, Grep, Glob, Bash (read-only git) | Reviews the diff of a completed task against the spec: correctness, Jellyfin API misuse, tag/policy safety (must never touch non-`releasefin-*` tags). High-confidence findings only. |
| `docs-writer.md` | haiku | Read, Write, Edit, Glob | README (install/usage/cleanup), plugin repo manifest, CHANGELOG. Runs at the end, cheap. |

Each agent file: standard frontmatter (`name`, `description` with clear trigger conditions, `tools`, `model`) + a body with role, project context pointer (spec path), hard rules, and output-format contract.

### Orchestration workflow

1. Main session works through the implementation plan task-by-task (superpowers:subagent-driven-development).
2. Per task: dispatch `plugin-implementer` (or `ui-builder`) with a brief → `build-verifier` confirms green → `code-reviewer` checks the diff → main session commits.
3. `jellyfin-researcher` is dispatched on demand whenever an API question arises, instead of loading docs into the main context.
4. Independent tasks (e.g. docs vs. UI) may run as parallel background agents.

## Deliverable of the next step

1. Save the design as a spec (`docs/superpowers/specs/2026-07-06-releasefin-design.md`) and commit it.
2. Create the six agent files in `.claude/agents/` as specified above and commit.
3. Produce the detailed implementation plan (superpowers:writing-plans), then execute it subagent-driven per the workflow above.
