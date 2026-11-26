using System.ComponentModel;
using System.Runtime.InteropServices;

namespace DiskLens;

internal enum FileActionKind { Move, Recycle, Delete }
internal enum MoveConflict { Reject, Review, Skip, Replace, Merge }

internal sealed record FileActionResult(int Completed, bool Aborted, string[] Errors, bool PermissionDenied = false,
    int Skipped = 0, string[]? Backups = null);
internal sealed record FileSource(string Name, string Path, bool Directory, bool Movable, bool Allowed, long Ticks, long Bytes, ScanProgress? FolderTotals)
{
    public static FileSource Capture(DiskNode node) => new(node.Name, node.FullPath, node.IsDirectory,
        node.Parent is not null && !node.IsUnavailable,
        node.IsSizeKnown && node.Parent is not null && !node.IsUnavailable && !node.ReadFailed && !(node.IsDirectory && node.IsPartial),
        node.ModifiedTicks, node.Bytes, node.SubtreeTotals);
}
internal sealed record MoveCollision(string Output, PathSafety.FileIdentity Identity, bool Directory, long Ticks, long Bytes, string Backup);
internal sealed record PreparedFileSource(FileSource Snapshot, string Path, PathSafety.FileIdentity Identity, string? Output, bool CrossVolume,
    MoveCollision? Collision = null);
internal sealed record FileActionPlan(FileActionKind Kind, PreparedFileSource[] Items, string? Target, PathSafety.FileIdentity TargetIdentity, string? Stage,
    string[] Claims, int Skipped = 0, MoveConflict Conflict = MoveConflict.Reject)
{
    public string Verb => Kind switch { FileActionKind.Move => "移动", FileActionKind.Delete => "彻底删除", _ => "移到回收站" };
    public string Label => $"{Verb} {Items.Length:N0} 项 · {Items[0].Snapshot.Name}" + (Items.Length > 1 ? " 等" : "");
}

internal static class FileActions
{
    public const int BatchLimit = 200;
    internal static int MoveWorkers => Math.Clamp(Environment.ProcessorCount, 2, 4);
    private const uint AllowUndo = 0x0040, WantNukeWarning = 0x4000, AddUndoRecord = 0x20000000, RecycleOnDelete = 0x00080000;
    private const int Cancelled = unchecked((int)0x800704C7);
    private static readonly Guid ShellItemId = new("43826D1E-E718-42EE-BC55-A1E261C37BFE");
    private static readonly Guid FileOperationId = new("3AD05575-8857-4850-9277-11B85BDB8E09");

    internal static bool IsActionError(Exception error) => error is IOException or UnauthorizedAccessException or Win32Exception or COMException or System.Security.SecurityException;
    internal static bool IsAccessDenied(Exception error) => ErrorCode(error, 5, 1314);

    private static bool ErrorCode(Exception error, params int[] codes)
    {
        for (Exception? current = error; current is not null; current = current.InnerException)
            if (current is Win32Exception win32 ? codes.Contains(win32.NativeErrorCode) :
                codes.Any(code => current.HResult == (unchecked((int)0x80070000) | code))) return true;
        return false;
    }

    internal static string MoveError(Exception error) => error.Message +
        (ErrorCode(error, 32, 33) ? " 文件或目录被占用，请关闭相关程序后重试。" :
        ErrorCode(error, 80, 183) ? " 路径已存在；请核对目标并重新提交，没有直接覆盖。" :
        IsAccessDenied(error) ? " Windows 拒绝访问；请核对源或目标权限，管理员权限不一定能解除限制。" : "");

    public static async Task<FileActionResult> RunAsync(IReadOnlyList<DiskNode> nodes, string? destination, IntPtr owner)
    {
        var snapshots = nodes.Select(FileSource.Capture).ToArray();
        var plan = await Task.Run(() => Prepare(snapshots, destination)).ConfigureAwait(false);
        return await RunAsync(plan, owner).ConfigureAwait(false);
    }

