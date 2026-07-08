#!/usr/bin/env bash
# ReleaseFin integration test runner: builds the plugin, boots a real Jellyfin in a
# container (docker or podman), and runs integration_test.py against it.
set -euo pipefail
cd "$(dirname "$0")/../.."

TOOL="${RF_CONTAINER_TOOL:-}"
if [ -z "$TOOL" ]; then
  TOOL=$(basename "$(command -v docker || command -v podman)") \
    || { echo "need docker or podman"; exit 1; }
fi
PORT="${RF_PORT:-8097}"
NAME="${RF_CONTAINER_NAME:-releasefin-it-$$}"
WORK=$(mktemp -d)
# Defaults match the plugin's net8.0/Jellyfin 10.10 target. Override both together to
# exercise the net9.0/Jellyfin 10.11 build (see Jellyfin.Plugin.ReleaseFin.csproj for the
# TFM <-> server-version pairing):
#   RF_PUBLISH_TFM=net9.0 RF_JELLYFIN_IMAGE=docker.io/jellyfin/jellyfin:10.11.11 tests/integration/run.sh
TFM="${RF_PUBLISH_TFM:-net8.0}"
IMAGE="${RF_JELLYFIN_IMAGE:-docker.io/jellyfin/jellyfin:10.10.7}"

cleanup() {
  "$TOOL" rm -f "$NAME" >/dev/null 2>&1 || true
  rm -rf "$WORK" 2>/dev/null || sudo rm -rf "$WORK" 2>/dev/null || true
}
trap cleanup EXIT

echo "== build plugin ($TFM)"
dotnet publish src/Jellyfin.Plugin.ReleaseFin -c Release -f "$TFM" -o "$WORK/publish" --nologo
mkdir -p "$WORK/config/plugins/ReleaseFin" "$WORK/media"
cp "$WORK/publish/Jellyfin.Plugin.ReleaseFin.dll" "$WORK/publish/Cronos.dll" \
   "$WORK/config/plugins/ReleaseFin/"

echo "== start jellyfin ($TOOL, port $PORT, image $IMAGE)"
# rf-host.internal lets the container reach a webhook listener on the host
# (host-gateway works on docker 20.10+ and podman 4+).
"$TOOL" run -d --name "$NAME" -p "$PORT:8096" \
  --add-host=rf-host.internal:host-gateway \
  -v "$WORK/config:/config" -v "$WORK/media:/media" \
  "$IMAGE" >/dev/null

RF_PORT="$PORT" RF_CONTAINER_TOOL="$TOOL" RF_CONTAINER_NAME="$NAME" \
RF_CONFIG_DIR="$WORK/config" RF_MEDIA_DIR="$WORK/media" \
python3 tests/integration/integration_test.py
