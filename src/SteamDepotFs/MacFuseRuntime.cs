#if MACFUSE_MOUNT
using System.Xml.Linq;

internal static class MacFuseRuntime
{
    private const string BundlePath = "/Library/Filesystems/macfuse.fs";
    private const string InfoPlistPath = BundlePath + "/Contents/Info.plist";
    private const string MountHelperPath = BundlePath + "/Contents/Resources/mount_macfuse";

    public static bool IsInstalled => Directory.Exists(BundlePath) && File.Exists(MountHelperPath);

    public static bool ShouldUseFSKitBackend(string mountPoint)
        => OperatingSystem.IsMacOSVersionAtLeast(15, 4) &&
           VersionAtLeast(5, 0) &&
           IsFSKitMountPoint(mountPoint);

    internal static bool IsFSKitMountPoint(string mountPoint)
        => IsUnderVolumes(mountPoint);

    private static bool VersionAtLeast(int major, int minor)
    {
        var version = ReadVersion();
        return version is not null && version >= new Version(major, minor);
    }

    private static Version? ReadVersion()
    {
        if (!File.Exists(InfoPlistPath))
        {
            return null;
        }

        try
        {
            var document = XDocument.Load(InfoPlistPath);
            var elements = document.Descendants().ToList();
            for (var i = 0; i < elements.Count - 1; i++)
            {
                if (elements[i].Name.LocalName == "key" &&
                    elements[i].Value == "CFBundleShortVersionString" &&
                    Version.TryParse(elements[i + 1].Value, out var version))
                {
                    return version;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Xml.XmlException)
        {
        }

        return null;
    }

    private static bool IsUnderVolumes(string mountPoint)
    {
        var fullPath = Path.GetFullPath(mountPoint).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return fullPath.StartsWith("/Volumes/", StringComparison.Ordinal);
    }
}
#endif
