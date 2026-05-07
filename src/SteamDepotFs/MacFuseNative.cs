#if MACFUSE_MOUNT
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Runtime.InteropServices;

internal static unsafe partial class MacFuseMountSupport
{
    private const string FuseLibraryName = "fuse";
    private static int _resolverConfigured;

    [DllImport(FuseLibraryName, EntryPoint = "fuse_main_real", CallingConvention = CallingConvention.Cdecl)]
    private static extern int FuseMainReal(
        int argc,
        IntPtr argv,
        MacFuseOperations* operations,
        UIntPtr operationSize,
        IntPtr userData);

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
}
#endif
