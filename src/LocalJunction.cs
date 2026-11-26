using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace DiskLens;

// Preserve ordinary, absolute local directory junctions as link entries. Never open their targets.
internal sealed record LocalJunction(string Relative, PathSafety.FileIdentity Identity, PathSafety.FileIdentity StagedIdentity, byte[] Data)
{
    private const uint GetReparsePoint = 0x000900a8, SetReparsePoint = 0x000900a4, MountPointTag = 0xa0000003;
    private const uint GenericWrite = 0x40000000;

    public static LocalJunction Copy(string relative, string source, string staged, string sourceRoot, string outputRoot)
    {
        var sourcePath = Path.Join(source, relative);
        using var original = PathSafety.OpenMetadataNoFollow(sourcePath, share: 3);
        var (identity, data) = Read(original);
        var target = SubstituteName(data);
        if (PathSafety.Within(target, sourceRoot) || PathSafety.Within(target, outputRoot))
            throw new IOException($"联接点指向移动目录内部，跨盘移动可能改变链接含义，已保留原目录：{relative}");
        var destination = Path.Join(staged, relative);
        CrossVolumeMover.CreateNewDirectory(destination);
        using var copied = PathSafety.OpenMetadataNoFollow(destination, share: 3, extraAccess: GenericWrite);
        var before = PathSafety.Identity(copied, PathSafety.OrdinaryInfo(copied, directory: true));
        if (!DeviceIoControl(copied, SetReparsePoint, data, data.Length, null, 0, out _, IntPtr.Zero)) throw new Win32Exception();
        var after = Read(copied);
        if (before != after.Identity || !data.AsSpan().SequenceEqual(after.Data))
            throw new IOException($"联接点复制校验失败：{relative}");
        return new(relative, identity, before, data);
    }

    public void RemoveOriginal(string source, string output)
    {
        var from = Path.Join(source, Relative);
        var to = Path.Join(output, Relative);
        PathSafety.RequireOrdinaryPath(Path.GetDirectoryName(from)!);
        PathSafety.RequireOrdinaryPath(Path.GetDirectoryName(to)!);
        using var copied = PathSafety.OpenMetadataNoFollow(to, share: 1);
        using var original = PathSafety.OpenMetadataNoFollow(from, delete: true);
        var a = Read(copied);
        var b = Read(original);
        if (a.Identity != StagedIdentity || b.Identity != Identity ||
            !Data.AsSpan().SequenceEqual(a.Data) || !Data.AsSpan().SequenceEqual(b.Data))
            throw new IOException("源或目标联接点已变更，原链接已保留。");
        PathSafety.RemoveByHandle(original);
    }

    private static (PathSafety.FileIdentity Identity, byte[] Data) Read(SafeFileHandle handle)
    {
        if (!PathSafety.GetFileInformationByHandle(handle, out var info)) throw new Win32Exception();
        var attrs = (FileAttributes)info.Attributes;
        if ((attrs & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != (FileAttributes.Directory | FileAttributes.ReparsePoint) ||
            (attrs & (FileAttributes.Offline | (FileAttributes)0x40000 | (FileAttributes)0x400000)) != 0)
            throw new IOException("不是可保留的本地目录联接点。");
        var data = new byte[16 * 1024];
        if (!DeviceIoControl(handle, GetReparsePoint, null, 0, data, data.Length, out var length, IntPtr.Zero)) throw new Win32Exception();
        if (length < 16 || BinaryPrimitives.ReadUInt32LittleEndian(data) != MountPointTag ||
            BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(4)) != length - 8)
            throw new IOException("跨盘移动仅支持普通本地目录联接点；不支持符号链接或其他重解析点。");
        return (PathSafety.Identity(handle, info), data[..length]);
    }

    private static string SubstituteName(byte[] data)
    {
        var offset = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(8));
        var size = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(10));
        if ((offset | size) % 2 != 0 || 16 + offset + size > data.Length) throw new IOException("联接点格式不受支持。");
        var name = Encoding.Unicode.GetString(data, 16 + offset, size);
        if (name.Length < 7 || !name.StartsWith(@"\??\", StringComparison.Ordinal) ||
            !char.IsAsciiLetter(name[4]) || name[5] != ':' || name[6] != '\\' || name.Contains('\0'))
            throw new IOException("不支持跨盘移动卷挂载点或非本地联接点。");
        return PathSafety.Normalize(name[4..]);
    }

    internal static void CreateFixture(string path, string target) => Create(path, target);

    // Create only the junction entry, without traversing its target or depending on a shell.
    internal static void Create(string path, string target)
    {
        var substitute = Encoding.Unicode.GetBytes(@"\??\" + target);
        var print = Encoding.Unicode.GetBytes(target);
        var data = new byte[20 + substitute.Length + print.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(data, MountPointTag);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(4), checked((ushort)(data.Length - 8)));
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(10), checked((ushort)substitute.Length));
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(12), checked((ushort)(substitute.Length + 2)));
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(14), checked((ushort)print.Length));
        substitute.CopyTo(data.AsSpan(16));
        print.CopyTo(data.AsSpan(18 + substitute.Length));
        CrossVolumeMover.CreateNewDirectory(path);
        using var handle = PathSafety.OpenMetadataNoFollow(path, share: 3, extraAccess: GenericWrite);
        if (!DeviceIoControl(handle, SetReparsePoint, data, data.Length, null, 0, out _, IntPtr.Zero)) throw new Win32Exception();
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle handle, uint control, byte[]? input, int inputSize,
        [Out] byte[]? output, int outputSize, out int returned, IntPtr overlapped);
}
