#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
PROJECT="$ROOT/src/SteamDepotFs/SteamDepotFs.csproj"
WORK_ROOT="${RUNNER_TEMP:-/tmp}"
CACHE_DIR="${CACHE_DIR:-$WORK_ROOT/steam-depotfs-auth-cache}"
CACHE_MAX_BYTES="${CACHE_MAX_BYTES:-8G}"
CACHE_LOW_WATERMARK="${CACHE_LOW_WATERMARK:-6G}"
CACHE_MIN_FREE_BYTES="${CACHE_MIN_FREE_BYTES:-2G}"

APP_ID="${STEAM_DEPOTFS_AUTH_APP_ID:-480}"
DEPOT_ID="${STEAM_DEPOTFS_AUTH_DEPOT_ID:-481}"
BRANCH="${STEAM_DEPOTFS_AUTH_BRANCH:-public}"
MANIFEST_ID="${STEAM_DEPOTFS_AUTH_MANIFEST_ID:-}"
READ_PATH="${STEAM_DEPOTFS_AUTH_READ_PATH:-installscript.vdf}"
BRANCH_PASSWORD_HASH="${STEAM_DEPOTFS_AUTH_BRANCH_PASSWORD_HASH:-}"
TIMEOUT="${STEAM_DEPOTFS_AUTH_TIMEOUT:-180}"

if [[ -z "${STEAM_USERNAME:-}" && -z "${STEAM_ACCESS_TOKEN:-}" ]]; then
  echo "Authenticated test requires STEAM_USERNAME or STEAM_ACCESS_TOKEN." >&2
  exit 1
fi

if [[ -n "${STEAM_USERNAME:-}" && -z "${STEAM_PASSWORD:-}" && -z "${STEAM_ACCESS_TOKEN:-}" ]]; then
  echo "Authenticated test requires STEAM_PASSWORD when STEAM_USERNAME is used without STEAM_ACCESS_TOKEN." >&2
  exit 1
fi

COMMON_ARGS=(
  --app "$APP_ID"
  --depot "$DEPOT_ID"
  --branch "$BRANCH"
  --cache-dir "$CACHE_DIR"
  --cache-max-bytes "$CACHE_MAX_BYTES"
  --cache-low-watermark "$CACHE_LOW_WATERMARK"
  --cache-min-free-bytes "$CACHE_MIN_FREE_BYTES"
  --timeout "$TIMEOUT"
)

if [[ -n "$MANIFEST_ID" ]]; then
  COMMON_ARGS+=(--manifest "$MANIFEST_ID")
fi

if [[ -n "$BRANCH_PASSWORD_HASH" ]]; then
  COMMON_ARGS+=(--branch-password-hash "$BRANCH_PASSWORD_HASH")
fi

dotnet build "$PROJECT" -c Release

dotnet run --no-build --project "$PROJECT" -c Release -- smoke "${COMMON_ARGS[@]}"

if [[ -n "$READ_PATH" ]]; then
  OUT_FILE="$WORK_ROOT/steam-depotfs-auth-read"
  dotnet run --no-build --project "$PROJECT" -c Release -- read \
    --path "$READ_PATH" \
    --out "$OUT_FILE" \
    "${COMMON_ARGS[@]}"
fi
