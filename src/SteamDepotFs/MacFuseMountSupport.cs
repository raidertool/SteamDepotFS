#if MACFUSE_MOUNT
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using SteamKit2;

internal static unsafe class MacFuseMountSupport
{
    private const string FuseLibraryName = "fuse";
    private static int _resolverConfigured;

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

    private static class MacFuseNative
    {
        private static readonly string[] MacFuseLibraryCandidates =
        [
            "/usr/local/lib/libfuse.dylib",
            "/usr/local/lib/libfuse.2.dylib",
            "/opt/homebrew/lib/libfuse.dylib",
            "/opt/homebrew/lib/libfuse.2.dylib"
        ];

        public static void ConfigureResolver()
        {
            if (Interlocked.Exchange(ref _resolverConfigured, 1) != 0)
            {
                return;
            }

            NativeLibrary.SetDllImportResolver(typeof(MacFuseMountSupport).Assembly, Resolve);
        }

        public static bool TryLoad([NotNullWhen(false)] out string? error)
        {
            foreach (var candidate in MacFuseLibraryCandidates)
            {
                if (!NativeLibrary.TryLoad(candidate, out var handle))
                {
                    continue;
                }

                NativeLibrary.Free(handle);
                error = null;
                return true;
            }

            error = "Unable to load libfuse.dylib from /usr/local/lib or /opt/homebrew/lib.";
            return false;
        }

        private static IntPtr Resolve(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
        {
            if (libraryName != FuseLibraryName)
            {
                return IntPtr.Zero;
            }

            foreach (var candidate in MacFuseLibraryCandidates)
            {
                if (NativeLibrary.TryLoad(candidate, out var handle))
                {
                    return handle;
                }
            }

            return IntPtr.Zero;
        }
    }

    [DllImport(FuseLibraryName, EntryPoint = "fuse_main_real", CallingConvention = CallingConvention.Cdecl)]
    private static extern int FuseMainReal(
        int argc,
        IntPtr argv,
        MacFuseOperations* operations,
        UIntPtr operationSize,
        IntPtr userData);

    [StructLayout(LayoutKind.Sequential, Size = 464)]
    private struct MacFuseOperations
    {
        public IntPtr GetAttr;
        public IntPtr ReadLink;
        public IntPtr GetDir;
        public IntPtr MkNod;
        public IntPtr MkDir;
        public IntPtr Unlink;
        public IntPtr RmDir;
        public IntPtr SymLink;
        public IntPtr Rename;
        public IntPtr Link;
        public IntPtr Chmod;
        public IntPtr Chown;
        public IntPtr Truncate;
        public IntPtr Utime;
        public IntPtr Open;
        public IntPtr Read;
        public IntPtr Write;
        public IntPtr StatFs;
        public IntPtr Flush;
        public IntPtr Release;
        public IntPtr Fsync;
        public IntPtr SetXAttr;
        public IntPtr GetXAttr;
        public IntPtr ListXAttr;
        public IntPtr RemoveXAttr;
        public IntPtr OpenDir;
        public IntPtr ReadDir;
        public IntPtr ReleaseDir;
        public IntPtr FsyncDir;
        public IntPtr Init;
        public IntPtr Destroy;
        public IntPtr Access;
        public IntPtr Create;
        public IntPtr FTruncate;
        public IntPtr FGetAttr;
        public IntPtr Lock;
        public IntPtr UTimens;
        public IntPtr Bmap;
        private readonly uint _flags;
        private readonly uint _paddingAfterFlags;
        public IntPtr Ioctl;
        public IntPtr Poll;
        public IntPtr WriteBuf;
        public IntPtr ReadBuf;
        public IntPtr Flock;
        public IntPtr FAllocate;
        public IntPtr Reserved00;
        public IntPtr Reserved01;
        public IntPtr RenameX;
        public IntPtr StatFsX;
        public IntPtr SetVolName;
        public IntPtr Exchange;
        public IntPtr GetXTimes;
        public IntPtr SetBackupTime;
        public IntPtr SetChangeTime;
        public IntPtr SetCreateTime;
        public IntPtr ChFlags;
        public IntPtr SetAttrX;
        public IntPtr FSetAttrX;
    }

