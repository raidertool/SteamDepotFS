#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
PROJECT="$ROOT/src/SteamDepotFs/SteamDepotFs.csproj"
WORK_ROOT="${RUNNER_TEMP:-/tmp}"
CACHE_DIR="${CACHE_DIR:-$WORK_ROOT/steam-depotfs-auth-benchmark-cache}"
CACHE_MAX_BYTES="${CACHE_MAX_BYTES:-8G}"
CACHE_LOW_WATERMARK="${CACHE_LOW_WATERMARK:-6G}"
CACHE_MIN_FREE_BYTES="${CACHE_MIN_FREE_BYTES:-2G}"

APP_ID="${STEAM_DEPOTFS_AUTH_APP_ID:-480}"
DEPOT_ID="${STEAM_DEPOTFS_AUTH_DEPOT_ID:-481}"
BRANCH="${STEAM_DEPOTFS_AUTH_BRANCH:-public}"
MANIFEST_ID="${STEAM_DEPOTFS_AUTH_MANIFEST_ID:-}"
READ_PATH="${STEAM_DEPOTFS_BENCH_READ_PATH:-${STEAM_DEPOTFS_AUTH_READ_PATH:-}}"
BRANCH_PASSWORD_HASH="${STEAM_DEPOTFS_AUTH_BRANCH_PASSWORD_HASH:-}"
TIMEOUT="${STEAM_DEPOTFS_AUTH_TIMEOUT:-600}"
LIST_LIMIT="${STEAM_DEPOTFS_BENCH_LIST_LIMIT:-200000}"

if [[ -z "${STEAM_USERNAME:-}" && -z "${STEAM_ACCESS_TOKEN:-}" ]]; then
  echo "Authenticated benchmark requires STEAM_USERNAME or STEAM_ACCESS_TOKEN." >&2
  exit 1
fi

if [[ -n "${STEAM_USERNAME:-}" && -z "${STEAM_PASSWORD:-}" && -z "${STEAM_ACCESS_TOKEN:-}" ]]; then
  echo "Authenticated benchmark requires STEAM_PASSWORD when STEAM_USERNAME is used without STEAM_ACCESS_TOKEN." >&2
  exit 1
fi

COMMON_ARGS=(
  --branch "$BRANCH"
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

cd "$ROOT"
dotnet build "$PROJECT" -c Release

if [[ -z "$READ_PATH" ]]; then
  LIST_FILE="$WORK_ROOT/steam-depotfs-auth-file-list.txt"
  dotnet run --no-build --project "$PROJECT" -c Release -- list \
    --app "$APP_ID" \
    --depot "$DEPOT_ID" \
    --cache-dir "$CACHE_DIR/list-cache" \
    --limit "$LIST_LIMIT" \
    "${COMMON_ARGS[@]}" >"$LIST_FILE"

  READ_PATH="$(
    awk '
      BEGIN { IGNORECASE = 1 }
      /^[[:space:]]*[0-9]+[[:space:]]+/ {
        size = $1
        path = $0
        sub(/^[[:space:]]*[0-9]+[[:space:]]+/, "", path)
        if (path ~ /\.(utoc|ucas|pak)$/ && size > best_size) {
          best_size = size
          best_path = path
        }
      }
      END { print best_path }
    ' "$LIST_FILE"
  )"
fi

if [[ -z "$READ_PATH" ]]; then
  echo "Could not select a benchmark path. Set STEAM_DEPOTFS_BENCH_READ_PATH." >&2
  exit 1
fi

export READ_AHEAD_VALUES="${READ_AHEAD_VALUES:-0 1 2}"
export CONCURRENCY_VALUES="${CONCURRENCY_VALUES:-4 8 16}"
export ITERATIONS="${ITERATIONS:-1}"
export LENGTH="${LENGTH:-67108864}"
export CACHE_ROOT="${CACHE_ROOT:-$WORK_ROOT/steam-depotfs-auth-benchmark-matrix}"

echo "Benchmarking app=$APP_ID depot=$DEPOT_ID branch=$BRANCH path=$READ_PATH length=$LENGTH"
echo "Matrix read_ahead=[$READ_AHEAD_VALUES] concurrency=[$CONCURRENCY_VALUES] iterations=$ITERATIONS"

"$ROOT/scripts/bench/read-matrix.sh" "$APP_ID" "$DEPOT_ID" "$READ_PATH" "${COMMON_ARGS[@]}"
