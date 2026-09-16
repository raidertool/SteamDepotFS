# SteamDepotFs

`SteamDepotFs` mounts Steam depots read-only, downloading chunks on demand into a bounded disk cache.

## Installation

Download and extract a [release](https://github.com/raidertool/SteamDepotFS/releases)
for your platform. Release binaries are self-contained; no .NET installation is needed.

The v0.2.0 binaries predate the username/password login fix. For that fix, build
source containing commit `91a8cc1` ([PR #3](https://github.com/raidertool/SteamDepotFS/pull/3)).

Only `mount` needs a filesystem driver; `smoke`, `list`, `inspect`, and `read` do not:

- **Windows:** [WinFsp 2.1+](https://winfsp.dev/rel/).
- **Linux (Ubuntu/Debian):** `sudo apt-get install -y fuse3 libfuse2`; load FUSE with `sudo modprobe fuse` if needed.
- **macOS:** [macFUSE](https://macfuse.github.io/). With macOS 15.4+ and macFUSE 5+, mount under `/Volumes/<name>` for FSKit. Other paths use the kernel backend and may require system-extension approval.

To build and test source, install the .NET 8 SDK, then run from the repository root:

```bash
dotnet build src/SteamDepotFs/SteamDepotFs.csproj -c Release
dotnet test tests/SteamDepotFs.Tests/SteamDepotFs.Tests.csproj -c Release
```

## Usage

Run these examples from the extracted release directory. From the repository
root, replace `./SteamDepotFs` with `dotnet run --project src/SteamDepotFs -c Release --`.

List files in a depot:

```bash
./SteamDepotFs list \
  --app <app-id> \
  --depot <depot-id>
```

Read a file from a depot:

```bash
./SteamDepotFs read \
  --app <app-id> \
  --depot <depot-id> \
  --path <depot-path> \
  --out /tmp/output-file
```

Mount a depot read-only:

```bash
mkdir -p /tmp/steam-depotfs

./SteamDepotFs mount \
  --app <app-id> \
  --depot <depot-id> \
  --mount-point /tmp/steam-depotfs
```

On Windows, use an unused drive letter or an empty directory mount point:

```powershell
.\SteamDepotFs.exe mount `
  --app <app-id> `
  --depot <depot-id> `
  --mount-point X:
```

Unmount with `fusermount3 -u /tmp/steam-depotfs` on Linux or
`diskutil unmount <mount-point>` on macOS. On Windows, stop the mount process with `Ctrl+C`.

### Authentication

Anonymous login is used unless credentials are provided. Credentials can be passed as arguments or environment variables:

| Argument | Environment variable |
| --- | --- |
| `--username` | `STEAM_USERNAME` |
| `--password` | `STEAM_PASSWORD` |
| `--auth-code` | `STEAM_AUTH_CODE` |
| `--two-factor-code` | `STEAM_TWO_FACTOR_CODE` |
| `--access-token` | `STEAM_ACCESS_TOKEN` |
| `--login-id` | `STEAM_LOGIN_ID` |
| `--remember-password` | `STEAM_REMEMBER_PASSWORD=1` |

Username/password login obtains a fresh Steam client refresh token in memory, then
uses that token for depot access. `STEAM_ACCESS_TOKEN` (or `--access-token`)
accepts an existing client refresh token and takes precedence over any password.
Tokens are not printed or saved, including with `--remember-password`.
Each command authenticates independently; supply the username with either method.

Login is noninteractive. If password login requests Steam Guard, supply an email
or mobile authenticator code. Device-approval prompts are unsupported. Prefer
environment variables so credentials do not appear in command arguments.

Use `--branch NAME` to select a branch and `--branch-password-hash HASH` when
required. `--manifest ID` pins a specific snapshot instead of resolving the branch.

### Cache

Cache limits use explicit byte caps. Suffixes `K`, `M`, `G`, and `T` are accepted:

```bash
./SteamDepotFs mount \
  --app <app-id> \
  --depot <depot-id> \
  --mount-point /tmp/steam-depotfs \
  --cache-dir /tmp/steam-depotfs-cache \
  --cache-max-bytes 8G \
  --cache-low-watermark 6G \
  --cache-min-free-bytes 2G \
  --max-chunk-concurrency 4 \
  --read-ahead-chunks 1
```

The cache grows to `--cache-max-bytes`, then evicts toward `--cache-low-watermark`.
`--cache-min-free-bytes` guards minimum free disk space. Oversized chunks are served
without caching. `--max-chunk-concurrency` limits concurrent fetches across reads
and prefetching; `--read-ahead-chunks 0` disables prefetching.

## Testing

The [public test](scripts/ci/test-steam-depotfs-public.sh) uses Spacewar
(app `480`, depot `481`, branch `public`) anonymously:

```bash
CACHE_MAX_BYTES=8G CACHE_LOW_WATERMARK=6G CACHE_MIN_FREE_BYTES=2G REQUIRE_FUSE=1 \
  scripts/ci/test-steam-depotfs-public.sh
```

Compare prefetching and concurrency with the [benchmark script](scripts/bench/read-matrix.sh):

```bash
scripts/bench/read-matrix.sh <app-id> <depot-id> <depot-path>
```

See those scripts for configuration variables. [Depot tests](.github/workflows/public-test.yml)
checks public reads and Linux mounts on pushes and PRs. Manual runs add Windows
mounts and optional self-hosted macOS mounts; hosted macOS checks reads only.
Authenticated tests run on pushes/manual runs when configured. Supply
`STEAM_USERNAME` with `STEAM_PASSWORD` or `STEAM_ACCESS_TOKEN`, or use the workflow's
1Password references. Target-depot variables are documented in the workflow.

## Releases

Automatic releases follow successful `Depot tests` runs on `main`. Manual release
dispatch also uses `main`, without rerunning those tests. The release workflow
uses [conventional commits](scripts/ci/compute-release-version.sh) since the
previous reachable `v*` tag:

- breaking changes create a major release
- `feat:` creates a minor release
- `fix:`, `perf:`, `refactor:`, and `revert:` create a patch release
- docs-only or CI-only changes do not create a release

The [release workflow](.github/workflows/release.yml) publishes Windows, Linux,
and macOS archives, generates release notes, and regenerates `CHANGELOG.md` from Git history.

Release builds receive their version from CI; each archive includes a `VERSION`
file. Source builds default to version `1.0.0`, so identify them by Git commit.
There is no `--version` command. No version bump in the project file is needed.

## License

[Apache-2.0](LICENSE); see [NOTICE](NOTICE). Dependencies retain their own licenses,
including SteamKit2's LGPL-2.1-only; see [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
