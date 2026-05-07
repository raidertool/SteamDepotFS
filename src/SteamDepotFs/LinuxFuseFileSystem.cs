#if FUSE_MOUNT
using Mono.Fuse.NETStandard;
using Mono.Unix.Native;
using SteamKit2;

internal sealed class DepotFuseFileSystem : FileSystem
{
    private static readonly FilePermissions DirectoryMode =
        FilePermissions.S_IFDIR |
        FilePermissions.S_IRUSR | FilePermissions.S_IXUSR |
        FilePermissions.S_IRGRP | FilePermissions.S_IXGRP |
        FilePermissions.S_IROTH | FilePermissions.S_IXOTH;

    private static readonly FilePermissions FileMode =
        FilePermissions.S_IFREG |
        FilePermissions.S_IRUSR |
        FilePermissions.S_IRGRP |
        FilePermissions.S_IROTH;

    private static readonly FilePermissions ExecutableFileMode =
        FileMode |
        FilePermissions.S_IXUSR |
        FilePermissions.S_IXGRP |
        FilePermissions.S_IXOTH;

    private static readonly FilePermissions SymlinkMode =
        FilePermissions.S_IFLNK |
        FilePermissions.S_IRUSR |
        FilePermissions.S_IRGRP |
        FilePermissions.S_IROTH;

    private readonly DepotReader _reader;
    private readonly long _mountedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    public DepotFuseFileSystem(string mountPoint, DepotReader reader)
        : base(mountPoint)
    {
        _reader = reader;
        Name = "steam-depotfs";
        MultiThreaded = true;
        EnableKernelCache = true;
        EnableDirectIO = false;
        EnableLargeReadRequests = true;
        MaxReadSize = 1024 * 1024;
        AttributeTimeout = 60;
        PathTimeout = 60;

        if (Environment.GetEnvironmentVariable("STEAM_DEPOTFS_FUSE_DEBUG") == "1")
        {
            EnableFuseDebugOutput = true;
        }
    }

    protected override Errno OnGetPathStatus(string path, out Stat stat)
    {
        if (_reader.Index.TryGetDirectory(path, out var directory))
        {
            stat = StatForDirectory(directory);
            return 0;
        }

        if (_reader.Index.TryGetFile(path, out var file))
        {
            stat = StatForFile(file);
            return 0;
        }

        stat = default;
        return Errno.ENOENT;
    }

    protected override Errno OnReadSymbolicLink(string link, out string target)
    {
        target = string.Empty;
        if (!_reader.Index.TryGetFile(link, out var file))
        {
            return Errno.ENOENT;
        }

        if (!file.Flags.HasFlag(EDepotFileFlag.Symlink) || string.IsNullOrEmpty(file.LinkTarget))
        {
            return Errno.EINVAL;
        }

        target = file.LinkTarget;
        return 0;
    }

    protected override Errno OnOpenHandle(string file, OpenedPathInfo info)
    {
        if (!_reader.Index.TryGetFile(file, out _))
        {
            return _reader.Index.TryGetDirectory(file, out _) ? Errno.EISDIR : Errno.ENOENT;
        }

        if ((((int)info.OpenAccess) & 3) != (int)OpenFlags.O_RDONLY)
        {
            return Errno.EROFS;
        }

        info.KeepCache = true;
        info.DirectIO = false;
        return 0;
    }

