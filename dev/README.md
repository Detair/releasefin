# ReleaseFin dev environment

1. Build & deploy the plugin (Cronos.dll must be copied too):
   dotnet publish src/Jellyfin.Plugin.ReleaseFin -c Release -o /tmp/rf-publish
   mkdir -p dev/config/plugins/ReleaseFin
   cp /tmp/rf-publish/Jellyfin.Plugin.ReleaseFin.dll /tmp/rf-publish/Cronos.dll dev/config/plugins/ReleaseFin/
2. Put a small TV library under dev/media (a couple of series, 2 seasons each; empty
   video files with correct S01E01 naming are enough for metadata-only testing).
3. docker compose -f dev/docker-compose.yml up -d and complete setup at http://localhost:8096
   (create an admin and a "Kids" user).
4. Restart the container after every plugin redeploy.

## Verification checklist (manual; requires the environment above)

1. Dashboard → Plugins → ReleaseFin: assign a daily schedule to a series for Kids with
   offset S01E02 → as Kids (web + one other client), only S01E01–S01E02 visible; as
   admin, everything visible with `releasefin-*` tags in the metadata editor.
2. "Release now" → exactly one more episode appears for Kids.
3. Stop the container, move `LastRunUtc` back 3 days in
   `config/plugins/configurations/ReleaseFin.xml` (or wait across ticks), start →
   3 episodes released.
4. Add a new episode file to the scheduled series, run a library scan → it arrives
   hidden for Kids.
5. Delete the schedule → all episodes visible for Kids; no `releasefin-*` tags remain
   on items; Kids' parental "block items with tags" list has no ReleaseFin entries.
