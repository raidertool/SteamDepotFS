#if WINDOWS_MOUNT
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using Fsp;
using SteamKit2;
using FileInfo = Fsp.Interop.FileInfo;
using VolumeInfo = Fsp.Interop.VolumeInfo;

internal static class WinFspMountSupport
{
    public static MountPreflight Check(string mountPoint)
    {
        var normalizedMountPoint = NormalizeMountPoint(mountPoint);
        var mountPointError = ValidateMountPoint(normalizedMountPoint);
        if (mountPointError is not null)
        {
            return MountPreflight.Failure(normalizedMountPoint, mountPointError);
        }

        try
        {
            using var host = new FileSystemHost(new PreflightFileSystem());
            var status = host.Preflight(normalizedMountPoint);
            return status == FileSystemBase.STATUS_SUCCESS
                ? MountPreflight.Success(DepotMountBackend.WinFsp, normalizedMountPoint)
                : MountPreflight.Failure(
                    normalizedMountPoint,
                    $"WinFsp preflight failed for {normalizedMountPoint}: NTSTATUS 0x{(uint)status:x8}",
                    "Install WinFsp 2.1 or later from https://winfsp.dev/rel/ and rerun the mount command.");
        }
        catch (Exception ex) when (IsWinFspLoadFailure(ex))
        {
            return MountPreflight.Failure(
                normalizedMountPoint,
                "WinFsp is not installed or its runtime DLLs are unavailable.",
                "Install WinFsp 2.1 or later from https://winfsp.dev/rel/ and rerun the mount command.");
        }
    }

    public static IDepotMountHost Create(string mountPoint, DepotReader reader)
        => new WinFspDepotMountHost(mountPoint, reader);

    private static string NormalizeMountPoint(string mountPoint)
    {
        mountPoint = mountPoint.Trim();
        if (DepotMountFactory.IsWindowsDriveMountPoint(mountPoint))
        {
            return char.ToUpperInvariant(mountPoint[0]) + ":";
        }

        return Path.GetFullPath(mountPoint);
    }

    private static string? ValidateMountPoint(string mountPoint)
    {
        if (DepotMountFactory.IsWindowsDriveMountPoint(mountPoint))
        {
            var driveRoot = mountPoint + Path.DirectorySeparatorChar;
            if (DriveInfo.GetDrives().Any(d => d.Name.Equals(driveRoot, StringComparison.OrdinalIgnoreCase)))
            {
                return $"Windows mount drive is already in use: {mountPoint}";
            }

            return null;
        }

        if (File.Exists(mountPoint))
        {
            return $"Windows mount point is a file, not a directory: {mountPoint}";
        }

        if (Directory.Exists(mountPoint))
        {
            return null;
        }

        var parent = Path.GetDirectoryName(mountPoint);
        return !string.IsNullOrWhiteSpace(parent) && Directory.Exists(parent)
            ? null
            : $"Windows mount point parent does not exist: {mountPoint}";
    }

    private static bool IsWinFspLoadFailure(Exception ex)
        => ex is DllNotFoundException or BadImageFormatException ||
           ex is TypeInitializationException { InnerException: not null } &&
           IsWinFspLoadFailure(ex.InnerException);

    private sealed class PreflightFileSystem : FileSystemBase
    {
    }

    private sealed class WinFspDepotMountHost : IDepotMountHost
    {
        private readonly FileSystemHost _host;

        public WinFspDepotMountHost(string mountPoint, DepotReader reader)
        {
            MountPoint = mountPoint;
            _host = new FileSystemHost(new WinFspDepotFileSystem(reader));
        }

        public string MountPoint { get; }

        public void Start()
        {
            var status = _host.MountEx(MountPoint, ThreadCount: 0, SecurityDescriptor: null!, Synchronized: false, DebugLog: 0);
            if (status != FileSystemBase.STATUS_SUCCESS)
            {
                throw new InvalidOperationException($"WinFsp mount failed for {MountPoint}: NTSTATUS 0x{(uint)status:x8}");
            }

            Thread.Sleep(Timeout.Infinite);
        }

        public void Dispose()
            => _host.Dispose();
    }

    private sealed class WinFspDepotFileSystem : FileSystemBase
    {
        private const uint AllocationUnit = 4096;
        private static readonly byte[] SecurityDescriptor = CreateSecurityDescriptor();
        private readonly DepotReader _reader;
        private readonly ulong _createdAt;
        private readonly ulong _mountedAt;

        public WinFspDepotFileSystem(DepotReader reader)
        {
            _reader = reader;
            _createdAt = ToFileTime(reader.Manifest.CreationTime);
            _mountedAt = ToFileTime(DateTime.UtcNow);
        }

