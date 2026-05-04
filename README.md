# SteamDepotFs

`SteamDepotFs` is a SteamKit-based, read-only Steam depot filesystem. It resolves depot manifests, downloads only the chunks needed for reads, and keeps decompressed chunks in a bounded on-disk cache.

It was created for GitHub workflows that operate on Steam game files but have limited disk space. It can also be used anywhere a read-only, on-demand view of a depot is useful.

## Installation

For CI or other projects, download the latest Linux x64 release archive. The release binary is self-contained and does not require the .NET SDK:

```bash
curl -L -o SteamDepotFS-linux-x64.tar.gz \
  https://github.com/raidertool/SteamDepotFS/releases/latest/download/SteamDepotFS-linux-x64.tar.gz
tar -xzf SteamDepotFS-linux-x64.tar.gz
```

For mounted filesystem access on Linux, install FUSE:

```bash
sudo apt-get update
sudo apt-get install -y fuse3 libfuse2
sudo modprobe fuse || true
```

The `smoke`, `list`, and `read` commands do not require FUSE. Only `mount` requires FUSE.

To build from source instead, install the .NET 8 SDK and run:

```bash
dotnet build src/SteamDepotFs/SteamDepotFs.csproj -c Release
```

## Usage

List files in a depot:

```bash
dotnet run --project src/SteamDepotFs/SteamDepotFs.csproj -c Release -- list \
  --app <app-id> \
  --depot <depot-id>
```

Read a file from a depot:

```bash
dotnet run --project src/SteamDepotFs/SteamDepotFs.csproj -c Release -- read \
  --app <app-id> \
  --depot <depot-id> \
  --path <depot-path> \
  --out /tmp/output-file
```

Mount a depot read-only:

```bash
mkdir -p /tmp/steam-depotfs

dotnet run --project src/SteamDepotFs/SteamDepotFs.csproj -c Release -- mount \
  --app <app-id> \
  --depot <depot-id> \
  --mount-point /tmp/steam-depotfs
```

Unmount:

```bash
fusermount3 -u /tmp/steam-depotfs
```

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

Private branches may also require:

```bash
--manifest <manifest-gid> --branch <branch-name> --branch-password-hash <hash>
```

Cache limits use explicit byte caps. Suffixes `K`, `M`, `G`, and `T` are accepted:

```bash
dotnet run --project src/SteamDepotFs/SteamDepotFs.csproj -c Release -- mount \
  --app <app-id> \
  --depot <depot-id> \
  --mount-point /tmp/steam-depotfs \
  --cache-dir /tmp/steam-depotfs-cache \
  --cache-max-bytes 8G \
  --cache-low-watermark 6G \
  --cache-min-free-bytes 2G
```

`--cache-max-bytes` is the hard cap. `--cache-low-watermark` is the cache size to return to after eviction, not the amount of free disk space to keep. For example, with `--cache-max-bytes 8G` and `--cache-low-watermark 6G`, the cache can grow to 8 GiB; once a new chunk would exceed that cap, old chunks are evicted until the cache is back near 6 GiB.

`--cache-min-free-bytes` is the free-space guard for the filesystem that contains the cache. If a single chunk is larger than the cache cap, it is served without being stored.

## Testing

The default public smoke target is Spacewar:

- app `480`
- depot `481`
- branch `public`

That depot is small and works anonymously, so it is useful for validating Steam login, manifest resolution, CDN downloads, cache reuse, and FUSE mounting before testing private depots.

Run the public test script:

```bash
CACHE_MAX_BYTES=8G CACHE_LOW_WATERMARK=6G CACHE_MIN_FREE_BYTES=2G REQUIRE_FUSE=1 \
  scripts/ci/test-steam-depotfs-public.sh
```

The repository includes `.github/workflows/public-test.yml`, which runs the same public test on pushes and pull requests. The workflow builds the project, reads `installscript.vdf` from the Spacewar depot, mounts the depot through FUSE, and verifies the file is visible through the mounted filesystem.

The workflow also includes an authenticated smoke test for pushes and manual runs. It logs in with configured Steam credentials, resolves the configured depot, and reads a small file from that depot. Configure either:

- `OP_SERVICE_ACCOUNT_TOKEN` secret plus `OP_STEAM_USERNAME_REF` and `OP_STEAM_PASSWORD_REF` variables for 1Password-backed credentials.
- `STEAM_USERNAME` and `STEAM_PASSWORD` secrets, or `STEAM_ACCESS_TOKEN` secret, for direct credentials.

Set `STEAM_DEPOTFS_AUTH_APP_ID`, `STEAM_DEPOTFS_AUTH_DEPOT_ID`, and `STEAM_DEPOTFS_AUTH_BRANCH` variables to choose the authenticated test target. `STEAM_DEPOTFS_AUTH_READ_PATH` is optional; when omitted, the smoke command chooses a small readable file from the manifest.

## Releases

Releases are created directly from `main` after the `Depot tests` workflow succeeds. The release workflow scans conventional commits since the latest `v*` tag:

- breaking changes create a major release
- `feat:` creates a minor release
- `fix:`, `perf:`, `refactor:`, and `revert:` create a patch release
- docs-only or CI-only changes do not create a release

Each release publishes a Linux x64 archive with both a versioned asset name and the stable `SteamDepotFS-linux-x64.tar.gz` asset name for `releases/latest` downloads.
