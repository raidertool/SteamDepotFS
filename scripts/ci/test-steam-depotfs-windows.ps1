param(
    [string] $Configuration = "Release",
    [string] $RuntimeIdentifier = "win-x64",
    [string] $MountPoint = "X:",
    [string] $PublishDir = "",
    [string] $CacheDir = "",
    [int] $TimeoutSeconds = 90
)

$ErrorActionPreference = "Stop"

function Resolve-WorkRoot {
    if (-not [string]::IsNullOrWhiteSpace($env:RUNNER_TEMP)) {
        return $env:RUNNER_TEMP
    }

    return [System.IO.Path]::GetTempPath()
}

function Join-MountPath {
    param(
        [string] $Root,
        [string] $RelativePath
    )

    if ($Root -match "^[A-Za-z]:\\?$") {
        return "$($Root.TrimEnd('\'))\$RelativePath"
    }

    return Join-Path $Root $RelativePath
}

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "../..")
$project = Join-Path $repoRoot "src/SteamDepotFs/SteamDepotFs.csproj"
$workRoot = Resolve-WorkRoot
if ([string]::IsNullOrWhiteSpace($PublishDir)) {
    $PublishDir = Join-Path $workRoot "steam-depotfs-windows-publish"
}

if ([string]::IsNullOrWhiteSpace($CacheDir)) {
    $CacheDir = Join-Path $workRoot "steam-depotfs-windows-cache"
}

Remove-Item $PublishDir -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Path $PublishDir, $CacheDir -Force | Out-Null

dotnet publish $project `
    -c $Configuration `
    -r $RuntimeIdentifier `
    --self-contained true `
    -p:UseAppHost=true `
    -o $PublishDir

$exe = Join-Path $PublishDir "SteamDepotFs.exe"
if (-not (Test-Path $exe)) {
    throw "SteamDepotFS executable was not published to $exe."
}

$commonArgs = @(
    "--app", "480",
    "--depot", "481",
    "--cache-dir", $CacheDir,
    "--cache-max-bytes", "1G",
    "--cache-low-watermark", "512M",
    "--cache-min-free-bytes", "256M",
    "--timeout", "180"
)

& $exe smoke @commonArgs
if ($LASTEXITCODE -ne 0) {
    throw "SteamDepotFS smoke failed with exit code $LASTEXITCODE."
}

$mountStdout = Join-Path $workRoot "steam-depotfs-windows-mount.out.log"
$mountStderr = Join-Path $workRoot "steam-depotfs-windows-mount.err.log"
Remove-Item $mountStdout, $mountStderr -Force -ErrorAction SilentlyContinue

$mountArgs = @("mount", "--mount-point", $MountPoint) + $commonArgs
$mountProcess = Start-Process `
    -FilePath $exe `
    -ArgumentList $mountArgs `
    -PassThru `
    -NoNewWindow `
    -RedirectStandardOutput $mountStdout `
    -RedirectStandardError $mountStderr

try {
    $targetPath = Join-MountPath $MountPoint "installscript.vdf"
    $deadline = [DateTimeOffset]::UtcNow.AddSeconds($TimeoutSeconds)
    while ([DateTimeOffset]::UtcNow -lt $deadline) {
        if (Test-Path $targetPath) {
            Get-Item $targetPath | Format-List FullName, Length
            Get-FileHash $targetPath -Algorithm SHA256 | Format-List Path, Hash
            Write-Host "WinFsp mount test passed."
            return
        }

        if ($mountProcess.HasExited) {
            Write-Error "SteamDepotFS mount process exited early with code $($mountProcess.ExitCode)."
        }

        Start-Sleep -Seconds 1
    }

    throw "Timed out waiting for $targetPath."
}
finally {
    if (-not $mountProcess.HasExited) {
        Stop-Process -Id $mountProcess.Id -Force -ErrorAction SilentlyContinue
        Wait-Process -Id $mountProcess.Id -Timeout 10 -ErrorAction SilentlyContinue
    }

    Write-Host "--- mount stdout ---"
    if (Test-Path $mountStdout) {
        Get-Content $mountStdout -ErrorAction SilentlyContinue
    }

    Write-Host "--- mount stderr ---"
    if (Test-Path $mountStderr) {
        Get-Content $mountStderr -ErrorAction SilentlyContinue
    }
}