        public override int Init(object host)
        {
            if (host is FileSystemHost fileSystemHost)
            {
                fileSystemHost.SectorSize = 512;
                fileSystemHost.SectorsPerAllocationUnit = 8;
                fileSystemHost.MaxComponentLength = 255;
                fileSystemHost.CaseSensitiveSearch = true;
                fileSystemHost.CasePreservedNames = true;
                fileSystemHost.UnicodeOnDisk = true;
                fileSystemHost.PersistentAcls = false;
                fileSystemHost.ReparsePoints = false;
                fileSystemHost.NamedStreams = false;
                fileSystemHost.FileInfoTimeout = 60_000;
                fileSystemHost.DirInfoTimeout = 60_000;
                fileSystemHost.VolumeInfoTimeout = 60_000;
                fileSystemHost.SecurityTimeout = 60_000;
                fileSystemHost.PostCleanupWhenModifiedOnly = true;
                fileSystemHost.FileSystemName = "SteamDepotFS";
            }

            return STATUS_SUCCESS;
        }

        public override int ExceptionHandler(Exception ex)
        {
            Console.Error.WriteLine($"WinFsp operation failed: {ex.Message}");
            return STATUS_UNSUCCESSFUL;
        }

        public override int GetVolumeInfo(out VolumeInfo volumeInfo)
        {
            const ulong blockSize = AllocationUnit;
            volumeInfo = new VolumeInfo
            {
                TotalSize = DepotFileSystemMetadata.RoundUp(_reader.Manifest.TotalUncompressedSize, blockSize),
                FreeSize = 0
            };
            volumeInfo.SetVolumeLabel("SteamDepotFS");
            return STATUS_SUCCESS;
        }

        public override int GetSecurityByName(string fileName, out uint fileAttributes, ref byte[] securityDescriptor)
        {
            fileAttributes = 0;
            if (!TryGetNode(fileName, out var node))
            {
                return STATUS_OBJECT_NAME_NOT_FOUND;
            }

            fileAttributes = FileAttributesFor(node);
            CopySecurityDescriptor(ref securityDescriptor);
            return STATUS_SUCCESS;
        }

        public override int Open(
            string fileName,
            uint createOptions,
            uint grantedAccess,
            out object fileNode,
            out object fileDesc,
            out FileInfo fileInfo,
            out string normalizedName)
        {
            fileNode = null!;
            fileDesc = null!;
            fileInfo = default;
            normalizedName = fileName;

            if (!TryGetNode(fileName, out var node))
            {
                return STATUS_OBJECT_NAME_NOT_FOUND;
            }

            if (node.IsDirectory && (createOptions & FILE_NON_DIRECTORY_FILE) != 0)
            {
                return STATUS_FILE_IS_A_DIRECTORY;
            }

            if (!node.IsDirectory && (createOptions & FILE_DIRECTORY_FILE) != 0)
            {
                return STATUS_NOT_A_DIRECTORY;
            }

            fileNode = node;
            fileInfo = FileInfoFor(node);
            return STATUS_SUCCESS;
        }

        public override void Close(object fileNode, object fileDesc)
        {
        }

        public override int Read(
            object fileNode,
            object fileDesc,
            IntPtr buffer,
            ulong offset,
            uint length,
            out uint bytesTransferred)
        {
            bytesTransferred = 0;
            if (fileNode is not WinFspNode { File: { } file })
            {
                return STATUS_FILE_IS_A_DIRECTORY;
            }

            if (length > int.MaxValue)
            {
                return STATUS_INVALID_PARAMETER;
            }

            var managedBuffer = new byte[(int)length];
            int read;
            if (file.Flags.HasFlag(EDepotFileFlag.Symlink) && file.LinkTarget is not null)
            {
                read = DepotFileSystemMetadata.ReadLinkTarget(file.LinkTarget, checked((long)offset), managedBuffer);
            }
            else
            {
                read = _reader.ReadAsync(file, checked((long)offset), managedBuffer, CancellationToken.None)
                    .GetAwaiter()
                    .GetResult();
            }

            if (read > 0)
            {
                Marshal.Copy(managedBuffer, 0, buffer, read);
            }

            bytesTransferred = (uint)read;
            return STATUS_SUCCESS;
        }

        public override int GetFileInfo(object fileNode, object fileDesc, out FileInfo fileInfo)
        {
            if (fileNode is not WinFspNode node)
            {
                fileInfo = default;
                return STATUS_OBJECT_NAME_NOT_FOUND;
            }

            fileInfo = FileInfoFor(node);
            return STATUS_SUCCESS;
        }

