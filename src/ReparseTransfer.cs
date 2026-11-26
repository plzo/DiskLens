using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace DiskLens;

internal static class ReparseTransfer
{
    // Only local junctions and symbolic links. No traversal, cloud recalls, or arbitrary reparse tags.
    public static void Move(string source, string output, string scratch, IReadOnlyList<PreparedFileSource> mappings)
    {
        PathSafety.RequireOrdinaryPath(Path.GetDirectoryName(source)!);
        PathSafety.RequireOrdinaryPath(Path.GetDirectoryName(output)!);
        using var original = PathSafety.OpenMetadataNoFollow(source, delete: true, share: 3);
        var before = Read(original);
        var directory = ((FileAttributes)before.Info.Attributes & FileAttributes.Directory) != 0;
        var text = directory ? new DirectoryInfo(source).LinkTarget : new FileInfo(source).LinkTarget;
        if (text is null) throw new IOException("无法读取链接本身，未跟随或删除。");
        if (text.StartsWith(@"\??\", StringComparison.Ordinal)) text = text[4..];
        if (Path.IsPathFullyQualified(text))
        {
            text = PathSafety.Normalize(text);
            foreach (var mapping in mappings)
                if (PathSafety.Within(text, mapping.Path))
                { text = Path.Join(mapping.Output!, Path.GetRelativePath(mapping.Path, text)); break; }
        }
        if (before.Tag == 0xa0000003)
        {
            if (!directory || !Path.IsPathFullyQualified(text) || text.StartsWith(@"\\", StringComparison.Ordinal))
                throw new IOException("不移动卷挂载点或非本地联接点。");
            LocalJunction.Create(scratch, text);
        }
        else if (directory) Directory.CreateSymbolicLink(scratch, text);
        else File.CreateSymbolicLink(scratch, text);
        using var copied = PathSafety.OpenMetadataNoFollow(scratch, delete: true);
        var expected = Read(copied);
        if (!before.Data.AsSpan().SequenceEqual(Read(original).Data)) throw new IOException("链接已变化，原链接保留。");
        if (PathSafety.Exists(output)) output = CrossVolumeMover.KeepBothName(output);
        PathSafety.RenameByHandle(copied, output);
        var published = Read(copied);
        if (PathSafety.Identity(copied, published.Info) != PathSafety.Identity(copied, expected.Info) ||
            !expected.Data.AsSpan().SequenceEqual(published.Data))
            throw new IOException("发布后的链接校验失败，原链接保留。");
        // The source handle still denotes the original link and never its target.
        PathSafety.RemoveByHandle(original);
    }

    private static (uint Tag, byte[] Data, PathSafety.HandleInfo Info) Read(SafeFileHandle handle)
    {
        if (!PathSafety.GetFileInformationByHandle(handle, out var info)) throw new Win32Exception();
        var attrs = (FileAttributes)info.Attributes;
        if ((attrs & FileAttributes.ReparsePoint) == 0 ||
            (attrs & (FileAttributes.Offline | (FileAttributes)0x40000 | (FileAttributes)0x400000)) != 0)
            throw new IOException("链接类型不安全，原项保留。");
        var data = new byte[16 * 1024];
        if (!DeviceIoControl(handle, 0x000900a8, IntPtr.Zero, 0, data, data.Length, out var count, IntPtr.Zero)) throw new Win32Exception();
        var tag = BinaryPrimitives.ReadUInt32LittleEndian(data);
        if (count < 16 || tag is not (0xa0000003 or 0xa000000c)) throw new IOException("不支持此重解析点类型。");
        return (tag, data[..count], info);
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle handle, uint control, IntPtr input, int inputSize,
        [Out] byte[] output, int outputSize, out int returned, IntPtr overlapped);
}
