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
INSPECT_CANDIDATES="${STEAM_DEPOTFS_BENCH_INSPECT_CANDIDATES:-5}"

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
  CANDIDATES_FILE="$WORK_ROOT/steam-depotfs-auth-benchmark-candidates.tsv"
  dotnet run --no-build --project "$PROJECT" -c Release -- list \
    --app "$APP_ID" \
    --depot "$DEPOT_ID" \
    --cache-dir "$CACHE_DIR/list-cache" \
    --limit "$LIST_LIMIT" \
    "${COMMON_ARGS[@]}" >"$LIST_FILE"

  awk '
    BEGIN { IGNORECASE = 1 }
    /^[[:space:]]*[0-9]+[[:space:]]+/ {
      size = $1
      path = $0
      sub(/^[[:space:]]*[0-9]+[[:space:]]+/, "", path)
      if (path ~ /\.(utoc|ucas|pak)$/) {
        print size "\t" path
      }
    }
  ' "$LIST_FILE" | sort -nr >"$CANDIDATES_FILE"

  inspected=0
  while IFS=$'\t' read -r candidate_size candidate_path; do
    PROBE_FILE="$WORK_ROOT/steam-depotfs-auth-benchmark-probe.bin"
    rm -f "$PROBE_FILE"
    echo "Probing candidate size=$candidate_size path=$candidate_path"
    if (( inspected < INSPECT_CANDIDATES )); then
      dotnet run --no-build --project "$PROJECT" -c Release -- inspect \
        --app "$APP_ID" \
        --depot "$DEPOT_ID" \
        --path "$candidate_path" \
        --chunks 8 \
        --cache-dir "$CACHE_DIR/inspect-cache" \
        "${COMMON_ARGS[@]}"
      inspected=$((inspected + 1))
    fi

    dotnet run --no-build --project "$PROJECT" -c Release -- read \
      --app "$APP_ID" \
      --depot "$DEPOT_ID" \
      --path "$candidate_path" \
      --length 1 \
      --out "$PROBE_FILE" \
      --cache-dir "$CACHE_DIR/probe-cache" \
      --read-ahead-chunks 0 \
      "${COMMON_ARGS[@]}" >/dev/null

    if [[ -s "$PROBE_FILE" ]]; then
      READ_PATH="$candidate_path"
      echo "Selected benchmark candidate size=$candidate_size path=$READ_PATH"
      break
    fi
  done <"$CANDIDATES_FILE"

  if [[ -z "$READ_PATH" ]]; then
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
dotnet run --no-build --project "$PROJECT" -c Release -- inspect \
  --app "$APP_ID" \
  --depot "$DEPOT_ID" \
  --path "$READ_PATH" \
  --chunks 12 \
  --cache-dir "$CACHE_DIR/inspect-selected-cache" \
  "${COMMON_ARGS[@]}"

RESULTS_FILE="$WORK_ROOT/steam-depotfs-auth-benchmark-results.csv"
"$ROOT/scripts/bench/read-matrix.sh" "$APP_ID" "$DEPOT_ID" "$READ_PATH" "${COMMON_ARGS[@]}" | tee "$RESULTS_FILE"

if ! awk -F, 'NR > 1 && $5 ~ /misses=[1-9]/ && $5 ~ /downloaded=[1-9]/ { ok = 1 } END { exit ok ? 0 : 1 }' "$RESULTS_FILE"; then
  echo "Benchmark did not exercise any cache-miss chunk downloads." >&2
  exit 1
fi
