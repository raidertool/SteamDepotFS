#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
PROJECT="$ROOT/src/SteamDepotFs/SteamDepotFs.csproj"
WORK_ROOT="${RUNNER_TEMP:-/tmp}"
CACHE_DIR="${CACHE_DIR:-$WORK_ROOT/steam-depotfs-cache}"
CACHE_MAX_BYTES="${CACHE_MAX_BYTES:-8G}"
CACHE_LOW_WATERMARK="${CACHE_LOW_WATERMARK:-7G}"
CACHE_MIN_FREE_BYTES="${CACHE_MIN_FREE_BYTES:-1G}"

COMMON_ARGS=(
  --app 480
  --depot 481
  --cache-dir "$CACHE_DIR"
  --cache-max-bytes "$CACHE_MAX_BYTES"
  --cache-low-watermark "$CACHE_LOW_WATERMARK"
  --cache-min-free-bytes "$CACHE_MIN_FREE_BYTES"
  --timeout 180
)

dotnet build "$PROJECT" -c Release

dotnet run --no-build --project "$PROJECT" -c Release -- smoke "${COMMON_ARGS[@]}"

OUT_FILE="$WORK_ROOT/steam-depotfs-installscript.vdf"
dotnet run --no-build --project "$PROJECT" -c Release -- read \
  --path installscript.vdf \
  --out "$OUT_FILE" \
  "${COMMON_ARGS[@]}"

if [[ "$(uname -s)" != "Linux" ]]; then
  echo "Skipping FUSE mount test: host is not Linux."
  exit 0
fi

if [[ ! -e /dev/fuse ]]; then
  if [[ "${REQUIRE_FUSE:-0}" == "1" ]]; then
    echo "FUSE mount test required, but /dev/fuse is missing." >&2
    exit 1
  fi

  echo "Skipping FUSE mount test: /dev/fuse is missing."
  exit 0
fi

if command -v fusermount3 >/dev/null 2>&1; then
  UNMOUNT=(fusermount3 -u)
elif command -v fusermount >/dev/null 2>&1; then
  UNMOUNT=(fusermount -u)
else
  if [[ "${REQUIRE_FUSE:-0}" == "1" ]]; then
    echo "FUSE mount test required, but fusermount/fusermount3 is missing." >&2
    exit 1
  fi

  echo "Skipping FUSE mount test: fusermount/fusermount3 is missing."
  exit 0
fi

MOUNT_DIR="$WORK_ROOT/steam-depotfs-mount"
LOG_FILE="$WORK_ROOT/steam-depotfs-mount.log"
mkdir -p "$MOUNT_DIR"

dotnet run --no-build --project "$PROJECT" -c Release -- mount \
  --mount-point "$MOUNT_DIR" \
  "${COMMON_ARGS[@]}" >"$LOG_FILE" 2>&1 &
MOUNT_PID=$!

cleanup() {
  "${UNMOUNT[@]}" "$MOUNT_DIR" >/dev/null 2>&1 || true
  wait "$MOUNT_PID" >/dev/null 2>&1 || true
}
trap cleanup EXIT

for _ in $(seq 1 30); do
  if [[ -f "$MOUNT_DIR/installscript.vdf" ]]; then
    break
  fi

  if ! kill -0 "$MOUNT_PID" >/dev/null 2>&1; then
    echo "Mount process exited early. Log:" >&2
    sed -n '1,120p' "$LOG_FILE" >&2 || true
    exit 1
  fi

  sleep 1
done

test -f "$MOUNT_DIR/installscript.vdf"

if command -v sha256sum >/dev/null 2>&1; then
  sha256sum "$MOUNT_DIR/installscript.vdf"
else
  shasum -a 256 "$MOUNT_DIR/installscript.vdf"
fi

echo "FUSE mount test passed."