    protected override Errno OnReadHandle(string file, OpenedPathInfo info, byte[] buf, long offset, out int bytesWritten)
    {
        bytesWritten = 0;
        try
        {
            if (!_reader.Index.TryGetFile(file, out var depotFile))
            {
                return Errno.ENOENT;
            }

            bytesWritten = _reader.ReadAsync(depotFile, offset, buf, CancellationToken.None).GetAwaiter().GetResult();
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"read failed for {file}: {ex.Message}");
            return Errno.EIO;
        }
    }

    protected override Errno OnOpenDirectory(string directory, OpenedPathInfo info)
        => _reader.Index.TryGetDirectory(directory, out _) ? 0 : Errno.ENOENT;

    protected override Errno OnReleaseDirectory(string directory, OpenedPathInfo info)
        => 0;

    protected override Errno OnReadDirectory(string directory, OpenedPathInfo info, out IEnumerable<DirectoryEntry> paths)
    {
        paths = [];
        if (!_reader.Index.TryGetDirectory(directory, out var node))
        {
            return Errno.ENOENT;
        }

        paths = DepotFileSystemMetadata.EnumerateDirectory(node)
            .Select(entry => Entry(entry.Name, entry.Directory is not null
                ? StatForDirectory(entry.Directory)
                : StatForFile(entry.File!)))
            .ToArray();
        return 0;
    }

    protected override Errno OnAccessPath(string path, AccessModes mode)
    {
        if (!_reader.Index.Exists(path))
        {
            return Errno.ENOENT;
        }

        return mode.HasFlag(AccessModes.W_OK) ? Errno.EROFS : 0;
    }

    protected override Errno OnGetFileSystemStatus(string path, out Statvfs buf)
    {
        var blockSize = DepotFileSystemMetadata.BlockSize;
        var blocks = Math.Max(1, DepotFileSystemMetadata.RoundUp(_reader.Manifest.TotalUncompressedSize, blockSize) / blockSize);
        buf = default;
        buf.f_bsize = blockSize;
        buf.f_frsize = blockSize;
        buf.f_blocks = blocks;
        buf.f_bfree = 0;
        buf.f_bavail = 0;
        buf.f_files = (ulong)_reader.Index.AllFiles.Count + 1;
        buf.f_ffree = 0;
        buf.f_favail = 0;
        buf.f_namemax = DepotFileSystemMetadata.MaxNameBytes;
        return 0;
    }

    protected override Errno OnCreateHandle(string file, OpenedPathInfo info, FilePermissions mode) => Errno.EROFS;
    protected override Errno OnWriteHandle(string file, OpenedPathInfo info, byte[] buf, long offset, out int bytesRead)
    {
        bytesRead = 0;
        return Errno.EROFS;
    }

    protected override Errno OnCreateDirectory(string directory, FilePermissions mode) => Errno.EROFS;
    protected override Errno OnRemoveFile(string file) => Errno.EROFS;
    protected override Errno OnRemoveDirectory(string directory) => Errno.EROFS;
    protected override Errno OnRenamePath(string oldpath, string newpath) => Errno.EROFS;
    protected override Errno OnChangePathPermissions(string path, FilePermissions mode) => Errno.EROFS;
    protected override Errno OnChangePathOwner(string path, long owner, long group) => Errno.EROFS;
    protected override Errno OnTruncateFile(string file, long length) => Errno.EROFS;

    private static DirectoryEntry Entry(string name, Stat stat) => new(name) { Stat = stat };

    private Stat StatForDirectory(DirectoryNode directory)
        => new()
        {
            st_ino = DepotFileSystemMetadata.StableId(directory.Path),
            st_mode = DirectoryMode,
            st_nlink = 2,
            st_size = 0,
            st_blksize = (long)DepotFileSystemMetadata.BlockSize,
            st_blocks = 0,
            st_atime = _mountedAt,
            st_mtime = _mountedAt,
            st_ctime = _mountedAt
        };

    private Stat StatForFile(DepotManifest.FileData file)
    {
        var mode = file.Flags.HasFlag(EDepotFileFlag.Symlink)
            ? SymlinkMode
            : file.Flags.HasFlag(EDepotFileFlag.Executable)
                ? ExecutableFileMode
                : FileMode;

        var size = DepotFileSystemMetadata.FileSize(file);
        return new Stat
        {
            st_ino = DepotFileSystemMetadata.StableId(file.FileName),
            st_mode = mode,
            st_nlink = 1,
            st_size = size,
            st_blksize = (long)DepotFileSystemMetadata.BlockSize,
            st_blocks = DepotFileSystemMetadata.DiskBlocks(size),
            st_atime = _mountedAt,
            st_mtime = DepotFileSystemMetadata.ToUnixTimeSeconds(_reader.Manifest.CreationTime),
            st_ctime = _mountedAt
        };
    }
}
#endif
