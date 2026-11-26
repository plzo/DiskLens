using System.Collections.Concurrent;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;

namespace DiskLens;

// Cross-volume moves use ordinary single-file copies, never Explorer's move UI. Nothing at the
// source is removed until the complete item is copied and published without replacing a target.
internal sealed class CrossVolumeMover
{
    internal const string StagePrefix = ".DiskLens-move-";
    private const uint NamedStreams = 0x00040000;
    private const uint CopyFailIfExists = 0x00000001, CopySymlinkItself = 0x00000800;
    private const int HandleEof = 38;
    private readonly string _stage;
    private readonly ParallelOptions _parallel;
    private readonly PathSafety.FileIdentity _stageIdentity;
    private readonly bool _targetStreams;
    private readonly ConcurrentDictionary<string, bool> _sourceStreams = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, bool> VolumeStreams = new(StringComparer.OrdinalIgnoreCase);
    public string? CleanupError { get; private set; }

    private readonly record struct StableFile(PathSafety.FileIdentity Identity, long Length, long Write, long Change);
    private readonly record struct FolderCopy(string Relative, PathSafety.FileIdentity Identity, PathSafety.FileIdentity StagedIdentity, PathSafety.HandleInfo Info);
    private readonly record struct FileCopy(string Name, int Folder, long Length, long Ticks);
    private readonly record struct CopiedFile(StableFile Source, StableFile Target);

    private sealed class TreeWorker : IDisposable
    {
        private readonly PathSafety.DirectoryGuard _original = new(), _copied = new();
        private int _index = -1;
        public string Original { get; private set; } = "";
        public string Copied { get; private set; } = "";
        public void Use(FileCopy file, List<FolderCopy> folders, string source, string target)
        {
            if (_index == file.Folder) return;
            _index = -1;
            var folder = folders[file.Folder];
            Original = Path.Join(source, folder.Relative);
            Copied = Path.Join(target, folder.Relative);
            _original.Use(Original, folder.Identity);
            _copied.Use(Copied, folder.StagedIdentity);
            _index = file.Folder;
        }
        public void Dispose() { _original.Dispose(); _copied.Dispose(); }
    }

    public CrossVolumeMover(string stage, int workers)
    {
        _stage = stage;
        _targetStreams = SupportsNamedStreams(Path.GetPathRoot(stage)!);
        _parallel = new() { MaxDegreeOfParallelism = Math.Clamp(workers, 1, 8) };
        CreateNewDirectory(stage);
        using var handle = PathSafety.OpenMetadataNoFollow(stage, share: 3);
        _stageIdentity = PathSafety.Identity(handle, PathSafety.OrdinaryInfo(handle, directory: true));
    }

    internal static bool IsStageName(string name) => name.StartsWith(StagePrefix, StringComparison.OrdinalIgnoreCase) &&
        Guid.TryParseExact(name.AsSpan(StagePrefix.Length), "N", out _);