        public override int Flush(object fileNode, object fileDesc, out FileInfo fileInfo)
        {
            fileInfo = fileNode is WinFspNode node ? FileInfoFor(node) : default;
            return STATUS_SUCCESS;
        }

        public override int ReadDirectory(
            object fileNode,
            object fileDesc,
            string pattern,
            string marker,
            IntPtr buffer,
            uint length,
            out uint bytesTransferred)
            => SeekableReadDirectory(fileNode, fileDesc, pattern, marker, buffer, length, out bytesTransferred);

        public override bool ReadDirectoryEntry(
            object fileNode,
            object fileDesc,
            string pattern,
            string marker,
            ref object context,
            out string fileName,
            out FileInfo fileInfo)
        {
            fileName = string.Empty;
            fileInfo = default;
            if (fileNode is not WinFspNode { Directory: { } directory })
            {
                return false;
            }

            if (context is not DirectoryEnumeration enumeration)
            {
                enumeration = new DirectoryEnumeration(DirectoryEntries(directory, marker));
                context = enumeration;
            }

            if (!enumeration.TryMoveNext(out var entry))
            {
                return false;
            }

            fileName = entry.Name;
            fileInfo = entry.FileInfo;
            return true;
        }

        public override int GetDirInfoByName(
            object fileNode,
            object fileDesc,
            string fileName,
            out string normalizedName,
            out FileInfo fileInfo)
        {
            normalizedName = fileName;
            fileInfo = default;
            if (fileNode is not WinFspNode { Directory: { } directory })
            {
                return STATUS_NOT_A_DIRECTORY;
            }

            var childPath = DepotFileSystemMetadata.CombinePath(directory.Path, fileName);
            if (!TryGetNode(childPath, out var child))
            {
                return STATUS_OBJECT_NAME_NOT_FOUND;
            }

            normalizedName = child.Name;
            fileInfo = FileInfoFor(child);
            return STATUS_SUCCESS;
        }

        public override int GetSecurity(object fileNode, object fileDesc, ref byte[] securityDescriptor)
        {
            CopySecurityDescriptor(ref securityDescriptor);
            return STATUS_SUCCESS;
        }

        public override int Create(
            string fileName,
            uint createOptions,
            uint grantedAccess,
            uint fileAttributes,
            byte[] securityDescriptor,
            ulong allocationSize,
            out object fileNode,
            out object fileDesc,
            out FileInfo fileInfo,
            out string normalizedName)
        {
            fileNode = null!;
            fileDesc = null!;
            fileInfo = default;
            normalizedName = fileName;
            return STATUS_MEDIA_WRITE_PROTECTED;
        }

        public override int Write(
            object fileNode,
            object fileDesc,
            IntPtr buffer,
            ulong offset,
            uint length,
            bool writeToEndOfFile,
            bool constrainedIo,
            out uint bytesTransferred,
            out FileInfo fileInfo)
        {
            bytesTransferred = 0;
            fileInfo = fileNode is WinFspNode node ? FileInfoFor(node) : default;
            return STATUS_MEDIA_WRITE_PROTECTED;
        }

        public override int Overwrite(
            object fileNode,
            object fileDesc,
            uint fileAttributes,
            bool replaceFileAttributes,
            ulong allocationSize,
            out FileInfo fileInfo)
        {
            fileInfo = fileNode is WinFspNode node ? FileInfoFor(node) : default;
            return STATUS_MEDIA_WRITE_PROTECTED;
        }

        public override int SetBasicInfo(
            object fileNode,
            object fileDesc,
            uint fileAttributes,
            ulong creationTime,
            ulong lastAccessTime,
            ulong lastWriteTime,
            ulong changeTime,
            out FileInfo fileInfo)
        {
            fileInfo = fileNode is WinFspNode node ? FileInfoFor(node) : default;
            return STATUS_MEDIA_WRITE_PROTECTED;
        }

        public override int SetFileSize(
            object fileNode,
            object fileDesc,
            ulong newSize,
            bool setAllocationSize,
            out FileInfo fileInfo)
        {
            fileInfo = fileNode is WinFspNode node ? FileInfoFor(node) : default;
            return STATUS_MEDIA_WRITE_PROTECTED;
        }

        public override int CanDelete(object fileNode, object fileDesc, string fileName)
            => STATUS_MEDIA_WRITE_PROTECTED;

        public override int Rename(
            object fileNode,
            object fileDesc,
            string fileName,
            string newFileName,
            bool replaceIfExists)
            => STATUS_MEDIA_WRITE_PROTECTED;

