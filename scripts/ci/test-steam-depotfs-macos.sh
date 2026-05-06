#!/usr/bin/env bash
set -euo pipefail

if [[ "$(uname -s)" != "Darwin" ]]; then
  echo "macOS mount test only runs on macOS." >&2
  exit 1
fi

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
PROJECT="$ROOT/src/SteamDepotFs/SteamDepotFs.csproj"
WORK_ROOT="${RUNNER_TEMP:-/tmp}"
CACHE_DIR="${CACHE_DIR:-$WORK_ROOT/steam-depotfs-macos-cache}"
PUBLISH_DIR="${PUBLISH_DIR:-$WORK_ROOT/steam-depotfs-macos-publish}"
MOUNT_DIR="${MOUNT_DIR:-$WORK_ROOT/steam-depotfs-mount}"
TIMEOUT_SECONDS="${TIMEOUT_SECONDS:-90}"

case "$(uname -m)" in
  arm64) RUNTIME_IDENTIFIER="${RUNTIME_IDENTIFIER:-osx-arm64}" ;;
  x86_64) RUNTIME_IDENTIFIER="${RUNTIME_IDENTIFIER:-osx-x64}" ;;
  *)
    echo "Unsupported macOS architecture: $(uname -m)" >&2
    exit 1
    ;;
esac

if [[ ! -d /Library/Filesystems/macfuse.fs ]]; then
  echo "macFUSE is not installed." >&2
  exit 1
fi

if [[ ! -e /usr/local/lib/libfuse.dylib && ! -e /opt/homebrew/lib/libfuse.dylib ]]; then
  echo "macFUSE libfuse.dylib is not available." >&2
  exit 1
fi

rm -rf "$PUBLISH_DIR"
mkdir -p "$PUBLISH_DIR" "$CACHE_DIR"

dotnet publish "$PROJECT" \
  -c Release \
  -r "$RUNTIME_IDENTIFIER" \
  --self-contained true \
  -p:UseAppHost=true \
  -o "$PUBLISH_DIR"

EXE="$PUBLISH_DIR/SteamDepotFs"
if [[ ! -x "$EXE" ]]; then
  echo "SteamDepotFS executable was not published to $EXE." >&2
  exit 1
fi

COMMON_ARGS=(
  --app 480
  --depot 481
  --cache-dir "$CACHE_DIR"
  --cache-max-bytes 1G
  --cache-low-watermark 512M
  --cache-min-free-bytes 256M
  --timeout 180
)

"$EXE" smoke "${COMMON_ARGS[@]}"

MOUNT_STDOUT="$WORK_ROOT/steam-depotfs-macos-mount.out.log"
MOUNT_STDERR="$WORK_ROOT/steam-depotfs-macos-mount.err.log"
rm -f "$MOUNT_STDOUT" "$MOUNT_STDERR"

if [[ "$MOUNT_DIR" == /Volumes/* ]]; then
  sudo mkdir -p "$MOUNT_DIR"
  sudo chown "$(id -u):$(id -g)" "$MOUNT_DIR"
else
  mkdir -p "$MOUNT_DIR"
fi

"$EXE" mount \
  --mount-point "$MOUNT_DIR" \
  "${COMMON_ARGS[@]}" >"$MOUNT_STDOUT" 2>"$MOUNT_STDERR" &
MOUNT_PID=$!

cleanup() {
  diskutil unmount force "$MOUNT_DIR" >/dev/null 2>&1 || umount "$MOUNT_DIR" >/dev/null 2>&1 || true

  if kill -0 "$MOUNT_PID" >/dev/null 2>&1; then
    kill "$MOUNT_PID" >/dev/null 2>&1 || true
    sleep 1
  fi

  if kill -0 "$MOUNT_PID" >/dev/null 2>&1; then
    kill -9 "$MOUNT_PID" >/dev/null 2>&1 || true
  fi

  wait "$MOUNT_PID" >/dev/null 2>&1 || true
  if [[ "$MOUNT_DIR" == /Volumes/SteamDepotFS-* ]]; then
    sudo rmdir "$MOUNT_DIR" >/dev/null 2>&1 || true
  fi
}
trap cleanup EXIT

TARGET_FILE="$MOUNT_DIR/installscript.vdf"
for _ in $(seq 1 "$TIMEOUT_SECONDS"); do
  if [[ -f "$TARGET_FILE" ]]; then
    ls -l "$TARGET_FILE"
    shasum -a 256 "$TARGET_FILE"
    echo "macFUSE mount test passed."
    exit 0
  fi

  if ! kill -0 "$MOUNT_PID" >/dev/null 2>&1; then
    echo "SteamDepotFS mount process exited early." >&2
    break
  fi

  sleep 1
done

echo "--- mount stdout ---" >&2
sed -n '1,160p' "$MOUNT_STDOUT" >&2 || true
echo "--- mount stderr ---" >&2
sed -n '1,160p' "$MOUNT_STDERR" >&2 || true

exit 1