    [StructLayout(LayoutKind.Sequential, Size = 144)]
    private struct MacStat
    {
        public int Device;
        public ushort Mode;
        public ushort LinkCount;
        public ulong Inode;
        public uint UserId;
        public uint GroupId;
        public int RawDevice;
        private readonly int _padding0;
        public MacTimespec AccessTime;
        public MacTimespec ModifyTime;
        public MacTimespec ChangeTime;
        public MacTimespec BirthTime;
        public long Size;
        public long Blocks;
        public int BlockSize;
        public uint Flags;
        public uint Generation;
        private readonly int _spare0;
        private readonly long _spare1;
        private readonly long _spare2;
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct MacTimespec(long seconds)
    {
        public readonly long Seconds = seconds;
        public readonly long Nanoseconds = 0;
    }

    [StructLayout(LayoutKind.Sequential, Size = 64)]
    private struct MacStatVfs
    {
        public ulong BlockSize;
        public ulong FragmentSize;
        public uint Blocks;
        public uint FreeBlocks;
        public uint AvailableBlocks;
        public uint FileCount;
        public uint FreeFiles;
        public uint AvailableFiles;
        public ulong FileSystemId;
        public ulong Flags;
        public ulong NameMax;
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

    private sealed class MacFuseFileSystem : IDisposable
    {
        private const int ErrnoNoEntry = 2;
        private const int ErrnoIo = 5;
        private const int ErrnoIsDirectory = 21;
        private const int ErrnoInvalidArgument = 22;
        private const int ErrnoReadOnlyFileSystem = 30;

        private const int OpenAccessMode = 0x0003;
        private const int OpenReadOnly = 0;
        private const int AccessWrite = 0x02;

        private const ushort DirectoryMode =
            0x4000 |
            0x0100 | 0x0040 |
            0x0020 | 0x0008 |
            0x0004 | 0x0001;

        private const ushort FileMode =
            0x8000 |
            0x0100 |
            0x0020 |
            0x0004;

        private const ushort ExecutableFileMode =
            FileMode |
            0x0040 |
            0x0008 |
            0x0001;

        private const ushort SymlinkMode =
            0xa000 |
            0x0100 |
            0x0020 |
            0x0004;

        private static readonly object MountLock = new();
        private static MacFuseFileSystem? Current;

        private readonly DepotReader _reader;
        private readonly long _now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        private readonly bool _allowWriteModeOpens;
        private bool _disposed;

        public MacFuseFileSystem(string mountPoint, DepotReader reader)
        {
            MountPoint = mountPoint;
            _reader = reader;
            _allowWriteModeOpens = MacFuseRuntime.ShouldUseFSKitBackend(mountPoint);
        }

        public string MountPoint { get; }

        public void Start()
        {
            var operations = CreateOperations();
            var args = BuildArguments();
            var argv = AllocateArgv(args);
            try
            {
                lock (MountLock)
                {
                    if (Current is not null)
                    {
                        throw new InvalidOperationException("Only one macFUSE mount can run in a SteamDepotFS process.");
                    }

                    Current = this;
                }

                LogDebug("macFUSE arguments: " + string.Join(' ', args));
                var result = FuseMainReal(
                    args.Count,
                    argv,
                    &operations,
                    (UIntPtr)Marshal.SizeOf<MacFuseOperations>(),
                    IntPtr.Zero);

                if (result != 0)
                {
                    throw new InvalidOperationException($"macFUSE mount failed for {MountPoint}: exit code {result}");
                }
            }
            finally
            {
                lock (MountLock)
                {
                    if (ReferenceEquals(Current, this))
                    {
                        Current = null;
                    }
                }

                FreeArgv(argv, args.Count);
            }
        }

        public void Dispose()
        {
            _disposed = true;
        }

