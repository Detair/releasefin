#!/usr/bin/env python3
"""End-to-end integration test for ReleaseFin against a real Jellyfin server.

Launched by run.sh, which builds the plugin, prepares the config/media dirs, and
starts the container. This script completes the setup wizard via the API, builds a
test library, and asserts the release-drip scenarios from dev/README.md plus the
regressions found during live verification (batch import locking, no stray tags
after delete). stdlib only.
"""
import datetime
import http.server
import json
import os
import pathlib
import re
import shutil
import subprocess
import sys
import threading
import time
import urllib.error
import urllib.request

BASE = f"http://127.0.0.1:{os.environ.get('RF_PORT', '8097')}"
TOOL = os.environ["RF_CONTAINER_TOOL"]
NAME = os.environ["RF_CONTAINER_NAME"]
CONFIG = pathlib.Path(os.environ["RF_CONFIG_DIR"])
MEDIA = pathlib.Path(os.environ["RF_MEDIA_DIR"])
FIXTURE = pathlib.Path(__file__).parent / "fixtures" / "ep.mkv"
AUTH_HDR = 'MediaBrowser Client="rf-it", Device="ci", DeviceId="rf-it", Version="1.0"'
SERIES_DIR = MEDIA / "Test Show (2020)" / "Season 01"
PLUGIN_CONFIG = CONFIG / "plugins" / "configurations" / "Jellyfin.Plugin.ReleaseFin.xml"


def api(method, path, token=None, body=None):
    req = urllib.request.Request(BASE + path, method=method)
    # The legacy X-Emby-Authorization header must not accompany a token header:
    # Jellyfin's auth handler prefers it and rejects the request as tokenless.
    if token:
        req.add_header("Authorization", f'MediaBrowser Token="{token}"')
    else:
        req.add_header("X-Emby-Authorization", AUTH_HDR)
    data = None
    if body is not None:
        data = json.dumps(body).encode()
        req.add_header("Content-Type", "application/json")
    try:
        with urllib.request.urlopen(req, data=data, timeout=30) as r:
            raw = r.read()
            return r.status, json.loads(raw) if raw.strip() else None
    except urllib.error.HTTPError as e:
        return e.code, None
    except (urllib.error.URLError, ConnectionError, TimeoutError):
        return 0, None


def check(desc, cond, detail=""):
    if cond:
        print(f"  ok: {desc}")
    else:
        print(f"  FAIL: {desc} {detail}")
        subprocess.run([TOOL, "logs", "--tail", "40", NAME])
        sys.exit(1)


def wait_for(desc, fn, timeout=120):
    deadline = time.time() + timeout
    while time.time() < deadline:
        result = fn()
        if result:
            return result
        time.sleep(2)
    check(f"timeout waiting for {desc}", False)


def wait_server():
    wait_for("server up", lambda: api("GET", "/System/Info/Public")[0] == 200, 180)


def ctl(*args):
    subprocess.run([TOOL, *args], check=True, capture_output=True)


def episodes(token, user_id):
    _, d = api(
        "GET",
        f"/Items?IncludeItemTypes=Episode&Recursive=true&userId={user_id}"
        "&SortBy=IndexNumber&Fields=Tags",
        token,
    )
    return d["Items"] if d else []


def add_episode(n):
    shutil.copy(FIXTURE, SERIES_DIR / f"Test Show S01E{n:02d}.mkv")


def rewind_last_run(hours):
    """Stop the container, move the (single) schedule's LastRunUtc back, restart.

    Returns the rewound timestamp so callers can wait until the scheduler has
    ticked (it rewrites LastRunUtc on the first due tick after startup)."""
    ctl("stop", NAME)
    xml = PLUGIN_CONFIG.read_text()
    past = (datetime.datetime.now(datetime.timezone.utc)
            - datetime.timedelta(hours=hours, minutes=2)).strftime("%Y-%m-%dT%H:%M:%SZ")
    xml, n = re.subn(r"<LastRunUtc>[^<]*</LastRunUtc>",
                     f"<LastRunUtc>{past}</LastRunUtc>", xml)
    check("rewound LastRunUtc in plugin config", n == 1, f"replacements={n}")
    PLUGIN_CONFIG.write_text(xml)
    ctl("start", NAME)
    wait_server()
    return past