    public static Task<FileActionResult> RunAsync(FileActionPlan plan, IntPtr owner = default, FileTaskProgress? progress = null) => plan.Kind switch
    {
        FileActionKind.Move => Task.Run(() => Move(plan, progress)),
        FileActionKind.Delete => Task.Run(() => PermanentDeletion.RunAsync(plan, MoveWorkers, progress)),
        _ => RecycleAsync(plan, owner, progress)
    };

    private static Task<FileActionResult> RecycleAsync(FileActionPlan plan, IntPtr owner, FileTaskProgress? progress)
    {
        var tcs = new TaskCompletionSource<FileActionResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { tcs.SetResult(ExecuteRecycle(plan, owner, progress)); }
            catch (Exception error) { tcs.SetException(error); }
        }) { IsBackground = true, Name = "DiskLens Recycle Bin" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return tcs.Task;
    }

    public static FileActionPlan Prepare(IReadOnlyList<FileSource> nodes, string? destination, bool permanent = false,
        MoveConflict conflict = MoveConflict.Reject)
    {
        if (nodes.Count is 0 or > BatchLimit) throw new IOException($"每次请选择 1–{BatchLimit} 项。");
        PathSafety.FileIdentity targetIdentity = default;
        var target = destination is null ? null : ResolveTarget(destination, out targetIdentity);
        if (permanent && target is not null) throw new ArgumentException("删除不能指定目标路径。");
        var kind = target is not null ? FileActionKind.Move : permanent ? FileActionKind.Delete : FileActionKind.Recycle;
        var items = new PreparedFileSource[nodes.Count];
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < nodes.Count; i++)
        {
            var source = ValidateSource(nodes[i], out var identity, moving: kind == FileActionKind.Move);
            for (var j = 0; j < i; j++)
                if (Overlaps(source, items[j].Path)) throw new IOException("选中项包含同一文件或父子项，请只选其中一项。");
            string? output = null;
            MoveCollision? collision = null;
            var cross = false;
            if (target is null)
            {
                if (!permanent && new DriveInfo(Path.GetPathRoot(source)!).DriveType != DriveType.Fixed)
                    throw new IOException("仅对本机固定磁盘请求回收站操作；未删除任何项目。");
            }
            else
            {
                if (PathSafety.Within(target, source)) throw new IOException("不能把文件夹移动到自身或子目录。");
                var name = Path.GetFileName(source);
                output = Path.Join(target, name);
                if (!names.Add(name)) throw new IOException($"本批次有多个同名来源：{name}。请分批移动。");
                if (PathSafety.Exists(output))
                {
                    if (conflict == MoveConflict.Reject)
                        throw new IOException($"目标存在同名项：{output}。未移动任何项目。");
                    // A skip is always safe. A protected, unreadable or special collision can only be skipped.
                    if (conflict != MoveConflict.Skip)
                    {
                        try
                        {
                            var existing = PathSafety.CanonicalLocal(output, out var info, out var existingId);
                            if (!string.Equals(existing, output, StringComparison.OrdinalIgnoreCase))
                                throw new IOException("同名项路径解析结果不一致。");
                            PathSafety.RequireUnprotected(existing, source: true);
                            collision = new(output, existingId, ((FileAttributes)info.Attributes & FileAttributes.Directory) != 0,
                                info.LastWriteTicks, info.Length, Path.Join(target, "盘镜覆盖备份-" + Guid.NewGuid().ToString("N")));
                        }
                        catch (Exception error) when (IsActionError(error) && conflict == MoveConflict.Review)
                        { /* Keep the conflict visible, but do not allow replacing it. */ }
                    }
                    collision ??= new(output, default, false, 0, 0, "");
                }
                cross = !string.Equals(Path.GetPathRoot(source), Path.GetPathRoot(target), StringComparison.OrdinalIgnoreCase);
            }
            items[i] = new(nodes[i], source, identity, output, cross, collision);
        }
        for (var i = 0; i < items.Length; i++)
            if (items[i].Output is { } output && items.Any(other => Overlaps(output, other.Path)))
                throw new IOException($"目标与本批次来源重叠：{output}。请另选目标。");
        return WithConflicts(new(kind, items, target, targetIdentity, null, []), conflict);
    }

    internal static FileActionPlan WithConflicts(FileActionPlan plan, MoveConflict choice)
    {
        var items = choice == MoveConflict.Skip ? plan.Items.Where(item => item.Collision is null).ToArray() : plan.Items;
        if (choice is MoveConflict.Replace or MoveConflict.Merge && items.Any(item => item.Collision is { Backup.Length: 0 }))
            throw new IOException("同名项含受保护、链接或不可读取的路径，只能跳过。");
        var stage = choice == MoveConflict.Merge || items.Any(item => item.CrossVolume) ?
            Path.Join(plan.Target, CrossVolumeMover.StagePrefix + Guid.NewGuid().ToString("N")) : null;
        var claims = new List<string>();
        foreach (var item in items)
        {
            claims.Add(item.Path);
            if (item.Output is { } output) claims.Add(output);
            if (choice == MoveConflict.Replace && item.Collision is { } collision) claims.Add(collision.Backup);
        }
        if (stage is not null) claims.Add(stage);
        return plan with { Items = items, Stage = stage, Claims = claims.ToArray(), Skipped = plan.Items.Length - items.Length, Conflict = choice };
    }

    internal static bool Overlaps(string a, string b) => PathSafety.Within(a, b) || PathSafety.Within(b, a);

    private static string ResolveTarget(string destination, out PathSafety.FileIdentity identity)
    {
        var target = PathSafety.CanonicalLocal(destination, out var info, out identity);
        PathSafety.RequireUnprotected(target, source: false);
        if (((FileAttributes)info.Attributes & FileAttributes.Directory) == 0) throw new IOException("目标文件夹不可用。");
        return target;
    }

    private static string ValidateSource(FileSource node, out PathSafety.FileIdentity identity, bool moving)
    {
        if (moving ? !node.Movable : !node.Allowed)
            throw new IOException(moving ? "不能移动扫描根、链接或离线项本身。" : "扫描根、链接、离线项或含未扫描内容的目录不能删除。");
        var path = PathSafety.CanonicalLocal(node.Path, out var info, out identity);
        if (!string.Equals(Path.GetFileName(path), node.Name, StringComparison.OrdinalIgnoreCase))
            throw new IOException("文件名解析后与扫描记录不同，拒绝操作。");
        PathSafety.RequireUnprotected(path, source: true);
        if ((((FileAttributes)info.Attributes & FileAttributes.Directory) != 0) != node.Directory ||
            info.LastWriteTicks != node.Ticks || (!node.Directory && info.Length != node.Bytes))
            throw new IOException($"{node.Name} 自扫描后已改变，请先刷新。");
        return path;
    }

    internal static void Revalidate(PreparedFileSource item, bool moving = false)
    {
        if (!string.Equals(item.Path, ValidateSource(item.Snapshot, out var identity, moving), StringComparison.OrdinalIgnoreCase) || identity != item.Identity)
            throw new IOException("来源路径或文件实体已改变，请先刷新。");
    }

    private static void RevalidateTarget(FileActionPlan plan)
    {
        if (!string.Equals(plan.Target, ResolveTarget(plan.Target!, out var identity), StringComparison.OrdinalIgnoreCase) || identity != plan.TargetIdentity)
            throw new IOException("目标文件夹已改变。");
    }

    private static FileActionResult Move(FileActionPlan plan, FileTaskProgress? progress)
    {
        if (plan.Conflict == MoveConflict.Review && plan.Items.Any(item => item.Collision is not null))
            throw new IOException("请先确认同名处理方式。");
        if (plan.Items.Length == 0) return new(0, false, [], Skipped: plan.Skipped);
        RevalidateTarget(plan);
        // The handle denies rename/removal of the destination container while workers use it.
        using var targetGuard = PathSafety.OpenMetadataNoFollow(plan.Target!, share: 3);
        var targetInfo = PathSafety.OrdinaryInfo(targetGuard, directory: true);
        if (PathSafety.Identity(targetGuard, targetInfo) != plan.TargetIdentity) throw new IOException("目标文件夹已改变。");
        var errors = new string?[plan.Items.Length];
        var backups = new string?[plan.Items.Length];
        var completed = 0;
        var denied = 0;
        var stage = plan.Stage is null ? null : new CrossVolumeMover(plan.Stage, MoveWorkers);
        try
        {
            void Run(int i)
            {
                var item = plan.Items[i];
                var itemProgress = progress?.Item(i);
                try
                {
                    itemProgress?.Report(item.CrossVolume ? "准备跨盘移动" : "同盘改名");
                    Revalidate(item, moving: true);
                    void Publish(string source) => PublishMove(source, item, path => backups[i] = path);
                    if (plan.Conflict == MoveConflict.Merge || item.CrossVolume && item.Snapshot.Directory && item.Collision is null)
                        StreamingMergeMover.MoveAsync(item, plan, plan.Stage!, itemProgress).GetAwaiter().GetResult();
                    else if (stage is not null && item.CrossVolume) stage.Move(item, Publish, itemProgress);
                    else Publish(item.Path);
                    Interlocked.Increment(ref completed);
                    itemProgress?.Complete();
                }
                catch (Exception error) when (IsActionError(error))
                {
                    if (IsAccessDenied(error)) Interlocked.Exchange(ref denied, 1);
                    errors[i] = $"{item.Snapshot.Name}：{MoveError(error)}";
                }
            }
            // A same-volume folder is one rename. Cross-volume folders have their own file workers.
            Parallel.ForEach(Enumerable.Range(0, plan.Items.Length).Where(i => !plan.Items[i].CrossVolume || !plan.Items[i].Snapshot.Directory),
                new ParallelOptions { MaxDegreeOfParallelism = MoveWorkers }, Run);
            for (var i = 0; i < plan.Items.Length; i++)
                if (plan.Items[i].CrossVolume && plan.Items[i].Snapshot.Directory) Run(i);
        }
        finally { stage?.CleanUp(); }
        var problems = errors.OfType<string>().ToList();
        if (stage?.CleanupError is { } cleanup) problems.Add(cleanup);
        return new(completed, false, problems.ToArray(), denied != 0, plan.Skipped, backups.OfType<string>().ToArray());
    }

    private static void PublishMove(string source, PreparedFileSource item, Action<string?> reportBackup)
    {
        var output = item.Output!;
        if (item.Collision is not { } collision)
        {
            PathSafety.RequireOrdinaryPath(output, allowMissingLeaf: true);
            CrossVolumeMover.Rename(source, output);
            return;
        }
        PathSafety.RequireOrdinaryPath(output);
        PathSafety.RequireUnprotected(output, source: true);
        using var existing = PathSafety.OpenMetadataNoFollow(output, delete: true, share: 3);
        var info = PathSafety.OrdinaryInfo(existing, collision.Directory);
        if (PathSafety.Identity(existing, info) != collision.Identity || info.LastWriteTicks != collision.Ticks ||
            !collision.Directory && info.Length != collision.Bytes)
            throw new IOException("同名目标在确认后已变化，未覆盖。");
        PathSafety.RenameByHandle(existing, collision.Backup);
        reportBackup(collision.Backup);
        try { CrossVolumeMover.Rename(source, output); }
        catch (Exception error) when (IsActionError(error))
        {
            try { PathSafety.RenameByHandle(existing, output); reportBackup(null); }
            catch (Exception restore) when (IsActionError(restore))
            {
                throw new IOException($"移动失败，原目标仍保存在备份中：{collision.Backup}\n恢复失败：{restore.Message}", error);
            }
            throw;
        }
    }

    private static FileActionResult ExecuteRecycle(FileActionPlan plan, IntPtr owner, FileTaskProgress? progress)
    {
        var done = 0;
        var errors = new List<string>();
        for (var i = 0; i < plan.Items.Length; i++)
        {
            var item = plan.Items[i];
            try
            {
                progress?.Item(i).Report("Windows 回收站处理中（可能等待确认）");
                Revalidate(item);
                if (!Recycle(item.Path, owner)) return new(done, true, errors.ToArray());
                if (PathSafety.Exists(item.Path)) throw new IOException("原位置仍有此项，请在资源管理器中核对。");
                done++;
                progress?.Item(i).Complete();
            }
            catch (Exception error) when (IsActionError(error)) { errors.Add($"{item.Snapshot.Name}：{error.Message}"); }
        }
        return new(done, false, errors.ToArray());
    }

    private static bool Recycle(string source, IntPtr owner)
    {
        IFileOperation? op = null;
        IShellItem? from = null;
        try
        {
            op = (IFileOperation)Activator.CreateInstance(Type.GetTypeFromCLSID(FileOperationId, throwOnError: true)!)!;
            // Do not suppress shell confirmations. Windows warns separately if it cannot recycle an item.
            op.SetOperationFlags(AllowUndo | WantNukeWarning | AddUndoRecord | RecycleOnDelete);
            op.SetOwnerWindow(owner);
            Marshal.ThrowExceptionForHR(SHCreateItemFromParsingName(source, IntPtr.Zero, in ShellItemId, out from));
            op.DeleteItem(from, IntPtr.Zero);
            var hr = op.PerformOperations();
            if (op.GetAnyOperationsAborted() || hr == Cancelled) return false;
            Marshal.ThrowExceptionForHR(hr);
            return true;
        }
        finally
        {
            if (op is not null) Marshal.ReleaseComObject(op);
            if (from is not null) Marshal.ReleaseComObject(from);
        }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int SHCreateItemFromParsingName(string path, IntPtr bind, in Guid iid, out IShellItem item);

    [ComImport, Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem { }

    [ComImport, Guid("947AAB5F-0A5C-4C13-B4D6-4BF7836FC9F8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFileOperation
    {
        void Advise(IntPtr sink, out uint cookie);
        void Unadvise(uint cookie);
        void SetOperationFlags(uint flags);
        void SetProgressMessage([MarshalAs(UnmanagedType.LPWStr)] string message);
        void SetProgressDialog(IntPtr dialog);
        void SetProperties(IntPtr properties);
        void SetOwnerWindow(IntPtr owner);
        void ApplyPropertiesToItem(IShellItem item);
        void ApplyPropertiesToItems(IntPtr items);
        void RenameItem(IShellItem item, [MarshalAs(UnmanagedType.LPWStr)] string name, IntPtr sink);
        void RenameItems(IntPtr items, [MarshalAs(UnmanagedType.LPWStr)] string name);
        void MoveItem(IShellItem item, IShellItem destination, [MarshalAs(UnmanagedType.LPWStr)] string? name, IntPtr sink);
        void MoveItems(IntPtr items, IShellItem destination);
        void CopyItem(IShellItem item, IShellItem destination, [MarshalAs(UnmanagedType.LPWStr)] string? name, IntPtr sink);
        void CopyItems(IntPtr items, IShellItem destination);
        void DeleteItem(IShellItem item, IntPtr sink);
        void DeleteItems(IntPtr items);
        void NewItem(IShellItem destination, uint attributes, [MarshalAs(UnmanagedType.LPWStr)] string name, [MarshalAs(UnmanagedType.LPWStr)] string template, IntPtr sink);
        [PreserveSig] int PerformOperations();
        [return: MarshalAs(UnmanagedType.Bool)] bool GetAnyOperationsAborted();
    }
}
