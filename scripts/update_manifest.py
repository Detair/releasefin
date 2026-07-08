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
DEFAULT_TARGET_ABI = "10.10.0.0"


def main() -> None:
    ap = argparse.ArgumentParser()
    ap.add_argument("--version", required=True, help="four-part version, e.g. 1.0.0.0")
    ap.add_argument("--zip", required=True, help="path to the built plugin zip")
    ap.add_argument("--url", required=True, help="public download URL of the zip")
    ap.add_argument("--changelog", default="")
    ap.add_argument("--manifest", default="manifest.json")
    # Each server-compatible build (net8.0/10.10.x, net9.0/10.11.x, ...) is a distinct
    # manifest entry. Jellyfin's own manifest schema keys a version entry on "version"
    # alone, so two builds released together must carry different version strings even
    # when they share the same feature release — see the ecosystem convention check
    # below. --target-abi lets the release workflow record which server line a build
    # targets without editing this script per Jellyfin release.
    ap.add_argument("--target-abi", default=DEFAULT_TARGET_ABI)
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

    # Key on (version, targetAbi): a release with builds for two server lines (e.g.
    # 1.1.0.0/10.10.0.0 and 1.1.0.1/10.11.0.0) must not have one upsert evict the other's
    # entry just because this script ran twice in the same release job.
    entry["versions"] = [
        v for v in entry["versions"]
        if (v["version"], v["targetAbi"]) != (args.version, args.target_abi)
    ]
    entry["versions"].insert(0, {
        "version": args.version,
        "changelog": args.changelog,
        "targetAbi": args.target_abi,
        "sourceUrl": args.url,
        "checksum": checksum,
        "timestamp": datetime.datetime.now(datetime.timezone.utc)
            .strftime("%Y-%m-%dT%H:%M:%SZ"),
    })

    manifest_path.write_text(json.dumps(manifest, indent=2) + "\n")
    print(f"manifest updated: {args.version} targetAbi={args.target_abi} checksum={checksum}")


if __name__ == "__main__":
    main()