    public void Move(PreparedFileSource item, Action<string>? publish = null, FileTaskProgress.ItemProgress? progress = null)
    {
        var staged = Path.Join(_stage, item.Snapshot.Name);
        var source = item.Path;
        var output = item.Output!;
        publish ??= path => Rename(path, output);
        var srcStreams = _sourceStreams.GetOrAdd(Path.GetPathRoot(source)!, SupportsNamedStreams);
        if (!item.Snapshot.Directory)
        {
            progress?.Report("正在复制", 0);
            var copied = Copy(source, staged, new("", 0, item.Snapshot.Bytes, item.Snapshot.Ticks), srcStreams && !_targetStreams, item.Identity,
                transferred: progress is null ? null : bytes => progress.Report("正在复制",
                    .9 * bytes / Math.Max(1d, item.Snapshot.Bytes), bytes));
            progress?.Report("发布目标并移除原件", .92, item.Snapshot.Bytes);
            publish(staged);
            try { RemoveCopiedFile(source, output, copied); }
            catch (Exception error) when (FileActions.IsActionError(error))
            { throw new IOException("目标副本已保留，原文件未能移除：" + error.Message, error); }
            return;
        }

        progress?.Report("统计目录并准备复制");
        var (folders, files, links) = PrepareTree(item, staged, srcStreams);
        var copies = new CopiedFile[files.Count];
        var totalBytes = files.Sum(file => file.Length);
        long transferredBytes = 0;
        var copiedFiles = 0;
        void CopyProgress()
        {
            var bytes = Interlocked.Read(ref transferredBytes);
            var done = Volatile.Read(ref copiedFiles);
            var fraction = totalBytes == 0 ? done / Math.Max(1d, files.Count) :
                .8 * bytes / totalBytes + .2 * done / Math.Max(1d, files.Count);
            progress?.Report("正在并行复制", .02 + .88 * fraction, bytes);
        }
        progress?.Report("正在并行复制", .02);
        Exception? copyError = null;
        Parallel.For(0, files.Count, _parallel, () => new TreeWorker(), (i, loop, worker) =>
        {
            var file = files[i];
            try
            {
                worker.Use(file, folders, source, staged);
                long lastBytes = 0;
                void Transferred(long bytes)
                {
                    var current = Math.Clamp(bytes, 0, file.Length);
                    Interlocked.Add(ref transferredBytes, Math.Max(0, current - lastBytes));
                    lastBytes = Math.Max(lastBytes, current);
                    CopyProgress();
                }
                copies[i] = Copy(Path.Join(worker.Original, file.Name), Path.Join(worker.Copied, file.Name), file,
                    srcStreams && !_targetStreams, checkPath: false, transferred: progress is null ? null : Transferred);
                Interlocked.Increment(ref copiedFiles);
                if (progress is not null) CopyProgress();
            }
            catch (Exception error) when (FileActions.IsActionError(error))
            {
                var relative = Path.Join(folders[file.Folder].Relative, file.Name);
                Interlocked.CompareExchange(ref copyError, new IOException($"{relative}：{error.Message}", error), null);
                loop.Stop();
            }
            return worker;
        }, worker => worker.Dispose());
        if (copyError is not null) throw new IOException("复制未完成，原目录未删除。" + copyError.Message, copyError);
        progress?.Report("保留目录元数据", .90, totalBytes);
        for (var i = folders.Count - 1; i >= 0; i--) SetFolderMetadata(Path.Join(staged, folders[i].Relative), folders[i].Info);
        progress?.Report("发布目标", .92, totalBytes);
        publish(staged);

        var failed = 0;
        string? firstError = null;
        Exception? firstDenied = null;
        var removed = 0;
        var toRemove = files.Count + links.Count + folders.Count;
        void RemovalProgress()
        {
            var done = Interlocked.Increment(ref removed);
            progress?.Report("核验并移除原件", .92 + .07 * done / Math.Max(1d, toRemove), totalBytes);
        }
        void Record(string name, Exception error)
        {
            Interlocked.Increment(ref failed);
            Interlocked.CompareExchange(ref firstError, $"{name}：{error.Message}", null);
            if (FileActions.IsAccessDenied(error)) Interlocked.CompareExchange(ref firstDenied, error, null);
        }
        Parallel.For(0, files.Count, _parallel, () => new TreeWorker(), (i, _, worker) =>
        {
            var file = files[i];
            try
            {
                worker.Use(file, folders, source, output);
                RemoveCopiedFile(Path.Join(worker.Original, file.Name), Path.Join(worker.Copied, file.Name), copies[i], checkPaths: false);
            }
            catch (Exception error) when (FileActions.IsActionError(error)) { Record(Path.Join(folders[file.Folder].Relative, file.Name), error); }
            finally { if (progress is not null) RemovalProgress(); }
            return worker;
        }, worker => worker.Dispose());
        foreach (var link in links)
        {
            try { link.RemoveOriginal(source, output); }
            catch (Exception error) when (FileActions.IsActionError(error)) { Record(link.Relative, error); }
            finally { if (progress is not null) RemovalProgress(); }
        }
        // Remove only empty directories with the original identity. New/changed entries are not deleted.
        for (var i = folders.Count - 1; i >= 0; i--)
        {
            var dir = folders[i];
            var original = Path.Join(source, dir.Relative);
            try
            {
                PathSafety.RequireOrdinaryPath(original);
                using var handle = PathSafety.OpenMetadataNoFollow(original, delete: true);
                if (PathSafety.Identity(handle, PathSafety.OrdinaryInfo(handle, directory: true)) != dir.Identity)
                    throw new IOException("源目录已替换。");
                if (srcStreams && HasNamedStream(original)) throw new IOException("源目录新增了附加数据流，已保留。");
                PathSafety.RemoveByHandle(handle);
            }
            catch (Exception error) when (FileActions.IsActionError(error)) { Record(dir.Relative.Length == 0 ? item.Snapshot.Name : dir.Relative, error); }
            finally { if (progress is not null) RemovalProgress(); }
        }
        if (failed > 0) throw new IOException($"目标副本已保留；原位置有 {failed:N0} 个条目未能移除。{firstError}", firstDenied);
    }