        private List<string> BuildArguments()
        {
            var args = new List<string>
            {
                "SteamDepotFS",
                "-f",
                "-oro",
                "-ofsname=SteamDepotFS",
                "-ovolname=SteamDepotFS",
                "-osubtype=steam-depotfs"
            };

            if (MacFuseRuntime.ShouldUseFSKitBackend(MountPoint))
            {
                args.Add("-obackend=fskit");
            }

            if (Environment.GetEnvironmentVariable("STEAM_DEPOTFS_FUSE_DEBUG") == "1")
            {
                args.Add("-d");
            }

            args.Add(MountPoint);
            return args;
        }

        private static MacFuseOperations CreateOperations()
            => new()
            {
                GetAttr = (IntPtr)(delegate* unmanaged[Cdecl]<byte*, MacStat*, int>)&GetAttr,
                ReadLink = (IntPtr)(delegate* unmanaged[Cdecl]<byte*, byte*, UIntPtr, int>)&ReadLink,
                Open = (IntPtr)(delegate* unmanaged[Cdecl]<byte*, IntPtr, int>)&Open,
                Read = (IntPtr)(delegate* unmanaged[Cdecl]<byte*, byte*, UIntPtr, long, IntPtr, int>)&Read,
                StatFs = (IntPtr)(delegate* unmanaged[Cdecl]<byte*, MacStatVfs*, int>)&StatFs,
                OpenDir = (IntPtr)(delegate* unmanaged[Cdecl]<byte*, IntPtr, int>)&OpenDir,
                ReadDir = (IntPtr)(delegate* unmanaged[Cdecl]<byte*, IntPtr, IntPtr, long, IntPtr, int>)&ReadDir,
                ReleaseDir = (IntPtr)(delegate* unmanaged[Cdecl]<byte*, IntPtr, int>)&ReleaseDir,
                Access = (IntPtr)(delegate* unmanaged[Cdecl]<byte*, int, int>)&Access,
                Write = (IntPtr)(delegate* unmanaged[Cdecl]<byte*, byte*, UIntPtr, long, IntPtr, int>)&ReadOnlyWrite,
                MkNod = (IntPtr)(delegate* unmanaged[Cdecl]<byte*, ushort, int, int>)&ReadOnlyCreateSpecialFile,
                MkDir = (IntPtr)(delegate* unmanaged[Cdecl]<byte*, ushort, int>)&ReadOnlyCreateDirectory,
                Unlink = (IntPtr)(delegate* unmanaged[Cdecl]<byte*, int>)&ReadOnlyPath,
                RmDir = (IntPtr)(delegate* unmanaged[Cdecl]<byte*, int>)&ReadOnlyPath,
                SymLink = (IntPtr)(delegate* unmanaged[Cdecl]<byte*, byte*, int>)&ReadOnlyTwoPaths,
                Rename = (IntPtr)(delegate* unmanaged[Cdecl]<byte*, byte*, int>)&ReadOnlyTwoPaths,
                Link = (IntPtr)(delegate* unmanaged[Cdecl]<byte*, byte*, int>)&ReadOnlyTwoPaths,
                Chmod = (IntPtr)(delegate* unmanaged[Cdecl]<byte*, ushort, int>)&ReadOnlyChangeMode,
                Chown = (IntPtr)(delegate* unmanaged[Cdecl]<byte*, uint, uint, int>)&ReadOnlyChangeOwner,
                Truncate = (IntPtr)(delegate* unmanaged[Cdecl]<byte*, long, int>)&ReadOnlyTruncate,
                Create = (IntPtr)(delegate* unmanaged[Cdecl]<byte*, ushort, IntPtr, int>)&ReadOnlyCreate,
                FTruncate = (IntPtr)(delegate* unmanaged[Cdecl]<byte*, long, IntPtr, int>)&ReadOnlyFTruncate,
                SetXAttr = (IntPtr)(delegate* unmanaged[Cdecl]<byte*, byte*, byte*, UIntPtr, int, uint, int>)&ReadOnlySetXAttr,
                RemoveXAttr = (IntPtr)(delegate* unmanaged[Cdecl]<byte*, byte*, int>)&ReadOnlyRemoveXAttr,
                SetVolName = (IntPtr)(delegate* unmanaged[Cdecl]<byte*, int>)&ReadOnlySetVolName,
                RenameX = (IntPtr)(delegate* unmanaged[Cdecl]<byte*, byte*, uint, int>)&ReadOnlyRenameX,
                ChFlags = (IntPtr)(delegate* unmanaged[Cdecl]<byte*, uint, int>)&ReadOnlyChFlags
            };

