# ReleaseFin dev environment

## Jellyfin 10.10 / 10.11 dual target

The plugin multi-targets `net8.0` (Jellyfin 10.10.x, targetAbi 10.10.0.0) and `net9.0`
(Jellyfin 10.11.x, targetAbi 10.11.0.0) — see `Jellyfin.Plugin.ReleaseFin.csproj` and the
narrow `#if NET9_0` blocks in `ReleaseManager.cs`/`ReleaseNotifier.cs` for the handful of
API differences (10.11 moved `User`/`ActivityLog`/`PreferenceKind` from `Jellyfin.Data` to
`Jellyfin.Database.Implementations`, replaced `IUserManager.Users` with `GetUsers()`, and
made `User.GetPreference`/`SetPreference` extension methods instead of instance members).
**Because the project now declares both TFMs, you need an SDK that recognizes net9.0 to
build or test *anything* here, even net8.0-only** — an 8.0-only SDK fails outright with
`NETSDK1045` just evaluating the project. Install a 9.0.x (or newer) SDK alongside your
existing one (e.g. `dotnet-install.sh -Channel 9.0 -InstallDir ~/.dotnet9`, plus the 8.0
runtime — `-Channel 8.0 -Runtime dotnet`/`-Runtime aspnetcore` — into that same directory
so `dotnet test` can actually execute the net8.0 test host) and point `PATH`/`DOTNET_ROOT`
at it for all `dotnet` commands in this repo, including net8.0-only ones.
`dotnet publish` additionally requires `-f net8.0` or `-f net9.0` explicitly once a project
multi-targets — plain `dotnet publish` errors with `NETSDK1129`.

`tests/integration/run.sh` defaults to net8.0/Jellyfin 10.10.7. To run the same suite
against the net9.0/10.11 build instead:

    RF_PUBLISH_TFM=net9.0 RF_JELLYFIN_IMAGE=docker.io/jellyfin/jellyfin:10.11.11 \
      RF_CONTAINER_TOOL=podman tests/integration/run.sh

1. Build & deploy the plugin (Cronos.dll must be copied too; pick the TFM matching your
   test server — net8.0 for 10.10.x, net9.0 for 10.11.x):
   dotnet publish src/Jellyfin.Plugin.ReleaseFin -c Release -f net8.0 -o /tmp/rf-publish
   mkdir -p dev/config/plugins/ReleaseFin
   cp /tmp/rf-publish/Jellyfin.Plugin.ReleaseFin.dll /tmp/rf-publish/Cronos.dll dev/config/plugins/ReleaseFin/
2. Put a small TV library under dev/media with correct "Show S01E01" naming. Use tiny
   real video files (a 1-second clip is enough; empty files are not reliably indexed):
   ffmpeg -f lavfi -i color=c=blue:s=320x240:d=1 -c:v libx264 ep.mkv
3. docker compose -f dev/docker-compose.yml up -d (podman works identically) and complete
   setup at http://localhost:8096 (create an admin and a "Kids" user).
4. Restart the container after every plugin redeploy.

## Verification checklist (manual; requires the environment above)

1. Dashboard → Plugins → ReleaseFin: assign a daily schedule to a series for Kids with
   offset S01E02 → as Kids (web + one other client), only S01E01–S01E02 visible; as
   admin, everything visible with `releasefin-*` tags in the metadata editor.
2. "Release now" → exactly one more episode appears for Kids.
3. Stop the container, move `LastRunUtc` back 3 days in
   `dev/config/plugins/configurations/Jellyfin.Plugin.ReleaseFin.xml` (or wait across
   ticks), start → 3 episodes released on the startup catch-up tick.
4. Add a new episode file to the scheduled series, run a library scan → it arrives
   hidden for Kids.
5. Delete the schedule → all episodes visible for Kids; no `releasefin-*` tags remain
   on items; Kids' parental "block items with tags" list has no ReleaseFin entries.
