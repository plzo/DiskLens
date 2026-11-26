using System.Collections.Concurrent;
using System.Threading.Channels;

namespace DiskLens;

// Ordinary-file memory is bounded by the queue; directory and link metadata is retained separately.
// Each file is published and verified before removing its original; only empty known folders are removed.
internal static class StreamingMergeMover
{
    private sealed record Folder(string Source, string Target, PathSafety.FileIdentity SourceId,
        PathSafety.FileIdentity TargetId, PathSafety.HandleInfo Info, bool Created);
    private sealed record Work(Folder Folder, string Name, long Length, long Ticks);

    public static async Task MoveAsync(PreparedFileSource item, FileActionPlan plan, string stage,
        FileTaskProgress.ItemProgress? progress, int workers = 8, Action<string>? beforeFile = null)
    {
        if (item.Snapshot.Directory && ((File.GetAttributes(item.Path) & FileAttributes.Encrypted) != 0 ||
            CrossVolumeMover.HasNamedStream(item.Path)))
            throw new IOException("含加密属性或附加数据流的目录不能无损合并，原项保留。");
        // Reserve a new root atomically or recheck the exact root the user agreed to merge.
        var rootOutput = item.Output!;
        var rootCreated = false;
        if (item.Collision is { } collision)
        {
            PathSafety.RequireOrdinaryPath(rootOutput);
            using var existing = PathSafety.OpenMetadataNoFollow(rootOutput, share: 3);
            var info = PathSafety.OrdinaryInfo(existing, collision.Directory);
            if (PathSafety.Identity(existing, info) != collision.Identity || info.LastWriteTicks != collision.Ticks ||
                !collision.Directory && info.Length != collision.Bytes)
                throw new IOException("同名目标在确认后已变化，未合并。");
        }
        else if (PathSafety.Exists(rootOutput)) throw new IOException("目标在提交后出现了同名项，请刷新并重新确认。");
        if (item.Snapshot.Directory && PathSafety.Exists(rootOutput) && (File.GetAttributes(rootOutput) & FileAttributes.Directory) == 0)
            rootOutput = CrossVolumeMover.KeepBothName(rootOutput);
        if (item.Snapshot.Directory && !PathSafety.Exists(rootOutput))
        {
            CrossVolumeMover.CreateNewDirectory(rootOutput);
            rootCreated = true;
        }
        using var rootTargetGuard = new PathSafety.DirectoryGuard();
        if (item.Snapshot.Directory) rootTargetGuard.Use(rootOutput, Stamp(rootOutput).Id);
        var errors = new ConcurrentQueue<string>();
        long errorCount = 0, finished = 0, copied = 0, identical = 0, keptBoth = 0;
        Exception? denied = null;
        void Error(string path, Exception error)
        {
            if (Interlocked.Increment(ref errorCount) <= 24) errors.Enqueue($"{path}：{FileActions.MoveError(error)}");
            if (FileActions.IsAccessDenied(error)) Interlocked.CompareExchange(ref denied, error, null);
        }
        progress?.Report("统计待移动条目（不建立扫描树）");
        var total = item.Snapshot.Directory ? Measure(item.Path) : 1;
        var channel = Channel.CreateBounded<Work>(new BoundedChannelOptions(512)
            { SingleWriter = true, FullMode = BoundedChannelFullMode.Wait });
        using var stop = new CancellationTokenSource();
        var token = stop.Token;
        var folders = new List<Folder>();
        var links = new List<(Folder Folder, string Name)>();
        var mappings = plan.Items.ToList();
        if (rootOutput != item.Output) mappings.Insert(0, item with { Output = rootOutput });
        void Report(long delta = 0)
        {
            Interlocked.Add(ref copied, delta);
            progress?.Report($"流式合并 · 已处理 {Interlocked.Read(ref finished):N0}/{total:N0} 文件及链接",
                .02 + .96 * Math.Min(1, Interlocked.Read(ref finished) / Math.Max(1d, total)), Interlocked.Read(ref copied));
        }
        var consumers = Enumerable.Range(0, Math.Clamp(workers, 1, 8)).Select(index => Task.Run(async () =>
        {
            try
            {
                using var sourceGuard = new PathSafety.DirectoryGuard();
                using var targetGuard = new PathSafety.DirectoryGuard();
                var scratch = Path.Join(stage, "stream-" + Guid.NewGuid().ToString("N"));
                await foreach (var work in channel.Reader.ReadAllAsync(token).ConfigureAwait(false))
                {
                    var source = Path.Join(work.Folder.Source, work.Name);
                    try
                    {
                        beforeFile?.Invoke(source);
                        sourceGuard.Use(work.Folder.Source, work.Folder.SourceId);
                        targetGuard.Use(work.Folder.Target, work.Folder.TargetId);
                        long last = 0;
                        var kind = CrossVolumeMover.MergeFile(source, Path.Join(work.Folder.Target, work.Name), scratch,
                            work.Length, work.Ticks, bytes => { var current = Math.Max(last, bytes); Report(current - last); last = current; });
                        if (kind == 1) Interlocked.Increment(ref identical);
                        if (kind == 2) Interlocked.Increment(ref keptBoth);
                    }
                    catch (Exception error) when (FileActions.IsActionError(error))
                    {
                        Error(source, error);
                        // A failed, unpublished scratch has a live source. Never reuse/delete an unexpected scratch object.
                        if (PathSafety.Exists(scratch)) scratch = Path.Join(stage, "stream-" + Guid.NewGuid().ToString("N"));
                    }
                    finally { Interlocked.Increment(ref finished); Report(); }
                }
            }
            catch (Exception error)
            {
                channel.Writer.TryComplete(error);
                stop.Cancel();
                throw;
            }
        })).ToArray();

        var producer = Task.Run(async () =>
        {
            try
            {
                if (!item.Snapshot.Directory)
                {
                    var sourceParent = Path.GetDirectoryName(item.Path)!;
                    var targetParent = Path.GetDirectoryName(item.Output!)!;
                    var src = Stamp(sourceParent);
                    var dst = Stamp(targetParent);
                    await channel.Writer.WriteAsync(new(new(sourceParent, targetParent, src.Id, dst.Id, src.Info, false),
                        item.Snapshot.Name, item.Snapshot.Bytes, item.Snapshot.Ticks), token);
                    return;
                }
                var pending = new Stack<(string Source, string Target)>();
                pending.Push((item.Path, rootOutput));
                while (pending.TryPop(out var current))
                {
                    token.ThrowIfCancellationRequested();
                    try
                    {
                        PathSafety.RequireOrdinaryPath(current.Source);
                        PathSafety.RequireUnprotected(current.Source, source: true);
                        using var guard = PathSafety.OpenMetadataNoFollow(current.Source, share: 3);
                        var info = PathSafety.OrdinaryInfo(guard, directory: true);
                        var sourceId = PathSafety.Identity(guard, info);
                        if (current.Source == item.Path && sourceId != item.Identity) throw new IOException("来源根已替换。");
                        if (((FileAttributes)info.Attributes & FileAttributes.Encrypted) != 0 || CrossVolumeMover.HasNamedStream(current.Source))
                            throw new IOException("含加密属性或附加数据流的目录不能无损合并，原项保留。");
                        var target = current.Target;
                        PathSafety.RequireOrdinaryPath(Path.GetDirectoryName(target)!);
                        PathSafety.RequireUnprotected(target, source: false);
                        if (PathSafety.Exists(target) && (PathSafety.IsSpecial(File.GetAttributes(target)) ||
                            (File.GetAttributes(target) & FileAttributes.Directory) == 0))
                        {
                            target = CrossVolumeMover.KeepBothName(target);
                            mappings.Insert(0, item with { Path = current.Source, Output = target });
                        }
                        PathSafety.RequireOrdinaryPath(target, allowMissingLeaf: true);
                        var created = current.Source == item.Path ? rootCreated : !PathSafety.Exists(target);
                        if (created && current.Source != item.Path) CrossVolumeMover.CreateNewDirectory(target);
                        var destination = Stamp(target);
                        var folder = new Folder(current.Source, target, sourceId, destination.Id, info, created);
                        folders.Add(folder);
                        foreach (var entry in DiskScanner.Entries(current.Source))
                        {
                            if (CrossVolumeMover.IsStageName(entry.Name)) { Error(Path.Join(current.Source, entry.Name), new IOException("不移动其他任务的暂存目录。")); continue; }
                            if (PathSafety.IsSpecial(entry.Attributes)) links.Add((folder, entry.Name));
                            else if ((entry.Attributes & FileAttributes.Directory) != 0)
                                pending.Push((Path.Join(current.Source, entry.Name), Path.Join(target, entry.Name)));
                            else await channel.Writer.WriteAsync(new(folder, entry.Name, entry.Length, entry.ModifiedTicks), token).ConfigureAwait(false);
                        }
                    }
                    catch (Exception error) when (FileActions.IsActionError(error)) { Error(current.Source, error); }
                }
            }
            catch (Exception error)
            {
                channel.Writer.TryComplete(error);
                stop.Cancel();
                throw;
            }
            finally { channel.Writer.TryComplete(); }
        });
        try { await Task.WhenAll(consumers.Append(producer)).ConfigureAwait(false); }
        catch (Exception error)
        {
            throw new IOException("流式移动中断；所有工作线程已停止，已发布副本与未移动原件保留，不自动重放。", error);
        }
        foreach (var link in links)
        {
            var source = Path.Join(link.Folder.Source, link.Name);
            try
            {
                using var a = new PathSafety.DirectoryGuard();
                using var b = new PathSafety.DirectoryGuard();
                a.Use(link.Folder.Source, link.Folder.SourceId);
                b.Use(link.Folder.Target, link.Folder.TargetId);
                ReparseTransfer.Move(source, Path.Join(link.Folder.Target, link.Name),
                    Path.Join(stage, "link-" + Guid.NewGuid().ToString("N")), mappings);
            }
            catch (Exception error) when (FileActions.IsActionError(error)) { Error(source, error); }
            finally { Interlocked.Increment(ref finished); Report(); }
        }
        progress?.Report($"合并收尾 · 相同文件已核对 {identical:N0} · 差异保留两份 {keptBoth:N0}", .99, copied);
        for (var i = folders.Count - 1; i >= 0; i--)
        {
            var folder = folders[i];
            try
            {
                PathSafety.RequireOrdinaryPath(folder.Source);
                using var original = PathSafety.OpenMetadataNoFollow(folder.Source, delete: true);
                if (PathSafety.Identity(original, PathSafety.OrdinaryInfo(original, directory: true)) != folder.SourceId)
                    throw new IOException("来源目录已替换，已保留。");
                if (CrossVolumeMover.HasNamedStream(folder.Source)) throw new IOException("来源目录新增了附加数据流，已保留。");
                if (folder.Created)
                {
                    using var targetGuard = new PathSafety.DirectoryGuard();
                    targetGuard.Use(folder.Target, folder.TargetId);
                    Directory.SetCreationTimeUtc(folder.Target, new DateTime(folder.Info.CreationTicks, DateTimeKind.Utc));
                    Directory.SetLastWriteTimeUtc(folder.Target, new DateTime(folder.Info.LastWriteTicks, DateTimeKind.Utc));
                    var attributes = (FileAttributes)folder.Info.Attributes & (FileAttributes.ReadOnly | FileAttributes.Hidden |
                        FileAttributes.System | FileAttributes.Archive | FileAttributes.NotContentIndexed);
                    File.SetAttributes(folder.Target, attributes == 0 ? FileAttributes.Normal : attributes);
                }
                PathSafety.RemoveByHandle(original); // fails safely if any entry was retained or added
            }
            catch (Exception error) when (FileActions.IsActionError(error)) { Error(folder.Source, error); }
        }
        if (errorCount > 0)
            throw new IOException($"已处理 {finished:N0} 项；有 {errorCount:N0} 条异常，未能移动的原件保留。\n" + string.Join('\n', errors), denied);
    }

    private static (PathSafety.FileIdentity Id, PathSafety.HandleInfo Info) Stamp(string path)
    {
        PathSafety.RequireOrdinaryPath(path);
        using var handle = PathSafety.OpenMetadataNoFollow(path, share: 3);
        var info = PathSafety.OrdinaryInfo(handle, directory: true);
        return (PathSafety.Identity(handle, info), info);
    }

    private static long Measure(string path)
    {
        long files = 0;
        var pending = new Stack<string>();
        pending.Push(path);
        while (pending.TryPop(out var current))
        {
            try
            {
                PathSafety.RequireOrdinaryPath(current);
                foreach (var entry in DiskScanner.Entries(current))
                    if (CrossVolumeMover.IsStageName(entry.Name)) continue;
                    else if (!PathSafety.IsSpecial(entry.Attributes) && (entry.Attributes & FileAttributes.Directory) != 0)
                        pending.Push(Path.Join(current, entry.Name));
                    else files++;
            }
            catch (Exception error) when (FileActions.IsActionError(error)) { }
        }
        return files;
    }
}