    private static (List<FolderCopy>, List<FileCopy>, List<LocalJunction>) PrepareTree(PreparedFileSource item, string staged, bool supportsStreams)
    {
        var folders = new List<FolderCopy>();
        var files = new List<FileCopy>();
        var links = new List<LocalJunction>();
        var pending = new Stack<string>();
        pending.Push("");
        while (pending.TryPop(out var relative))
        {
            var path = Path.Join(item.Path, relative);
            PathSafety.RequireOrdinaryPath(path);
            using var handle = PathSafety.OpenMetadataNoFollow(path, share: 3);
            var info = PathSafety.OrdinaryInfo(handle, directory: true);
            var identity = PathSafety.Identity(handle, info);
            if (relative.Length == 0 && (identity != item.Identity || info.LastWriteTicks != item.Snapshot.Ticks))
                throw new IOException("源目录已改变，请先刷新。");
            if (((FileAttributes)info.Attributes & FileAttributes.Encrypted) != 0 || supportsStreams && HasNamedStream(path))
                throw new IOException($"目录含加密属性或附加数据流，不能安全跨盘移动：{path}");
            var targetDir = Path.Join(staged, relative);
            CreateNewDirectory(targetDir);
            using var targetGuard = PathSafety.OpenMetadataNoFollow(targetDir, share: 3);
            var targetId = PathSafety.Identity(targetGuard, PathSafety.OrdinaryInfo(targetGuard, directory: true));
            var index = folders.Count;
            folders.Add(new(relative, identity, targetId, info));
            foreach (var entry in DiskScanner.Entries(path))
            {
                var child = Path.Join(relative, entry.Name);
                if (IsStageName(entry.Name)) throw new IOException($"包含移动暂存目录，未继续：{child}");
                if (PathSafety.IsSpecial(entry.Attributes))
                {
                    try { links.Add(LocalJunction.Copy(child, item.Path, staged, item.Path, item.Output!)); }
                    catch (Exception error) when (FileActions.IsActionError(error) || error is ArgumentException)
                    { throw new IOException($"无法安全跨盘移动链接或离线项：{child}。{error.Message}", error); }
                }
                else if ((entry.Attributes & FileAttributes.Directory) != 0) pending.Push(child);
                else files.Add(new(entry.Name, index, entry.Length, entry.ModifiedTicks));
            }
        }
        return (folders, files, links);
    }

    private static void SetFolderMetadata(string path, PathSafety.HandleInfo info)
    {
        Directory.SetCreationTimeUtc(path, new DateTime(info.CreationTicks, DateTimeKind.Utc));
        Directory.SetLastWriteTimeUtc(path, new DateTime(info.LastWriteTicks, DateTimeKind.Utc));
        var attrs = (FileAttributes)info.Attributes & (FileAttributes.ReadOnly | FileAttributes.Hidden | FileAttributes.System |
            FileAttributes.Archive | FileAttributes.NotContentIndexed);
        File.SetAttributes(path, attrs == 0 ? FileAttributes.Normal : attrs);
    }

