#!/usr/bin/env bash
set -euo pipefail

if [[ "$(uname -s)" != "Darwin" ]]; then
  echo "macFUSE install script only runs on macOS." >&2
  exit 1
fi

MACFUSE_VERSION="${MACFUSE_VERSION:-5.2.0}"
MACFUSE_SHA256="${MACFUSE_SHA256:-09a4b4c23c1930af45335fc119696797da41562dec1630602d2db637f4804f27}"
WORK_ROOT="${RUNNER_TEMP:-/tmp}"
DMG_FILE="$WORK_ROOT/macfuse-$MACFUSE_VERSION.dmg"
MOUNT_ROOT="$WORK_ROOT/macfuse-dmg"
VOLUME_PATH=""

cleanup() {
  if [[ -n "$VOLUME_PATH" ]]; then
    hdiutil detach "$VOLUME_PATH" -force >/dev/null 2>&1 || true
  fi
}
trap cleanup EXIT

curl -fsSL \
  -o "$DMG_FILE" \
  "https://github.com/macfuse/macfuse/releases/download/macfuse-$MACFUSE_VERSION/macfuse-$MACFUSE_VERSION.dmg"

echo "$MACFUSE_SHA256  $DMG_FILE" | shasum -a 256 -c -

rm -rf "$MOUNT_ROOT"
mkdir -p "$MOUNT_ROOT"
hdiutil attach -nobrowse -readonly -mountroot "$MOUNT_ROOT" "$DMG_FILE" >/dev/null

for candidate in "$MOUNT_ROOT"/*; do
  if [[ -d "$candidate" ]]; then
    VOLUME_PATH="$candidate"
    break
  fi
done

if [[ -z "$VOLUME_PATH" ]]; then
  echo "Unable to find mounted macFUSE volume under $MOUNT_ROOT." >&2
  exit 1
fi

PKG_FILE="$VOLUME_PATH/Install macFUSE.pkg"
if [[ ! -f "$PKG_FILE" ]]; then
  PKG_FILE="$(find "$VOLUME_PATH" -name '*.pkg' -print -quit)"
fi

if [[ -z "$PKG_FILE" ]]; then
  echo "Unable to find macFUSE pkg in $VOLUME_PATH." >&2
  exit 1
fi

sudo installer -pkg "$PKG_FILE" -target /

MACFUSE_CLI="/Library/Filesystems/macfuse.fs/Contents/Resources/macfuse.app/Contents/MacOS/macfuse"
if [[ -x "$MACFUSE_CLI" ]]; then
  sudo "$MACFUSE_CLI" install --components file-system-extensions --force
fi

if [[ ! -e /usr/local/lib/libfuse.dylib && ! -e /opt/homebrew/lib/libfuse.dylib ]]; then
  echo "macFUSE installed, but libfuse.dylib was not found in /usr/local/lib or /opt/homebrew/lib." >&2
  exit 1
fi

echo "macFUSE $MACFUSE_VERSION installed."