        public override int SetSecurity(
            object fileNode,
            object fileDesc,
            AccessControlSections sections,
            byte[] securityDescriptor)
            => STATUS_MEDIA_WRITE_PROTECTED;

        private bool TryGetNode(string path, out WinFspNode node)
        {
            var normalized = FileIndex.Normalize(path);
            if (_reader.Index.TryGetDirectory(normalized, out var directory))
            {
                node = new WinFspNode(directory.Name, normalized, directory, null);
                return true;
            }

            if (_reader.Index.TryGetFile(normalized, out var file))
            {
                node = new WinFspNode(Path.GetFileName(normalized), normalized, null, file);
                return true;
            }

            node = default;
            return false;
        }

        private IEnumerable<DirectoryEntryInfo> DirectoryEntries(DirectoryNode directory, string marker)
        {
            var entries = DepotFileSystemMetadata.EnumerateDirectory(directory)
                .Select(entry => new DirectoryEntryInfo(
                    entry.Name,
                    FileInfoFor(new WinFspNode(entry.Name, entry.Path, entry.Directory, entry.File))));

            return string.IsNullOrEmpty(marker)
                ? entries
                : entries.Where(entry => string.Compare(entry.Name, marker, StringComparison.Ordinal) > 0);
        }

        private FileInfo FileInfoFor(WinFspNode node)
        {
            if (node.Directory is not null)
            {
                return new FileInfo
                {
                    FileAttributes = FileAttributesFor(node),
                    AllocationSize = 0,
                    FileSize = 0,
                    CreationTime = _createdAt,
                    LastAccessTime = _mountedAt,
                    LastWriteTime = _createdAt,
                    ChangeTime = _mountedAt,
                    IndexNumber = DepotFileSystemMetadata.StableId(node.Path),
                    HardLinks = 1
                };
            }

            var file = node.File!;
            var size = DepotFileSystemMetadata.FileSize(file);
            return new FileInfo
            {
                FileAttributes = FileAttributesFor(node),
                AllocationSize = DepotFileSystemMetadata.RoundUp((ulong)size, AllocationUnit),
                FileSize = (ulong)size,
                CreationTime = _createdAt,
                LastAccessTime = _mountedAt,
                LastWriteTime = _createdAt,
                ChangeTime = _mountedAt,
                IndexNumber = DepotFileSystemMetadata.StableId(file.FileName),
                HardLinks = 1
            };
        }

        private static uint FileAttributesFor(WinFspNode node)
        {
            var attributes = System.IO.FileAttributes.ReadOnly;
            attributes |= node.Directory is not null
                ? System.IO.FileAttributes.Directory
                : System.IO.FileAttributes.Normal;
            return (uint)attributes;
        }

        private static ulong ToFileTime(DateTime value)
            => DepotFileSystemMetadata.ToFileTime(value);

        private static byte[] CreateSecurityDescriptor()
        {
#pragma warning disable CA1416
            var descriptor = new RawSecurityDescriptor("O:SYG:SYD:P(A;;0x1200a9;;;WD)");
            var bytes = new byte[descriptor.BinaryLength];
            descriptor.GetBinaryForm(bytes, 0);
            return bytes;
#pragma warning restore CA1416
        }

        private static void CopySecurityDescriptor(ref byte[] securityDescriptor)
        {
            if (securityDescriptor is null)
            {
                return;
            }

            if (securityDescriptor.Length < SecurityDescriptor.Length)
            {
                securityDescriptor = SecurityDescriptor.ToArray();
                return;
            }

            Array.Copy(SecurityDescriptor, securityDescriptor, SecurityDescriptor.Length);
        }

        private readonly record struct WinFspNode(
            string Name,
            string Path,
            DirectoryNode? Directory,
            DepotManifest.FileData? File)
        {
            public bool IsDirectory => Directory is not null;
        }

        private sealed record DirectoryEntryInfo(string Name, FileInfo FileInfo);

        private sealed class DirectoryEnumeration
        {
            private readonly IReadOnlyList<DirectoryEntryInfo> _entries;
            private int _index = -1;

            public DirectoryEnumeration(IEnumerable<DirectoryEntryInfo> entries)
            {
                _entries = entries.ToArray();
            }

            public bool TryMoveNext(out DirectoryEntryInfo entry)
            {
                _index++;
                if (_index >= _entries.Count)
                {
                    entry = default!;
                    return false;
                }

                entry = _entries[_index];
                return true;
            }
        }
    }
}
#endif
