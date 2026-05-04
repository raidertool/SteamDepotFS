#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$ROOT"

usage() {
  cat >&2 <<'USAGE'
Usage:
  scripts/bench/read-matrix.sh <app-id> <depot-id> <depot-path> [extra steam-depotfs args...]

Environment:
  READ_AHEAD_VALUES       Space-separated values. Default: "0 1 2"
  CONCURRENCY_VALUES      Space-separated values. Default: "4 8 16"
  ITERATIONS              Runs per matrix cell. Default: 3
  OFFSET                  Optional read offset passed to steam-depotfs.
  LENGTH                  Optional read length passed to steam-depotfs.
  CACHE_ROOT              Optional benchmark cache root. Default: mktemp directory.
  WARM_CACHE              Set to 1 to reuse each matrix cell cache across iterations.
  KEEP_CACHE              Set to 1 to keep generated cache/output files.

Examples:
  scripts/bench/read-matrix.sh 480 481 installscript.vdf
  LENGTH=104857600 READ_AHEAD_VALUES="0 1 2" CONCURRENCY_VALUES="4 8" \
    scripts/bench/read-matrix.sh <app> <depot> path/to/file.pak --branch public
USAGE
}

if [[ $# -lt 3 ]]; then
  usage
  exit 2
fi

app_id=$1
depot_id=$2
depot_path=$3
shift 3
extra_args=("$@")

read_ahead_values=${READ_AHEAD_VALUES:-"0 1 2"}
concurrency_values=${CONCURRENCY_VALUES:-"4 8 16"}
iterations=${ITERATIONS:-3}
cache_root=${CACHE_ROOT:-$(mktemp -d "${TMPDIR:-/tmp}/steam-depotfs-bench.XXXXXX")}

cleanup() {
  if [[ "${KEEP_CACHE:-0}" != "1" && -d "$cache_root" ]]; then
    rm -rf "$cache_root"
  fi
}
trap cleanup EXIT

dotnet build src/SteamDepotFs/SteamDepotFs.csproj -c Release >/dev/null

printf 'read_ahead_chunks,max_chunk_concurrency,iteration,elapsed_seconds,cache_stats\n'

for read_ahead in $read_ahead_values; do
  for concurrency in $concurrency_values; do
    cell_cache="$cache_root/ra-${read_ahead}-c-${concurrency}"
    for iteration in $(seq 1 "$iterations"); do
      if [[ "${WARM_CACHE:-0}" == "1" ]]; then
        run_cache="$cell_cache"
      else
        run_cache="$cell_cache/i-${iteration}"
      fi

      mkdir -p "$run_cache"
      output_file="$run_cache/read.bin"
      stderr_file="$run_cache/stderr.txt"

      cmd=(
        dotnet run --project src/SteamDepotFs/SteamDepotFs.csproj -c Release --
        read
        --app "$app_id"
        --depot "$depot_id"
        --path "$depot_path"
        --out "$output_file"
        --cache-dir "$run_cache/cache"
        --max-chunk-concurrency "$concurrency"
        --read-ahead-chunks "$read_ahead"
      )

      if [[ -n "${OFFSET:-}" ]]; then
        cmd+=(--offset "$OFFSET")
      fi

      if [[ -n "${LENGTH:-}" ]]; then
        cmd+=(--length "$LENGTH")
      fi

      cmd+=("${extra_args[@]}")

      TIMEFORMAT=%R
      elapsed=$({ time "${cmd[@]}" >/dev/null 2>"$stderr_file"; } 2>&1)
      stats=$(tail -n 1 "$stderr_file" | tr '\n' ' ')
      printf '%s,%s,%s,%s,"%s"\n' "$read_ahead" "$concurrency" "$iteration" "$elapsed" "$stats"
    done
  done
done