        private static IntPtr AllocateArgv(IReadOnlyList<string> args)
        {
            var argv = Marshal.AllocHGlobal((args.Count + 1) * IntPtr.Size);
            for (var i = 0; i < args.Count; i++)
            {
                Marshal.WriteIntPtr(argv, i * IntPtr.Size, StringToHGlobalUtf8(args[i]));
            }

            Marshal.WriteIntPtr(argv, args.Count * IntPtr.Size, IntPtr.Zero);
            return argv;
        }

        private static void FreeArgv(IntPtr argv, int count)
        {
            if (argv == IntPtr.Zero)
            {
                return;
            }

            for (var i = 0; i < count; i++)
            {
                var arg = Marshal.ReadIntPtr(argv, i * IntPtr.Size);
                if (arg != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(arg);
                }
            }

            Marshal.FreeHGlobal(argv);
        }

        private static IntPtr StringToHGlobalUtf8(string value)
        {
            var bytes = Encoding.UTF8.GetBytes(value);
            var ptr = Marshal.AllocHGlobal(bytes.Length + 1);
            Marshal.Copy(bytes, 0, ptr, bytes.Length);
            Marshal.WriteByte(ptr, bytes.Length, 0);
            return ptr;
        }

        private static MacFuseFileSystem? TryGetCurrent()
        {
            lock (MountLock)
            {
                return Current is { _disposed: false } current ? current : null;
            }
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
        private static int GetAttr(byte* path, MacStat* stat)
        {
            try
            {
                var current = TryGetCurrent();
                if (current is null)
                {
                    return -ErrnoIo;
                }

                var managedPath = PathFromNative(path);
                LogDebug($"macFUSE getattr {managedPath}");
                return current.GetAttr(managedPath, stat);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"macFUSE getattr failed: {ex.Message}");
                return -ErrnoIo;
            }
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
        private static int ReadLink(byte* path, byte* buffer, UIntPtr size)
        {
            try
            {
                if (size.ToUInt64() > int.MaxValue)
                {
                    return -ErrnoInvalidArgument;
                }

                var current = TryGetCurrent();
                if (current is null)
                {
                    return -ErrnoIo;
                }

                var managedPath = PathFromNative(path);
                LogDebug($"macFUSE readlink {managedPath}");
                return current.ReadLink(managedPath, buffer, (int)size);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"macFUSE readlink failed: {ex.Message}");
                return -ErrnoIo;
            }
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
        private static int Open(byte* path, IntPtr fileInfo)
        {
            try
            {
                var current = TryGetCurrent();
                if (current is null)
                {
                    return -ErrnoIo;
                }

                var managedPath = PathFromNative(path);
                LogDebug($"macFUSE open {managedPath}");
                return current.Open(managedPath, fileInfo);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"macFUSE open failed: {ex.Message}");
                return -ErrnoIo;
            }
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
        private static int Read(byte* path, byte* buffer, UIntPtr size, long offset, IntPtr fileInfo)
        {
            try
            {
                if (size.ToUInt64() > int.MaxValue)
                {
                    return -ErrnoInvalidArgument;
                }

                var current = TryGetCurrent();
                if (current is null)
                {
                    return -ErrnoIo;
                }

                var managedPath = PathFromNative(path);
                LogDebug($"macFUSE read {managedPath} size={size} offset={offset}");
                return current.Read(managedPath, buffer, (int)size, offset);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"macFUSE read failed: {ex.Message}");
                return -ErrnoIo;
            }
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
        private static int StatFs(byte* path, MacStatVfs* stat)
        {
            try
            {
                var current = TryGetCurrent();
                if (current is null)
                {
                    return -ErrnoIo;
                }

                LogDebug("macFUSE statfs");
                return current.StatFs(stat);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"macFUSE statfs failed: {ex.Message}");
                return -ErrnoIo;
            }
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
        private static int OpenDir(byte* path, IntPtr fileInfo)
        {
            try
            {
                var current = TryGetCurrent();
                if (current is null)
                {
                    return -ErrnoIo;
                }

                var managedPath = PathFromNative(path);
                LogDebug($"macFUSE opendir {managedPath}");
                return current.OpenDir(managedPath);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"macFUSE opendir failed: {ex.Message}");
                return -ErrnoIo;
            }
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
        private static int ReadDir(byte* path, IntPtr buffer, IntPtr filler, long offset, IntPtr fileInfo)
        {
            try
            {
                var current = TryGetCurrent();
                if (current is null)
                {
                    return -ErrnoIo;
                }

                var managedPath = PathFromNative(path);
                LogDebug($"macFUSE readdir {managedPath}");
                return current.ReadDir(managedPath, buffer, filler);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"macFUSE readdir failed: {ex.Message}");
                return -ErrnoIo;
            }
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
        private static int ReleaseDir(byte* path, IntPtr fileInfo)
            => 0;

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
        private static int Access(byte* path, int mode)
        {
            try
            {
                var current = TryGetCurrent();
                if (current is null)
                {
                    return -ErrnoIo;
                }

                var managedPath = PathFromNative(path);
                LogDebug($"macFUSE access {managedPath} mode={mode}");
                return current.Access(managedPath, mode);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"macFUSE access failed: {ex.Message}");
                return -ErrnoIo;
            }
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
        private static int ReadOnlyPath(byte* path)
            => -ErrnoReadOnlyFileSystem;

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
        private static int ReadOnlyTwoPaths(byte* oldPath, byte* newPath)
            => -ErrnoReadOnlyFileSystem;

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
        private static int ReadOnlyWrite(byte* path, byte* buffer, UIntPtr size, long offset, IntPtr fileInfo)
            => -ErrnoReadOnlyFileSystem;

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
        private static int ReadOnlyCreateSpecialFile(byte* path, ushort mode, int device)
            => -ErrnoReadOnlyFileSystem;

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
        private static int ReadOnlyCreateDirectory(byte* path, ushort mode)
            => -ErrnoReadOnlyFileSystem;

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
        private static int ReadOnlyChangeMode(byte* path, ushort mode)
            => -ErrnoReadOnlyFileSystem;

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
        private static int ReadOnlyChangeOwner(byte* path, uint owner, uint group)
            => -ErrnoReadOnlyFileSystem;

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
        private static int ReadOnlyTruncate(byte* path, long length)
            => -ErrnoReadOnlyFileSystem;

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
        private static int ReadOnlyCreate(byte* path, ushort mode, IntPtr fileInfo)
            => -ErrnoReadOnlyFileSystem;

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
        private static int ReadOnlyFTruncate(byte* path, long length, IntPtr fileInfo)
            => -ErrnoReadOnlyFileSystem;

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
        private static int ReadOnlySetXAttr(byte* path, byte* name, byte* value, UIntPtr size, int flags, uint position)
            => -ErrnoReadOnlyFileSystem;

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
        private static int ReadOnlyRemoveXAttr(byte* path, byte* name)
            => -ErrnoReadOnlyFileSystem;

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
        private static int ReadOnlySetVolName(byte* name)
            => -ErrnoReadOnlyFileSystem;

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
        private static int ReadOnlyRenameX(byte* oldPath, byte* newPath, uint flags)
            => -ErrnoReadOnlyFileSystem;

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
        private static int ReadOnlyChFlags(byte* path, uint flags)
            => -ErrnoReadOnlyFileSystem;

        private int GetAttr(string path, MacStat* stat)
        {
            if (_reader.Index.TryGetDirectory(path, out var directory))
            {
                *stat = StatForDirectory(directory);
                return 0;
            }

            if (_reader.Index.TryGetFile(path, out var file))
            {
                *stat = StatForFile(file);
                return 0;
            }

            return -ErrnoNoEntry;
        }

        private int ReadLink(string path, byte* buffer, int size)
        {
            if (!_reader.Index.TryGetFile(path, out var file))
            {
                return -ErrnoNoEntry;
            }

            if (!file.Flags.HasFlag(EDepotFileFlag.Symlink) || string.IsNullOrEmpty(file.LinkTarget))
            {
                return -ErrnoInvalidArgument;
            }

            var target = Encoding.UTF8.GetBytes(file.LinkTarget);
            if (size <= 0)
            {
                return -ErrnoInvalidArgument;
            }

            var length = Math.Min(target.Length, size - 1);
            Marshal.Copy(target, 0, (IntPtr)buffer, length);
            buffer[length] = 0;
            return 0;
        }

        private int Open(string path, IntPtr fileInfo)
        {
            if (!_reader.Index.TryGetFile(path, out _))
            {
                return _reader.Index.TryGetDirectory(path, out _) ? -ErrnoIsDirectory : -ErrnoNoEntry;
            }

            if (!_allowWriteModeOpens && fileInfo != IntPtr.Zero)
            {
                var flags = Marshal.ReadInt32(fileInfo);
                if ((flags & OpenAccessMode) != OpenReadOnly)
                {
                    return -ErrnoReadOnlyFileSystem;
                }
            }

            return 0;
        }

        private int Read(string path, byte* buffer, int size, long offset)
        {
            if (!_reader.Index.TryGetFile(path, out var file))
            {
                return -ErrnoNoEntry;
            }

            if (size < 0)
            {
                return -ErrnoInvalidArgument;
            }

            var managedBuffer = new byte[size];
            int read;
            if (file.Flags.HasFlag(EDepotFileFlag.Symlink) && file.LinkTarget is not null)
            {
                read = ReadLinkTarget(file.LinkTarget, offset, managedBuffer);
            }
            else
            {
                read = _reader.ReadAsync(file, offset, managedBuffer, CancellationToken.None)
                    .GetAwaiter()
                    .GetResult();
            }

            if (read > 0)
            {
                Marshal.Copy(managedBuffer, 0, (IntPtr)buffer, read);
            }

            return read;
        }

        private int StatFs(MacStatVfs* stat)
        {
            const ulong blockSize = 4096;
            var blocks = Math.Max(1, (_reader.Manifest.TotalUncompressedSize + blockSize - 1) / blockSize);
            *stat = new MacStatVfs
            {
                BlockSize = blockSize,
                FragmentSize = blockSize,
                Blocks = ClampToUInt32(blocks),
                FreeBlocks = 0,
                AvailableBlocks = 0,
                FileCount = ClampToUInt32((ulong)_reader.Index.AllFiles.Count + 1),
                FreeFiles = 0,
                AvailableFiles = 0,
                FileSystemId = 0,
                Flags = 1,
                NameMax = 255
            };
            return 0;
        }

        private int OpenDir(string path)
            => _reader.Index.TryGetDirectory(path, out _) ? 0 : -ErrnoNoEntry;

        private int ReadDir(string path, IntPtr buffer, IntPtr filler)
        {
            if (!_reader.Index.TryGetDirectory(path, out var node))
            {
                return -ErrnoNoEntry;
            }

            if (!FillDirectoryEntry(buffer, filler, ".", StatForDirectory(node)) ||
                !FillDirectoryEntry(buffer, filler, "..", StatForDirectory(node)))
            {
                return 0;
            }

            foreach (var directory in node.Directories.Values.OrderBy(static d => d.Name, StringComparer.Ordinal))
            {
                if (!FillDirectoryEntry(buffer, filler, directory.Name, StatForDirectory(directory)))
                {
                    return 0;
                }
            }

            foreach (var file in node.Files.OrderBy(static f => f.Key, StringComparer.Ordinal))
            {
                if (!FillDirectoryEntry(buffer, filler, file.Key, StatForFile(file.Value)))
                {
                    return 0;
                }
            }

            return 0;
        }

        private int Access(string path, int mode)
        {
            if (!_reader.Index.Exists(path))
            {
                return -ErrnoNoEntry;
            }

            return (mode & AccessWrite) != 0 ? -ErrnoReadOnlyFileSystem : 0;
        }

        private static bool FillDirectoryEntry(IntPtr buffer, IntPtr filler, string name, MacStat stat)
        {
            var fillerFunction = (delegate* unmanaged[Cdecl]<IntPtr, byte*, MacStat*, long, int>)filler;
            var nameBytes = Encoding.UTF8.GetBytes(name);
            if (nameBytes.Length > 255)
            {
                return false;
            }

            var terminatedName = new byte[nameBytes.Length + 1];
            Buffer.BlockCopy(nameBytes, 0, terminatedName, 0, nameBytes.Length);

            fixed (byte* namePtr = terminatedName)
            {
                return fillerFunction(buffer, namePtr, &stat, 0) == 0;
            }
        }

        private MacStat StatForDirectory(DirectoryNode directory)
            => new()
            {
                Mode = DirectoryMode,
                LinkCount = 2,
                Inode = StableInode(directory.Path),
                AccessTime = new MacTimespec(_now),
                ModifyTime = new MacTimespec(_now),
                ChangeTime = new MacTimespec(_now),
                BirthTime = new MacTimespec(_now),
                Size = 0,
                Blocks = 0,
                BlockSize = 4096
            };

        private MacStat StatForFile(DepotManifest.FileData file)
        {
            var mode = file.Flags.HasFlag(EDepotFileFlag.Symlink)
                ? SymlinkMode
                : file.Flags.HasFlag(EDepotFileFlag.Executable)
                    ? ExecutableFileMode
                    : FileMode;

            var size = file.Flags.HasFlag(EDepotFileFlag.Symlink) && file.LinkTarget is not null
                ? Encoding.UTF8.GetByteCount(file.LinkTarget)
                : checked((long)file.TotalSize);

            var manifestTime = new DateTimeOffset(_reader.Manifest.CreationTime.ToUniversalTime()).ToUnixTimeSeconds();
            return new MacStat
            {
                Mode = mode,
                LinkCount = 1,
                Inode = StableInode(file.FileName),
                AccessTime = new MacTimespec(_now),
                ModifyTime = new MacTimespec(manifestTime),
                ChangeTime = new MacTimespec(_now),
                BirthTime = new MacTimespec(manifestTime),
                Size = size,
                Blocks = (size + 511) / 512,
                BlockSize = 4096
            };
        }

        private static int ReadLinkTarget(string linkTarget, long offset, byte[] buffer)
        {
            var data = Encoding.UTF8.GetBytes(linkTarget);
            if (offset >= data.Length)
            {
                return 0;
            }

            var available = data.Length - checked((int)offset);
            var toCopy = Math.Min(buffer.Length, available);
            Buffer.BlockCopy(data, checked((int)offset), buffer, 0, toCopy);
            return toCopy;
        }

        private static string PathFromNative(byte* path)
            => Marshal.PtrToStringUTF8((IntPtr)path) ?? "/";

        private static void LogDebug(string message)
        {
            if (Environment.GetEnvironmentVariable("STEAM_DEPOTFS_FUSE_DEBUG") == "1")
            {
                Console.Error.WriteLine(message);
            }
        }

        private static ulong StableInode(string path)
        {
            var hash = SHA256.HashData(Encoding.UTF8.GetBytes(path));
            return BitConverter.ToUInt64(hash, 0) & 0x7fffffffffffffff;
        }

        private static uint ClampToUInt32(ulong value)
            => value > uint.MaxValue ? uint.MaxValue : (uint)value;
    }
}

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