    private static CopiedFile Copy(string source, string target, FileCopy entry, bool checkNamedStreams,
        PathSafety.FileIdentity? expectedIdentity = null, bool checkPath = true, Action<long>? transferred = null)
    {
        if (checkPath) PathSafety.RequireOrdinaryPath(source);
        using var guard = PathSafety.OpenReadNoFollow(source); // Deny changes while the copy API reads the path.
        var before = Stamp(guard);
        if (before.Length != entry.Length || before.Write != entry.Ticks || expectedIdentity is { } id && before.Identity != id)
            throw new IOException("源文件已改变，请先刷新。");
        if (checkNamedStreams && HasNamedStream(source)) throw new IOException("目标文件系统无法保留附加数据流。");
        CopyProgressRoutine? callback = transferred is null ? null :
            (_, copied, _, _, _, _, _, _, _) => { transferred(Math.Min(copied, entry.Length)); return 0; };
        if (!CopyFileEx(Extended(source), Extended(target), callback, IntPtr.Zero, IntPtr.Zero, CopyFailIfExists | CopySymlinkItself))
            throw new Win32Exception();
        transferred?.Invoke(entry.Length);
        using var targetHandle = PathSafety.OpenMetadataNoFollow(target);
        var after = Stamp(targetHandle);
        if (before.Length != after.Length) throw new IOException("复制后长度校验失败，原文件保留。");
        return new(before, after);
    }

    private static void RemoveCopiedFile(string source, string target, CopiedFile copied, bool checkPaths = true, bool strictTarget = false)
    {
        if (checkPaths) PathSafety.RequireOrdinaryPath(target);
        using var targetGuard = PathSafety.OpenReadNoFollow(target);
        var current = Stamp(targetGuard);
        // Publication renames the staged item, which can legitimately update the change time.
        if (strictTarget && current != copied.Target || current.Identity != copied.Target.Identity || current.Length != copied.Target.Length || current.Write != copied.Target.Write)
            throw new IOException("目标文件已改变，原文件保留。");
        if (checkPaths) PathSafety.RequireOrdinaryPath(source);
        using var original = PathSafety.OpenMetadataNoFollow(source, delete: true);
        if (Stamp(original) != copied.Source) throw new IOException("源文件已改变，原文件保留。");
        PathSafety.RemoveByHandle(original);
    }

    // A bounded merge worker uses one private scratch filename at a time, not an in-memory record per file.
    internal static int MergeFile(string source, string output, string scratch, long length, long ticks, Action<long>? transferred)
    {
        var keptBoth = false;
        if (PathSafety.Exists(output))
        {
            PathSafety.RequireOrdinaryPath(Path.GetDirectoryName(output)!);
            var attributes = File.GetAttributes(output);
            if (!PathSafety.IsSpecial(attributes) && (attributes & FileAttributes.Directory) == 0)
            {
                CopiedFile? same = null;
                using (var original = PathSafety.OpenReadNoFollow(source))
                using (var existing = PathSafety.OpenReadNoFollow(output))
                {
                    var from = Stamp(original);
                    var to = Stamp(existing);
                    if (from.Length != length || from.Write != ticks) throw new IOException("来源文件已变化，已保留。");
                    if (from.Length == to.Length && !HasNamedStream(source) && !HasNamedStream(output))
                    {
                        using var a = File.OpenRead(source);
                        using var b = File.OpenRead(output);
                        if (SHA256.HashData(a).AsSpan().SequenceEqual(SHA256.HashData(b))) same = new(from, to);
                    }
                }
                if (same is { } verified)
                {
                    RemoveCopiedFile(source, output, verified, checkPaths: false, strictTarget: true);
                    return 1;
                }
            }
            output = KeepBothName(output);
            keptBoth = true;
        }
        if (string.Equals(Path.GetPathRoot(source), Path.GetPathRoot(output), StringComparison.OrdinalIgnoreCase))
        {
            using var original = PathSafety.OpenMetadataNoFollow(source, delete: true, share: 3);
            var info = PathSafety.OrdinaryInfo(original, directory: false);
            if (info.Length != length || info.LastWriteTicks != ticks) throw new IOException("来源文件已变化，已保留。");
            PathSafety.RenameByHandle(original, output);
            return keptBoth ? 2 : 0;
        }
        var copied = Copy(source, scratch, new("", 0, length, ticks),
            checkNamedStreams: !VolumeStreams.GetOrAdd(Path.GetPathRoot(output)!, SupportsNamedStreams),
            checkPath: false, transferred: transferred);
        Rename(scratch, output);
        RemoveCopiedFile(source, output, copied, checkPaths: false);
        return keptBoth ? 2 : 0;
    }

