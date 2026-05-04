# SteamDepotFs

`SteamDepotFs` is a SteamKit-based, read-only Steam depot filesystem. It resolves depot manifests, downloads only the chunks needed for reads, and keeps decompressed chunks in a bounded on-disk cache.

It was created for GitHub workflows that operate on Steam game files but have limited disk space. It can also be used anywhere a read-only, on-demand view of a depot is useful.

## Installation

Install the .NET 8 SDK.

For mounted filesystem access on Linux, install FUSE:

```bash
sudo apt-get update
sudo apt-get install -y fuse3 libfuse2
sudo modprobe fuse || true
```

Build the project:

```bash
dotnet build src/SteamDepotFs/SteamDepotFs.csproj -c Release
```

The `smoke`, `list`, and `read` commands do not require FUSE. Only `mount` requires FUSE.

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
  --cache-low-watermark 7G \
  --cache-min-free-bytes 1G
```

`--cache-max-bytes` is the hard cap. When storing a new chunk would cross it, the cache evicts least-recently-used chunk files until it reaches `--cache-low-watermark`. If a single chunk is larger than the cap, it is served without being stored.

## Testing

The default public smoke target is Spacewar:

- app `480`
- depot `481`
- branch `public`

That depot is small and works anonymously, so it is useful for validating Steam login, manifest resolution, CDN downloads, cache reuse, and FUSE mounting before testing private depots.

Run the public test script:

```bash
CACHE_MAX_BYTES=8G CACHE_LOW_WATERMARK=7G REQUIRE_FUSE=1 \
  scripts/ci/test-steam-depotfs-public.sh
```

The repository includes `.github/workflows/public-test.yml`, which runs the same public test on pushes and pull requests. The workflow builds the project, reads `installscript.vdf` from the Spacewar depot, mounts the depot through FUSE, and verifies the file is visible through the mounted filesystem.