HOOK_BODIES = []


class HookHandler(http.server.BaseHTTPRequestHandler):
    """Captures webhook POST bodies for scenario 7."""

    def do_POST(self):
        length = int(self.headers.get("Content-Length") or 0)
        try:
            HOOK_BODIES.append(json.loads(self.rfile.read(length)))
        except ValueError:
            HOOK_BODIES.append(None)
        self.send_response(200)
        self.end_headers()

    def log_message(self, *args):
        pass


def start_hook_listener():
    """Background HTTP listener on an ephemeral port; the container reaches it
    via rf-host.internal (--add-host=...:host-gateway in run.sh)."""
    listener = http.server.HTTPServer(("0.0.0.0", 0), HookHandler)
    threading.Thread(target=listener.serve_forever, daemon=True).start()
    return listener, listener.server_address[1]


def wait_tick_persisted(past):
    """Wait until the startup catch-up tick ran (LastRunUtc rewritten past the rewind)."""
    wait_for("scheduler tick persisted",
             lambda: f"<LastRunUtc>{past}</LastRunUtc>" not in PLUGIN_CONFIG.read_text())


def main():
    print("== setup wizard")
    wait_server()
    api("POST", "/Startup/Configuration", body={
        "UICulture": "en-US", "MetadataCountryCode": "US",
        "PreferredMetadataLanguage": "en"})
    api("GET", "/Startup/User")
    api("POST", "/Startup/User", body={"Name": "admin", "Password": "it-Passw0rd"})
    status, _ = api("POST", "/Startup/Complete")
    check("wizard completed", status == 204, f"status={status}")

    _, auth = api("POST", "/Users/AuthenticateByName",
                  body={"Username": "admin", "Pw": "it-Passw0rd"})
    admin, admin_id = auth["AccessToken"], auth["User"]["Id"]

    print("== library and users")
    SERIES_DIR.mkdir(parents=True, exist_ok=True)
    for n in range(1, 7):
        add_episode(n)
    status, _ = api("POST",
                    "/Library/VirtualFolders?name=Shows&collectionType=tvshows"
                    "&paths=%2Fmedia&refreshLibrary=true",
                    admin, body={"LibraryOptions": {"EnableInternetProviders": False}})
    check("library created", status == 204, f"status={status}")
    _, kid_user = api("POST", "/Users/New", admin,
                      body={"Name": "kids", "Password": "it-kids"})
    kid_id = kid_user["Id"]
    _, kauth = api("POST", "/Users/AuthenticateByName",
                   body={"Username": "kids", "Pw": "it-kids"})
    kid = kauth["AccessToken"]
    wait_for("6 episodes indexed", lambda: len(episodes(admin, admin_id)) == 6)
    _, d = api("GET", "/Items?IncludeItemTypes=Series&Recursive=true", admin)
    series_id = d["Items"][0]["Id"]

    print("== scenario 1: schedule with offset S01E02")
    status, sched = api("POST", "/ReleaseFin/Schedules", admin, body={
        "Name": "it-drip", "SeriesId": series_id, "UserIds": [kid_id],
        "CronExpression": "0 16 * * *", "EpisodesPerTick": 1,
        "InitialSeason": 1, "InitialEpisode": 2, "Enabled": True})
    check("schedule created", status == 200 and sched["Released"] == 2,
          f"status={status} sched={sched}")
    sched_id = sched["Id"]
    wait_for("kids sees exactly E01-E02",
             lambda: [e["IndexNumber"] for e in episodes(kid, kid_id)] == [1, 2])
    tagged = [e["IndexNumber"] for e in episodes(admin, admin_id)
              if any(t.startswith("releasefin-") for t in e.get("Tags", []))]
    check("admin sees tags on E03-E06", tagged == [3, 4, 5, 6], f"tagged={tagged}")
    _, kuser = api(f"GET", f"/Users/{kid_id}", admin)
    check("kids policy has blocked tag",
          any(t.startswith("releasefin-") for t in kuser["Policy"]["BlockedTags"]))

    print("== scenario 2: release now")
    status, sched = api("POST", f"/ReleaseFin/Schedules/{sched_id}/ReleaseNow", admin)
    check("release-now returns 3/6", status == 200 and sched["Released"] == 3,
          f"sched={sched}")
    wait_for("kids sees E01-E03",
             lambda: [e["IndexNumber"] for e in episodes(kid, kid_id)] == [1, 2, 3])

    print("== scenario 3: downtime catch-up (3 missed days)")
    rewind_last_run(72)
    wait_for("catch-up released exactly 3 (kids sees E01-E06)",
             lambda: [e["IndexNumber"] for e in episodes(kid, kid_id)]
             == [1, 2, 3, 4, 5, 6])

    print("== scenario 4: batch import arrives locked (order-independence)")
    add_episode(7)
    add_episode(8)
    api("POST", "/Library/Refresh", admin)
    wait_for("E07+E08 indexed and both tagged", lambda: sorted(
        e["IndexNumber"] for e in episodes(admin, admin_id)
        if any(t.startswith("releasefin-") for t in e.get("Tags", []))) == [7, 8])
    kids_sees = [e["IndexNumber"] for e in episodes(kid, kid_id)]
    check("kids still sees only E01-E06", kids_sees == [1, 2, 3, 4, 5, 6],
          f"kids={kids_sees}")

    print("== scenario 5: delete cleans up completely")
    status, _ = api("DELETE", f"/ReleaseFin/Schedules/{sched_id}", admin)
    check("delete accepted", status == 204, f"status={status}")
    wait_for("kids sees all 8", lambda: len(episodes(kid, kid_id)) == 8)
    strays = [(e["IndexNumber"], t) for e in episodes(admin, admin_id)
              for t in e.get("Tags", []) if t.startswith("releasefin-")]
    check("zero stray releasefin tags", strays == [], f"strays={strays}")
    _, kuser = api("GET", f"/Users/{kid_id}", admin)
    check("kids policy cleaned", kuser["Policy"]["BlockedTags"] == [])
    _, scheds = api("GET", "/ReleaseFin/Schedules", admin)
    check("schedule list empty", scheds == [])

    print("== scenario 6: watch-gated pacing")
    status, sched = api("POST", "/ReleaseFin/Schedules", admin, body={
        "Name": "it-gated", "SeriesId": series_id, "UserIds": [kid_id],
        "CronExpression": "0 16 * * *", "EpisodesPerTick": 1,
        "Pacing": "WatchGated", "Enabled": True})
    check("gated schedule created with 0 released",
          status == 200 and sched["Released"] == 0 and sched["Pacing"] == "WatchGated",
          f"status={status} sched={sched}")
    sched_id = sched["Id"]
    wait_for("kids sees nothing", lambda: episodes(kid, kid_id) == [])

    wait_tick_persisted(rewind_last_run(25))
    wait_for("gated tick released exactly E01",
             lambda: [e["IndexNumber"] for e in episodes(kid, kid_id)] == [1])

    wait_tick_persisted(rewind_last_run(25))  # E01 still unplayed -> tick must not release
    kids_sees = [e["IndexNumber"] for e in episodes(kid, kid_id)]
    check("unwatched E01 gates the tick (still only E01)", kids_sees == [1],
          f"kids={kids_sees}")

    ep1_id = episodes(kid, kid_id)[0]["Id"]
    status, _ = api("POST", f"/UserPlayedItems/{ep1_id}?userId={kid_id}", kid)
    if status == 404:  # pre-10.9 fallback route
        status, _ = api("POST", f"/Users/{kid_id}/PlayedItems/{ep1_id}", admin)
    check("marked E01 played for kids", status == 200, f"status={status}")

    wait_tick_persisted(rewind_last_run(25))
    wait_for("watched E01 unlocks the next tick (kids sees E01-E02)",
             lambda: [e["IndexNumber"] for e in episodes(kid, kid_id)] == [1, 2])

    status, _ = api("DELETE", f"/ReleaseFin/Schedules/{sched_id}", admin)
    check("gated schedule delete accepted", status == 204, f"status={status}")
    wait_for("kids sees all 8 again", lambda: len(episodes(kid, kid_id)) == 8)

    print("== scenario 7: notifications (webhook + activity log)")
    listener, hook_port = start_hook_listener()
    hook_url = f"http://rf-host.internal:{hook_port}/hook"
    status, _ = api("PUT", "/ReleaseFin/Settings", admin, body={"WebhookUrl": "not a url"})
    check("invalid webhook url rejected", status == 400, f"status={status}")
    status, settings = api("PUT", "/ReleaseFin/Settings", admin,
                           body={"WebhookUrl": hook_url})
    check("webhook url saved",
          status == 200 and settings["WebhookUrl"] == hook_url,
          f"status={status} settings={settings}")
    status, settings = api("GET", "/ReleaseFin/Settings", admin)
    check("settings round-trip", status == 200 and settings["WebhookUrl"] == hook_url,
          f"status={status} settings={settings}")

    status, sched = api("POST", "/ReleaseFin/Schedules", admin, body={
        "Name": "it-notify", "SeriesId": series_id, "UserIds": [kid_id],
        "CronExpression": "0 16 * * *", "EpisodesPerTick": 1, "Enabled": True})
    check("notify schedule created", status == 200 and sched["Released"] == 0,
          f"status={status} sched={sched}")
    sched_id = sched["Id"]
    status, sched = api("POST", f"/ReleaseFin/Schedules/{sched_id}/ReleaseNow", admin)
    check("release-now released E01", status == 200 and sched["Released"] == 1,
          f"sched={sched}")

    hook = wait_for("webhook delivery", lambda: next(iter(HOOK_BODIES), None), 30)
    # the plugin serializes Guids dashed; the Jellyfin API returns them dash-less
    hook_users = [u.replace("-", "").lower() for u in hook["users"]]
    check("webhook body names S01E01 of the series",
          hook["schedule"] == "it-notify" and "Test Show" in hook["series"]
          and [(e["season"], e["episode"]) for e in hook["episodes"]] == [(1, 1)]
          and kid_id.replace("-", "").lower() in hook_users,
          f"hook={hook}")

    def released_activity():
        _, d = api("GET", "/System/ActivityLog/Entries?hasUserId=false", admin)
        return [x for x in (d or {}).get("Items", [])
                if x.get("Type") == "ReleaseFin.EpisodeReleased"]
    entries = wait_for("activity log entry", released_activity, 30)
    check("activity log entry names the release",
          "S01E01" in entries[0]["Name"] and "it-notify" in entries[0]["Name"],
          f"entry={entries[0]}")

    status, _ = api("DELETE", f"/ReleaseFin/Schedules/{sched_id}", admin)
    check("notify schedule delete accepted", status == 204, f"status={status}")
    status, settings = api("PUT", "/ReleaseFin/Settings", admin, body={"WebhookUrl": ""})
    check("webhook url cleared", status == 200 and settings["WebhookUrl"] == "",
          f"status={status} settings={settings}")
    listener.shutdown()

    print("ALL SCENARIOS PASSED")


if __name__ == "__main__":
    main()