    internal static string KeepBothName(string output)
    {
        var name = Path.GetFileNameWithoutExtension(output);
        var extension = Path.GetExtension(output);
        return Path.Join(Path.GetDirectoryName(output), name[..Math.Min(150, name.Length)] +
            ".盘镜保留-" + Guid.NewGuid().ToString("N")[..12] + extension[..Math.Min(40, extension.Length)]);
    }

    private static StableFile Stamp(SafeFileHandle handle)
    {
        var info = PathSafety.OrdinaryInfo(handle, directory: false);
        if (!GetBasicInfo(handle, 0, out var basic, (uint)Marshal.SizeOf<BasicInfo>())) throw new Win32Exception();
        return new(PathSafety.Identity(handle, info), info.Length, info.LastWriteTicks, basic.ChangeTime);
    }

    public void CleanUp()
    {
        try
        {
            PathSafety.RequireOrdinaryPath(_stage);
            using (var handle = PathSafety.OpenMetadataNoFollow(_stage, share: 3))
                if (PathSafety.Identity(handle, PathSafety.OrdinaryInfo(handle, directory: true)) != _stageIdentity)
                    throw new IOException("暂存目录已被替换。");
            // Only the private GUID scratch directory is eligible for recursive removal, never a source or published item.
            Directory.Delete(_stage, recursive: true);
        }
        catch (Exception error) when (FileActions.IsActionError(error))
        { CleanupError = $"暂存目录未清理，请核对后处理：{_stage}\n{error.Message}"; }
    }

    private static bool SupportsNamedStreams(string root)
    {
        if (!GetVolumeInformation(root, IntPtr.Zero, 0, out _, out _, out var flags, IntPtr.Zero, 0))
            throw new Win32Exception();
        return (flags & NamedStreams) != 0;
    }

    internal static bool HasNamedStream(string path)
    {
        var find = FindFirstStream(Extended(path), 0, out var data, 0);
        if (find == new IntPtr(-1))
        {
            var error = Marshal.GetLastWin32Error();
            if (error == HandleEof) return false;
            throw new Win32Exception(error);
        }
        try
        {
            do { if (data.Name != "::$DATA") return true; } while (FindNextStream(find, out data));
            var error = Marshal.GetLastWin32Error();
            if (error != HandleEof) throw new Win32Exception(error);
            return false;
        }
        finally { FindClose(find); }
    }

    internal static void Rename(string source, string target)
    {
        // A kernel rename cannot overwrite, recurse or fall back to copying across disks.
        if (!MoveFileEx(Extended(source), Extended(target), 0)) throw new Win32Exception();
    }

    internal static void CreateNewDirectory(string path)
    {
        if (!CreateDirectory(Extended(path), IntPtr.Zero)) throw new Win32Exception();
    }

    private static string Extended(string path) => @"\\?\" + path;

    [StructLayout(LayoutKind.Sequential)]
    private struct BasicInfo { public long CreationTime, AccessTime, WriteTime, ChangeTime; public uint Attributes; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StreamData { public long Size; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 296)] public string Name; }

    [DllImport("kernel32.dll", EntryPoint = "MoveFileExW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool MoveFileEx(string source, string target, uint flags);
    [DllImport("kernel32.dll", EntryPoint = "CopyFileExW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CopyFileEx(string source, string target, CopyProgressRoutine? callback, IntPtr data, IntPtr cancel, uint flags);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate uint CopyProgressRoutine(long totalSize, long transferred, long streamSize, long streamTransferred,
        uint streamNumber, uint reason, IntPtr source, IntPtr destination, IntPtr data);
    [DllImport("kernel32.dll", EntryPoint = "CreateDirectoryW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateDirectory(string path, IntPtr security);
    [DllImport("kernel32.dll", EntryPoint = "GetVolumeInformationW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumeInformation(string root, IntPtr name, uint nameSize, out uint serial,
        out uint componentLength, out uint flags, IntPtr filesystemName, uint filesystemSize);
    [DllImport("kernel32.dll", EntryPoint = "FindFirstStreamW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr FindFirstStream(string path, int level, out StreamData data, uint flags);
    [DllImport("kernel32.dll", EntryPoint = "FindNextStreamW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FindNextStream(IntPtr find, out StreamData data);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FindClose(IntPtr find);
    [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetBasicInfo(SafeFileHandle handle, int infoClass, out BasicInfo info, uint size);
}
