#if MACFUSE_MOUNT
internal static unsafe partial class MacFuseMountSupport
{
    public static MountPreflight Check(string mountPoint)
    {
        MacFuseNative.ConfigureResolver();
        if (!MacFuseRuntime.IsInstalled)
        {
            return MountPreflight.Failure(
                mountPoint,
                "macFUSE is not installed.",
                "Install macFUSE from https://macfuse.github.io/ and rerun the mount command.");
        }

        if (!MacFuseNative.TryLoad(out var loadError))
        {
            return MountPreflight.Failure(
                mountPoint,
                "macFUSE libfuse is not available to SteamDepotFS.",
                loadError,
                "Install or reinstall macFUSE from https://macfuse.github.io/ and rerun the mount command.");
        }

        return MountPreflight.Success(DepotMountBackend.MacFuse, mountPoint);
    }

    public static IDepotMountHost Create(string mountPoint, DepotReader reader)
    {
        MacFuseNative.ConfigureResolver();
        return new MacFuseDepotMountHost(mountPoint, reader);
    }

    private sealed class MacFuseDepotMountHost : IDepotMountHost
    {
        private readonly MacFuseFileSystem _fileSystem;

        public MacFuseDepotMountHost(string mountPoint, DepotReader reader)
        {
            MountPoint = mountPoint;
            _fileSystem = new MacFuseFileSystem(mountPoint, reader);
        }

        public string MountPoint { get; }

        public void Start()
            => _fileSystem.Start();

        public void Dispose()
            => _fileSystem.Dispose();
    }
}
#endif
