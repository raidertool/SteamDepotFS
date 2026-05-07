using System.Security.Cryptography;
using System.Text;
using SteamKit2;

internal static class DepotFileSystemMetadata
{
    public const ulong BlockSize = 4096;
    public const ulong DiskBlockSize = 512;
    public const int MaxNameBytes = 255;

    public static long FileSize(DepotManifest.FileData file)
        => file.Flags.HasFlag(EDepotFileFlag.Symlink) && file.LinkTarget is not null
            ? LinkTargetBytes(file.LinkTarget).Length
            : checked((long)file.TotalSize);

    public static long DiskBlocks(long size)
        => (size + (long)DiskBlockSize - 1) / (long)DiskBlockSize;

    public static ulong RoundUp(ulong value, ulong unit)
        => value == 0 ? 0 : ((value + unit - 1) / unit) * unit;

    public static ulong StableId(string path)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(path));
        return BitConverter.ToUInt64(hash, 0) & 0x7fffffffffffffff;
    }

    public static byte[] LinkTargetBytes(string linkTarget)
        => Encoding.UTF8.GetBytes(linkTarget);

    public static int ReadLinkTarget(string linkTarget, long offset, byte[] destination)
    {
        var bytes = LinkTargetBytes(linkTarget);
        if (offset < 0 || offset >= bytes.Length || destination.Length == 0)
        {
            return 0;
        }

        var start = checked((int)offset);
        var length = Math.Min(destination.Length, bytes.Length - start);
        Buffer.BlockCopy(bytes, start, destination, 0, length);
        return length;
    }

    public static IEnumerable<DepotDirectoryEntry> EnumerateDirectory(DirectoryNode directory)
    {
        yield return new DepotDirectoryEntry(".", directory.Path, directory, null);
        yield return new DepotDirectoryEntry("..", directory.Path, directory, null);

        foreach (var child in directory.Directories.Values.OrderBy(static child => child.Name, StringComparer.Ordinal))
        {
            yield return new DepotDirectoryEntry(child.Name, child.Path, child, null);
        }

        foreach (var child in directory.Files.OrderBy(static child => child.Key, StringComparer.Ordinal))
        {
            yield return new DepotDirectoryEntry(child.Key, child.Value.FileName, null, child.Value);
        }
    }

    public static string CombinePath(string parent, string child)
        => parent.Length == 0 ? child : parent + "/" + child;

    public static long ToUnixTimeSeconds(DateTime value)
        => new DateTimeOffset(value.ToUniversalTime()).ToUnixTimeSeconds();

    public static ulong ToFileTime(DateTime value)
    {
        var utc = value.Kind == DateTimeKind.Utc ? value : DateTime.SpecifyKind(value, DateTimeKind.Utc);
        var minimum = DateTime.FromFileTimeUtc(0);
        return utc <= minimum ? 0 : (ulong)utc.ToFileTimeUtc();
    }
}

internal readonly record struct DepotDirectoryEntry(
    string Name,
    string Path,
    DirectoryNode? Directory,
    DepotManifest.FileData? File);
