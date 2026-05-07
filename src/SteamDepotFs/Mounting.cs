#if FUSE_MOUNT
using Mono.Fuse.NETStandard;
#endif

internal interface IDepotMountHost : IDisposable
{
    string MountPoint { get; }
    void Start();
}

internal enum DepotMountBackend
{
    LinuxFuse,
    MacFuse,
    WinFsp
}

internal sealed record MountPreflight(
    DepotMountBackend? Backend,
    string MountPoint,
    IReadOnlyList<string> Errors)
{
    public bool Succeeded => Backend is not null && Errors.Count == 0;

    public static MountPreflight Success(DepotMountBackend backend, string mountPoint)
        => new(backend, mountPoint, []);

    public static MountPreflight Failure(string mountPoint, params string[] errors)
        => new(null, mountPoint, errors);

    public void ThrowIfFailed()
    {
        if (Succeeded)
        {
            return;
        }

        throw new InvalidOperationException(string.Join(Environment.NewLine, Errors));
    }
}

internal static class DepotMountFactory
{
    public static MountPreflight Check(string mountPoint)
    {
        if (string.IsNullOrWhiteSpace(mountPoint))
        {
            return MountPreflight.Failure(mountPoint, "mount requires --mount-point <path>.");
        }

        if (OperatingSystem.IsWindows())
        {
            return WinFspMountSupport.Check(mountPoint);
        }

        if (OperatingSystem.IsLinux())
        {
            return CheckLinuxFuse(mountPoint);
        }

        if (OperatingSystem.IsMacOS())
        {
            return CheckMacFuse(mountPoint);
        }

        return MountPreflight.Failure(
            mountPoint,
            $"Mount is not supported on this OS: {Environment.OSVersion.Platform}.");
    }

    public static IDepotMountHost Create(MountPreflight preflight, DepotReader reader)
    {
        preflight.ThrowIfFailed();
        return preflight.Backend switch
        {
            DepotMountBackend.LinuxFuse => CreateLinuxFuse(preflight.MountPoint, reader),
            DepotMountBackend.MacFuse => CreateMacFuse(preflight.MountPoint, reader),
            DepotMountBackend.WinFsp => WinFspMountSupport.Create(preflight.MountPoint, reader),
            _ => throw new PlatformNotSupportedException("No mount backend is available.")
        };
    }

    internal static bool IsWindowsDriveMountPoint(string mountPoint)
    {
        if (mountPoint.Length is not 2 and not 3)
        {
            return false;
        }

        if (!char.IsAsciiLetter(mountPoint[0]) || mountPoint[1] != ':')
        {
            return false;
        }

        return mountPoint.Length == 2 || mountPoint[2] is '\\' or '/';
    }

    private static MountPreflight CheckLinuxFuse(string mountPoint)
    {
#if FUSE_MOUNT
        var fullMountPoint = Path.GetFullPath(mountPoint);
        if (!TryValidateDirectoryMountPoint("Linux FUSE", fullMountPoint, out var error))
        {
            return MountPreflight.Failure(fullMountPoint, error);
        }

        if (!File.Exists("/dev/fuse"))
        {
            return MountPreflight.Failure(
                fullMountPoint,
                "Linux FUSE device is not available at /dev/fuse.",
                "Install and load FUSE, for example: sudo apt-get install -y fuse3 libfuse2 && sudo modprobe fuse");
        }

        return MountPreflight.Success(DepotMountBackend.LinuxFuse, fullMountPoint);
#else
        return MountPreflight.Failure(
            mountPoint,
            "This build does not include Linux FUSE mount support.",
            "Use a Linux build of SteamDepotFS or rebuild with -p:EnableFuse=true.");
#endif
    }

    private static IDepotMountHost CreateLinuxFuse(string mountPoint, DepotReader reader)
    {
#if FUSE_MOUNT
        return new FuseDepotMountHost(mountPoint, reader);
#else
        throw new PlatformNotSupportedException("This build does not include Linux FUSE mount support.");
#endif
    }

    private static MountPreflight CheckMacFuse(string mountPoint)
    {
#if MACFUSE_MOUNT
        var fullMountPoint = Path.GetFullPath(mountPoint);
        if (File.Exists(fullMountPoint))
        {
            return MountPreflight.Failure(
                fullMountPoint,
                $"macOS FUSE mount point is a file, not a directory: {fullMountPoint}");
        }

        var macFuse = MacFuseMountSupport.Check(fullMountPoint);
        if (!macFuse.Succeeded)
        {
            return macFuse;
        }

        if (!Directory.Exists(fullMountPoint) && !MacFuseRuntime.ShouldUseFSKitBackend(fullMountPoint))
        {
            return MountPreflight.Failure(
                fullMountPoint,
                $"macOS FUSE mount point does not exist: {fullMountPoint}");
        }

        return macFuse;
#else
        return MountPreflight.Failure(
            mountPoint,
            "This build does not include macOS FUSE mount support.",
            "Use a macOS release archive or rebuild with -p:EnableMacFuse=true.");
#endif
    }

    private static IDepotMountHost CreateMacFuse(string mountPoint, DepotReader reader)
    {
#if MACFUSE_MOUNT
        return MacFuseMountSupport.Create(mountPoint, reader);
#else
        throw new PlatformNotSupportedException("This build does not include macOS FUSE mount support.");
#endif
    }

    private static bool TryValidateDirectoryMountPoint(string backendName, string mountPoint, out string error)
    {
        if (File.Exists(mountPoint))
        {
            error = $"{backendName} mount point is a file, not a directory: {mountPoint}";
            return false;
        }

        if (!Directory.Exists(mountPoint))
        {
            error = $"{backendName} mount point does not exist: {mountPoint}";
            return false;
        }

        error = string.Empty;
        return true;
    }

#if FUSE_MOUNT
    private sealed class FuseDepotMountHost : IDepotMountHost
    {
        private readonly DepotFuseFileSystem _fileSystem;

        public FuseDepotMountHost(string mountPoint, DepotReader reader)
        {
            _fileSystem = new DepotFuseFileSystem(mountPoint, reader);
        }

        public string MountPoint => _fileSystem.MountPoint;

        public void Start()
            => _fileSystem.Start();

        public void Dispose()
            => _fileSystem.Dispose();
    }
#endif
}

#if !WINDOWS_MOUNT
internal static class WinFspMountSupport
{
    public static MountPreflight Check(string mountPoint)
        => MountPreflight.Failure(
            mountPoint,
            "This build does not include Windows WinFsp mount support.",
            "Use a Windows release archive or rebuild with -p:EnableWinFsp=true.");

    public static IDepotMountHost Create(string mountPoint, DepotReader reader)
        => throw new PlatformNotSupportedException("This build does not include Windows WinFsp mount support.");
}
#endif
