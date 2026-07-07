#!/usr/bin/env python3
"""Upsert a release into the Jellyfin plugin repository manifest (manifest.json).

Called by the release workflow with the built zip; computes the MD5 checksum
Jellyfin expects and inserts the version entry (newest first).
"""
import argparse
import datetime
import hashlib
import json
import pathlib

PLUGIN = {
    "guid": "e7d1f0a4-8c3b-4a5e-9f2d-6b0c4d8e1a23",
    "name": "ReleaseFin",
    "description": (
        "Assign cron-style release schedules to series for selected accounts (e.g. Kids). "
        "Unreleased episodes are hidden via per-schedule tags and the users' blocked-tags "
        "parental control; the scheduler reveals the next episode as each release time passes."
    ),
    "overview": "Drip-release episodes to selected accounts on a schedule.",
    "owner": "Detair",
    "category": "General",
    "imageUrl": "",
}
TARGET_ABI = "10.10.0.0"


def main() -> None:
    ap = argparse.ArgumentParser()
    ap.add_argument("--version", required=True, help="four-part version, e.g. 1.0.0.0")
    ap.add_argument("--zip", required=True, help="path to the built plugin zip")
    ap.add_argument("--url", required=True, help="public download URL of the zip")
    ap.add_argument("--changelog", default="")
    ap.add_argument("--manifest", default="manifest.json")
    args = ap.parse_args()

    checksum = hashlib.md5(pathlib.Path(args.zip).read_bytes()).hexdigest()
    manifest_path = pathlib.Path(args.manifest)
    manifest = json.loads(manifest_path.read_text()) if manifest_path.exists() else []

    entry = next((e for e in manifest if e.get("guid") == PLUGIN["guid"]), None)
    if entry is None:
        entry = {**PLUGIN, "versions": []}
        manifest.append(entry)
    else:
        versions = entry.get("versions", [])
        entry.clear()
        entry.update({**PLUGIN, "versions": versions})

    entry["versions"] = [v for v in entry["versions"] if v["version"] != args.version]
    entry["versions"].insert(0, {
        "version": args.version,
        "changelog": args.changelog,
        "targetAbi": TARGET_ABI,
        "sourceUrl": args.url,
        "checksum": checksum,
        "timestamp": datetime.datetime.now(datetime.timezone.utc)
            .strftime("%Y-%m-%dT%H:%M:%SZ"),
    })

    manifest_path.write_text(json.dumps(manifest, indent=2) + "\n")
    print(f"manifest updated: {args.version} checksum={checksum}")


if __name__ == "__main__":
    main()
