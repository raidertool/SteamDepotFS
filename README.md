# SteamDepotFs

`SteamDepotFs` is a small SteamKit-based depot reader with a read-only Linux FUSE adapter. It resolves a depot manifest, downloads only chunks needed for reads, and stores decompressed chunks in a bounded on-disk cache.

The default public smoke target is Spacewar:

- app `480`
- depot `481`
- branch `public`

That depot is intentionally small and works anonymously, so it is useful for CI and WSL validation before using private credentials.

## Commands

```bash
dotnet run --project src/SteamDepotFs/SteamDepotFs.csproj -c Release -- smoke
dotnet run --project src/SteamDepotFs/SteamDepotFs.csproj -c Release -- list --limit 20
dotnet run --project src/SteamDepotFs/SteamDepotFs.csproj -c Release -- read --path installscript.vdf --out /tmp/installscript.vdf
```

Mounting is Linux/WSL only:

```bash
mkdir -p /tmp/steam-depotfs
dotnet run --project src/SteamDepotFs/SteamDepotFs.csproj -c Release -- mount --mount-point /tmp/steam-depotfs
```

Unmount with:

```bash
fusermount3 -u /tmp/steam-depotfs || fusermount -u /tmp/steam-depotfs
```

## Cache Bounds

Set cache limits with explicit byte caps. Suffixes `K`, `M`, `G`, and `T` are accepted.

```bash
dotnet run --project src/SteamDepotFs/SteamDepotFs.csproj -c Release -- smoke \
  --cache-dir "${RUNNER_TEMP:-/tmp}/steam-depotfs-cache" \
  --cache-max-bytes 8G \
  --cache-low-watermark 7G \
  --cache-min-free-bytes 1G
```

`--cache-max-bytes` is the hard cap. When storing a new chunk would cross it, the cache evicts least-recently-used chunk files until it reaches `--cache-low-watermark`. If a single chunk is larger than the cap, it is served without being stored.

## Auth

Anonymous login is used unless credentials are provided. These can be passed as args or environment variables:

- `--username` or `STEAM_USERNAME`
- `--password` or `STEAM_PASSWORD`
- `--auth-code` or `STEAM_AUTH_CODE`
- `--two-factor-code` or `STEAM_TWO_FACTOR_CODE`
- `--access-token` or `STEAM_ACCESS_TOKEN`
- `--login-id` or `STEAM_LOGIN_ID`
- `--remember-password` or `STEAM_REMEMBER_PASSWORD=1`

Private branches may also need:

```bash
--manifest <gid> --branch <name> --branch-password-hash <hash>
```

## WSL or GitHub Runner Test

Install FUSE support first:

```bash
sudo apt-get update
sudo apt-get install -y dotnet-sdk-8.0 fuse3 libfuse2
sudo modprobe fuse || true
```

Run the public test script:

```bash
CACHE_MAX_BYTES=8G CACHE_LOW_WATERMARK=7G REQUIRE_FUSE=1 \
  scripts/ci/test-steam-depotfs-public.sh
```

For GitHub Actions, prefer a hosted Ubuntu runner directly instead of a job container. Job containers usually do not expose `/dev/fuse`, while `ubuntu-24.04` can load/use FUSE with `sudo`.

Minimal workflow step:

```yaml
- name: Install FUSE
  run: |
    sudo apt-get update
    sudo apt-get install -y fuse3 libfuse2
    sudo modprobe fuse || true

- name: Test SteamDepotFs public depot
  run: scripts/ci/test-steam-depotfs-public.sh
  env:
    CACHE_MAX_BYTES: 8G
    CACHE_LOW_WATERMARK: 7G
    CACHE_MIN_FREE_BYTES: 1G
    REQUIRE_FUSE: 1
```
