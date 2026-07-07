# ReleaseFin dev environment

1. Build & deploy the plugin (Cronos.dll must be copied too):
   dotnet publish src/Jellyfin.Plugin.ReleaseFin -c Release -o /tmp/rf-publish
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
