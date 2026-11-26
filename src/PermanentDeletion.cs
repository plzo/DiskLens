using System.Collections.Concurrent;

namespace DiskLens;

internal static class PermanentDeletion
{
    public static async Task<FileActionResult> RunAsync(FileActionPlan plan, int workers, FileTaskProgress? progress = null)
    {
        var parallel = new ParallelOptions { MaxDegreeOfParallelism = workers };
        var completed = 0;
        var errors = new ConcurrentQueue<string>();
        var indexed = plan.Items.Select((item, index) => (Item: item, Progress: progress?.Item(index))).ToArray();
        Parallel.ForEach(indexed.Where(entry => !entry.Item.Snapshot.Directory), parallel, entry =>
        {
            var item = entry.Item;
            try
            {
                entry.Progress?.Report("核验并删除文件");
                FileActions.Revalidate(item);
                RemoveFile(item.Path, item.Snapshot.Ticks, item.Snapshot.Bytes, item.Identity);
                Interlocked.Increment(ref completed);
                entry.Progress?.Complete();
            }
            catch (Exception error) when (FileActions.IsActionError(error)) { errors.Enqueue($"{item.Snapshot.Name}：{error.Message}"); }
        });
        foreach (var entry in indexed.Where(entry => entry.Item.Snapshot.Directory))
        {
            var item = entry.Item;
            try
            {
                entry.Progress?.Report("重新核验目录（未开始删除）");
                FileActions.Revalidate(item);
                var live = await DiskScanner.ScanAsync(item.Path, workers, null, CancellationToken.None).ConfigureAwait(false);
                if (live.Root.IsPartial || live.Totals != item.Snapshot.FolderTotals)
                    throw new IOException("目录内容已变更或无法完整读取，未删除。请刷新并重新确认。");
                FileActions.Revalidate(item);
                RemoveTree(item, live.Root, parallel, entry.Progress);
                completed++;
                entry.Progress?.Complete();
            }
            catch (Exception error) when (FileActions.IsActionError(error)) { errors.Enqueue($"{item.Snapshot.Name}：{error.Message}"); }
        }
        return new(completed, false, errors.ToArray());
    }

    private static void RemoveTree(PreparedFileSource item, DiskNode root, ParallelOptions parallel, FileTaskProgress.ItemProgress? progress)
    {
        var folders = new List<(string Path, PathSafety.FileIdentity Id)>();
        var files = new List<(int Folder, DiskNode Node)>();
        var pending = new Stack<(string Path, DiskNode Node)>();
        pending.Push((item.Path, root));
        while (pending.TryPop(out var folder))
        {
            var (path, node) = folder;
            PathSafety.RequireOrdinaryPath(path);
            PathSafety.RequireUnprotected(path, source: true);
            using var guard = PathSafety.OpenMetadataNoFollow(path, share: 3);
            var info = PathSafety.OrdinaryInfo(guard, directory: true);
            var id = PathSafety.Identity(guard, info);
            if (info.LastWriteTicks != node.ModifiedTicks || node == root && id != item.Identity)
                throw new IOException("目录在删除准备期间已改变，未删除。");
            var index = folders.Count;
            folders.Add((path, id));
            if (node.Children is null) continue;
            foreach (var child in node.Children)
            {
                if (CrossVolumeMover.IsStageName(child.Name)) throw new IOException("目录包含移动暂存项，未删除。");
                var full = Path.Join(path, child.Name);
                if (child.IsDirectory) pending.Push((full, child));
                else files.Add((index, child));
            }
        }

        var failed = 0;
        var attempted = 0;
        var total = files.Count + folders.Count;
        void Report()
        {
            var count = Interlocked.Increment(ref attempted);
            progress?.Report("正在并行删除", .05 + .94 * count / Math.Max(1d, total));
        }
        progress?.Report("正在并行删除", .05);
        string? firstError = null;
        void Record(string path, Exception error)
        {
            Interlocked.Increment(ref failed);
            Interlocked.CompareExchange(ref firstError, $"{path}：{error.Message}", null);
        }
        Parallel.ForEach(files, parallel, () => new PathSafety.DirectoryGuard(), (file, _, guard) =>
        {
            var parent = folders[file.Folder];
            var path = Path.Join(parent.Path, file.Node.Name);
            try
            {
                guard.Use(parent.Path, parent.Id);
                RemoveFile(path, file.Node.ModifiedTicks, file.Node.Bytes, checkPath: false);
            }
            catch (Exception error) when (FileActions.IsActionError(error)) { Record(path, error); }
            finally { if (progress is not null) Report(); }
            return guard;
        }, guard => guard.Dispose());
        // No blind recursive delete. Only the known, empty, unchanged directory objects are removed.
        for (var i = folders.Count - 1; i >= 0; i--)
        {
            var (path, identity) = folders[i];
            try
            {
                PathSafety.RequireOrdinaryPath(path);
                using var original = PathSafety.OpenMetadataNoFollow(path, delete: true);
                if (PathSafety.Identity(original, PathSafety.OrdinaryInfo(original, directory: true)) != identity)
                    throw new IOException("目录已被替换。");
                PathSafety.RemoveByHandle(original);
            }
            catch (Exception error) when (FileActions.IsActionError(error)) { Record(path, error); }
            finally { if (progress is not null) Report(); }
        }
        if (failed > 0) throw new IOException($"部分删除；{failed:N0} 个文件或目录未移除。{firstError}");
    }

    private static void RemoveFile(string path, long ticks, long length, PathSafety.FileIdentity? expected = null, bool checkPath = true)
    {
        if (checkPath) PathSafety.RequireOrdinaryPath(path);
        using var handle = PathSafety.OpenMetadataNoFollow(path, delete: true);
        var info = PathSafety.OrdinaryInfo(handle, directory: false);
        if (info.LastWriteTicks != ticks || info.Length != length || expected is { } id && PathSafety.Identity(handle, info) != id)
            throw new IOException("文件已改变，已保留。");
        PathSafety.RemoveByHandle(handle);
    }
}
