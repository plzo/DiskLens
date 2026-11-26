using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace DiskLens;

internal static class PathSafety
{
    private static readonly string[] ProtectedRoots =
    [Environment.GetFolderPath(Environment.SpecialFolder.Windows),
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        Path.Join(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DiskLens"),
        AppContext.BaseDirectory];
    private static readonly string UserProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    private static readonly HashSet<string> ProtectedVolumeEntries = new(StringComparer.OrdinalIgnoreCase)
    { "$Recycle.Bin", "System Volume Information", "Recovery", "Boot", "EFI", "bootmgr", "pagefile.sys", "hiberfil.sys", "swapfile.sys", "DumpStack.log.tmp" };

    public static bool IsSpecial(FileAttributes attrs) =>
        (attrs & (FileAttributes.ReparsePoint | FileAttributes.Offline | (FileAttributes)0x40000 | (FileAttributes)0x400000)) != 0;

    public static string Normalize(string path)
    {
        if (!Path.IsPathFullyQualified(path) || path.StartsWith(@"\\?\", StringComparison.Ordinal) ||
            path.StartsWith(@"\\.\", StringComparison.Ordinal) || path.AsSpan(2).Contains(':'))
            throw new IOException("不支持相对路径、设备路径或备用数据流。");
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    }

    public static void RequireOrdinaryPath(string full, bool allowMissingLeaf = false)
    {
        var parts = new Stack<string>();
        for (string? p = Normalize(full); p is not null; p = Path.GetDirectoryName(p)) parts.Push(p);
        while (parts.TryPop(out var p))
        {
            FileAttributes attrs;
            try { attrs = File.GetAttributes(p); }
            catch (FileNotFoundException) when (allowMissingLeaf && parts.Count == 0) { return; }
            catch (DirectoryNotFoundException) when (allowMissingLeaf && parts.Count == 0) { return; }
            if (IsSpecial(attrs)) throw new IOException("路径包含链接、挂载点或离线项，不会继续操作。");
        }
    }

    public static bool Within(string path, string root)
    {
        if (string.IsNullOrWhiteSpace(root)) return false;
        root = Path.TrimEndingDirectorySeparator(root);
        return path.StartsWith(root, StringComparison.OrdinalIgnoreCase) &&
            (path.Length == root.Length || Path.EndsInDirectorySeparator(root) || PathInternalSeparator(path[root.Length]));
    }

    private static bool PathInternalSeparator(char value) => value is '\\' or '/';

    public static void RequireUnprotected(string path, bool source)
    {
        var volumeRoot = Path.GetPathRoot(path)!;
        if (source && string.Equals(path, volumeRoot, StringComparison.OrdinalIgnoreCase))
            throw new IOException("不能移动或删除磁盘根目录。");
        var relative = path.AsSpan(volumeRoot.Length);
        var separator = relative.IndexOf(Path.DirectorySeparatorChar);
        var first = separator < 0 ? relative : relative[..separator];
        if (relative.ToString().Split(Path.DirectorySeparatorChar).Any(CrossVolumeMover.IsStageName) ||
            ProtectedVolumeEntries.Contains(first.ToString()) ||
            ProtectedRoots.Any(root => Within(path, root) || (source && root.Length != 0 && Within(Path.TrimEndingDirectorySeparator(root), path))) ||
            (source && UserProfile.Length != 0 && Within(UserProfile, path)))
            throw new IOException("这是受保护的系统、程序、用户主目录或盘镜位置，拒绝操作。");
    }

    internal readonly record struct DirectoryStamp(FileIdentity Identity, FileAttributes Attributes, long ModifiedTicks);

    internal static DirectoryStamp? TryDirectoryStamp(string path)
    {
        try
        {
            RequireOrdinaryPath(path);
            using var handle = Open(path, 1 /* FILE_LIST_DIRECTORY */, 7, 0x02000000 | 0x00200000);
            if (!GetFileInformationByHandle(handle, out var info)) return null;
            var attrs = (FileAttributes)info.Attributes;
            if ((attrs & FileAttributes.Directory) == 0 || IsSpecial(attrs)) return null;
            return new(Identity(handle, info), attrs, info.LastWriteTicks);
        }
        catch (Exception error) when (DiskScanner.IsReadError(error) || error is Win32Exception) { return null; }
    }

    public static string CanonicalLocal(string path, out HandleInfo info, out FileIdentity identity)
    {
        RequireOrdinaryPath(path);
        using var handle = Open(path, access: 0, share: 7, flags: 0x02000000 | 0x00200000);
        if (!GetFileInformationByHandle(handle, out info)) throw new Win32Exception();
        if (IsSpecial((FileAttributes)info.Attributes)) throw new IOException("路径在操作前变成了链接或离线项。");
        var buffer = new StringBuilder(512);
        var size = GetFinalPathNameByHandle(handle, buffer, (uint)buffer.Capacity, 0);
        if (size == 0) throw new Win32Exception();
        if (size >= buffer.Capacity)
        {
            buffer.EnsureCapacity(checked((int)size + 1));
            size = GetFinalPathNameByHandle(handle, buffer, (uint)buffer.Capacity, 0);
            if (size == 0 || size >= buffer.Capacity) throw new Win32Exception();
        }
        var resolved = buffer.ToString();
        if (!resolved.StartsWith(@"\\?\", StringComparison.Ordinal) || resolved.Length < 7 || resolved[5] != ':')
            throw new IOException("手动清理仅支持本机盘符，不支持网络或设备路径。");
        var canonical = Normalize(resolved[4..]);
        RequireOrdinaryPath(canonical);
        identity = Identity(handle, info);
        return canonical;
    }

    public static bool Exists(string path)
    {
        try { _ = File.GetAttributes(path); return true; }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
    }

    public static SafeFileHandle OpenReadNoFollow(string path) => Open(path, 0x80000000, 1, 0x00200000 | 0x08000000);

    internal static SafeFileHandle OpenMetadataNoFollow(string path, bool delete = false, uint share = 1, uint extraAccess = 0) =>
        Open(path, 0x80 | (delete ? 0x10000u : 0) | extraAccess, share, 0x02000000 | 0x00200000);

    internal static HandleInfo OrdinaryInfo(SafeFileHandle handle, bool directory)
    {
        if (!GetFileInformationByHandle(handle, out var info)) throw new Win32Exception();
        if (IsSpecial((FileAttributes)info.Attributes) || (((FileAttributes)info.Attributes & FileAttributes.Directory) != 0) != directory)
            throw new IOException("文件或目录类型已改变，或已变成链接或离线项。");
        return info;
    }

    // Each worker can reuse a checked directory handle for many adjacent small files.
    // Denying delete sharing prevents the directory being renamed/replaced during the batch.
    internal sealed class DirectoryGuard : IDisposable
    {
        private SafeFileHandle? _handle;
        private string? _path;
        private FileIdentity _identity;

        public void Use(string path, FileIdentity identity)
        {
            if (path == _path && identity == _identity) return;
            Dispose();
            RequireOrdinaryPath(path);
            var handle = OpenMetadataNoFollow(path, share: 3);
            try
            {
                if (Identity(handle, OrdinaryInfo(handle, directory: true)) != identity)
                    throw new IOException("操作期间目录已改变，未继续。");
                _handle = handle;
                _identity = identity;
                _path = path;
            }
            catch { handle.Dispose(); throw; }
        }

        public void Dispose()
        {
            _handle?.Dispose();
            _handle = null;
            _path = null;
        }
    }

    internal static void RemoveByHandle(SafeFileHandle handle)
    {
        uint flags = 0x01 | 0x10; // FILE_DISPOSITION_FLAG_DELETE | IGNORE_READONLY_ATTRIBUTE
        if (SetDispositionEx(handle, 21, ref flags, sizeof(uint))) return;
        var error = Marshal.GetLastWin32Error();
        if (error is not (1 or 50 or 87)) throw new Win32Exception(error); // older filesystem: ordinary disposition
        byte delete = 1;
        if (!SetDisposition(handle, 4, ref delete, sizeof(byte))) throw new Win32Exception();
    }

    internal static void RenameByHandle(SafeFileHandle handle, string target)
    {
        // FILE_RENAME_INFO, with no replacement and an absolute DOS path.
        var name = Encoding.Unicode.GetBytes(@"\\?\" + target + '\0');
        var rootOffset = IntPtr.Size;
        var lengthOffset = rootOffset + IntPtr.Size;
        var nameOffset = lengthOffset + sizeof(uint);
        var length = nameOffset + name.Length;
        var buffer = Marshal.AllocHGlobal(length);
        try
        {
            Marshal.WriteInt32(buffer, 0, 0);
            Marshal.WriteIntPtr(buffer, rootOffset, IntPtr.Zero);
            Marshal.WriteInt32(buffer, lengthOffset, name.Length - sizeof(char));
            Marshal.Copy(name, 0, buffer + nameOffset, name.Length);
            if (!SetRename(handle, 3, buffer, (uint)length)) throw new Win32Exception();
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private static SafeFileHandle Open(string path, uint access, uint share, uint flags)
    {
        var extended = path.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + path[2..] : @"\\?\" + path;
        var handle = CreateFile(extended, access, share, IntPtr.Zero, 3, flags, IntPtr.Zero);
        if (!handle.IsInvalid) return handle;
        var error = Marshal.GetLastWin32Error();
        handle.Dispose();
        throw new Win32Exception(error);
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct HandleInfo
    {
        public uint Attributes;
        public uint CreationLo, CreationHi, AccessLo, AccessHi, WriteLo, WriteHi;
        public uint Volume, SizeHi, SizeLo, Links, IndexHi, IndexLo;
        public readonly long Length => ((long)SizeHi << 32) | SizeLo;
        public readonly long CreationTicks => DateTime.FromFileTimeUtc(((long)CreationHi << 32) | CreationLo).Ticks;
        public readonly long LastWriteTicks => DateTime.FromFileTimeUtc(((long)WriteHi << 32) | WriteLo).Ticks;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal readonly record struct FileIdentity(ulong Volume, ulong Low, ulong High);

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security, uint mode, uint flags, IntPtr template);
    [DllImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandle(SafeFileHandle handle, StringBuilder path, uint size, uint flags);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetFileInformationByHandle(SafeFileHandle handle, out HandleInfo info);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle handle, int infoClass, out FileIdentity info, uint size);

    [DllImport("kernel32.dll", EntryPoint = "SetFileInformationByHandle", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetDispositionEx(SafeFileHandle handle, int infoClass, ref uint flags, uint size);
    [DllImport("kernel32.dll", EntryPoint = "SetFileInformationByHandle", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetDisposition(SafeFileHandle handle, int infoClass, ref byte delete, uint size);

    [DllImport("kernel32.dll", EntryPoint = "SetFileInformationByHandle", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetRename(SafeFileHandle handle, int infoClass, IntPtr info, uint size);

    internal static FileIdentity Identity(SafeFileHandle handle, HandleInfo info)
    {
        if (GetFileInformationByHandleEx(handle, 18, out var identity, 24) && (identity.Low != 0 || identity.High != 0)) return identity;
        if (info.IndexLo == 0 && info.IndexHi == 0) throw new IOException("无法识别文件实体。");
        return new(info.Volume, ((ulong)info.IndexHi << 32) | info.IndexLo, 0);
    }
}
