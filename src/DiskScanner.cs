using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Enumeration;
using System.Threading.Channels;

namespace DiskLens;

internal sealed record ScanIssue(string Path, string Reason);
internal sealed record ScanProgress(long Files, long Directories, long Bytes, int Inaccessible, int Skipped, ulong Fingerprint = 0);
internal sealed record ScanResult(DiskNode Root, ScanProgress Totals, ScanIssue[] Issues, TimeSpan Elapsed,
    ScanProblems Problems, int VisitedDirectories);

// Keep all failures for refresh, independently of the small sample displayed in the UI.
internal sealed class ScanProblems
{
    public const string SkippedReason = "链接或离线项，未跟随、未计入。";
    public HashSet<DiskNode> Failed { get; } = [];
    public HashSet<DiskNode> Skipped { get; } = [];

    public ScanIssue[] Sample() => Failed.Select(n => new ScanIssue(n.FullPath, n.ReadFailure!))
        .Concat(Skipped.Select(n => new ScanIssue(n.FullPath, SkippedReason))).Take(32).ToArray();
}

internal static class DiskScanner
{
    private static readonly EnumerationOptions Options = new()
    {
        IgnoreInaccessible = false, AttributesToSkip = 0, BufferSize = 64 * 1024
    };

    internal readonly record struct Pending(string Path, DiskNode Node);
    internal readonly record struct Entry(string Name, FileAttributes Attributes, long Length, long ModifiedTicks)
    {
        public DiskNode Create(DiskNode parent) => new(Name, parent, PathSafety.IsSpecial(Attributes) ? 0 : Length, ModifiedTicks, Attributes);
    }

    internal static FileSystemEnumerable<Entry> Entries(string path) => new(path,
        static (ref FileSystemEntry e) => new(e.FileName.ToString(), e.Attributes,
            e.IsDirectory ? 0 : e.Length, e.LastWriteTimeUtc.UtcTicks), Options);

    internal static long DirectoryTicks(string path)
    {
        var attrs = File.GetAttributes(path);
        if ((attrs & FileAttributes.Directory) == 0 || PathSafety.IsSpecial(attrs))
            throw new IOException("目录在扫描期间变成了文件、链接或离线项，未进入。");
        return Directory.GetLastWriteTimeUtc(path).Ticks;
    }

    internal static bool IsReadError(Exception error) => error is UnauthorizedAccessException or IOException or System.Security.SecurityException;
    internal static string ReadError(Exception error) => error is UnauthorizedAccessException or System.Security.SecurityException ?
        "没有读取权限。" : "读取失败或目录在扫描期间改变。";

    public static async Task<ScanResult> ScanAsync(string path, int workers, IProgress<ScanProgress>? progress, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var started = Stopwatch.GetTimestamp();
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        PathSafety.RequireOrdinaryPath(full);
        var attrs = File.GetAttributes(full);
        if ((attrs & FileAttributes.Directory) == 0) throw new IOException("请选择磁盘或文件夹。");
        var root = new DiskNode(full, null, 0, 0, attrs);
        var (problems, visited) = await ReadSubtreesAsync([new(full, root)], workers, progress, ct).ConfigureAwait(false);
        return new(root, root.SubtreeTotals!, problems.Sample(), Stopwatch.GetElapsedTime(started), problems, visited);
    }

    // The full scan and refresh share the same bounded worker pool for newly discovered subtrees.
    internal static async Task<(ScanProblems Problems, int Visited)> ReadSubtreesAsync(IReadOnlyList<Pending> seeds, int workers,
        IProgress<ScanProgress>? progress, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var problems = new ScanProblems();
        if (seeds.Count == 0) return (problems, 0);
        var queue = Channel.CreateUnbounded<Pending>(new UnboundedChannelOptions { AllowSynchronousContinuations = false });
        var failed = new ConcurrentQueue<DiskNode>();
        var special = new ConcurrentQueue<DiskNode>();
        long files = 0, directories = seeds.Count, bytes = 0, lastReport = Stopwatch.GetTimestamp();
        int inaccessible = 0, skipped = 0, outstanding = seeds.Count, visited = 0;
        foreach (var seed in seeds) queue.Writer.TryWrite(seed);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var token = stop.Token;

        ScanProgress Snapshot() => new(Interlocked.Read(ref files), Interlocked.Read(ref directories), Interlocked.Read(ref bytes),
            Volatile.Read(ref inaccessible), Volatile.Read(ref skipped));

        void Report()
        {
            if (progress is null) return;
            var previous = Volatile.Read(ref lastReport);
            var now = Stopwatch.GetTimestamp();
            if (now - previous > Stopwatch.Frequency / 7 && Interlocked.CompareExchange(ref lastReport, now, previous) == previous)
                progress.Report(Snapshot());
        }

        async Task Worker()
        {
            try
            {
                await foreach (var pending in queue.Reader.ReadAllAsync(token).ConfigureAwait(false))
                {
                    token.ThrowIfCancellationRequested();
                    var node = pending.Node;
                    long localFiles = 0, localDirs = 0, localBytes = 0;
                    var localSkipped = 0;
                    void Commit()
                    {
                        if (progress is null) return;
                        Interlocked.Add(ref files, localFiles);
                        Interlocked.Add(ref directories, localDirs);
                        Interlocked.Add(ref bytes, localBytes);
                        Interlocked.Add(ref skipped, localSkipped);
                        localFiles = localDirs = localBytes = localSkipped = 0;
                        Report();
                    }
                    try
                    {
                        Interlocked.Increment(ref visited);
                        // Parent enumeration metadata may be stale. Check the leaf again before opening it.
                        node.ModifiedTicks = DirectoryTicks(pending.Path);
                        foreach (var entry in Entries(pending.Path))
                        {
                            token.ThrowIfCancellationRequested();
                            var child = entry.Create(node);
                            (node.Children ??= []).Add(child);
                            if (child.IsUnavailable)
                            {
                                special.Enqueue(child);
                                localSkipped++;
                            }
                            else if (child.IsDirectory)
                            {
                                localDirs++;
                                Interlocked.Increment(ref outstanding);
                                queue.Writer.TryWrite(new(Path.Join(pending.Path, entry.Name), child));
                            }
                            else
                            {
                                localFiles++;
                                localBytes += entry.Length;
                            }
                            if (progress is not null && localFiles + localDirs + localSkipped >= 512) Commit();
                        }
                    }
                    catch (Exception error) when (IsReadError(error))
                    {
                        if (node.Parent is null && node.Children is null)
                            throw new IOException("无法读取所选扫描根目录。请检查权限或磁盘连接。", error);
                        node.ReadFailure = ReadError(error);
                        failed.Enqueue(node);
                        Interlocked.Increment(ref inaccessible);
                    }
                    finally
                    {
                        Commit();
                        if (Interlocked.Decrement(ref outstanding) == 0) queue.Writer.TryComplete();
                    }
                }
            }
            catch
            {
                stop.Cancel();
                throw;
            }
        }

        await Task.WhenAll(Enumerable.Range(0, Math.Clamp(workers, 1, 16)).Select(_ => Task.Run(Worker))).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        var order = new Stack<DiskNode>();
        var pendingDirs = new Stack<DiskNode>(seeds.Select(s => s.Node));
        while (pendingDirs.TryPop(out var current))
        {
            ct.ThrowIfCancellationRequested();
            order.Push(current);
            if (current.Children is null) continue;
            foreach (var child in current.Children)
                if (child.IsDirectory && !child.IsUnavailable) pendingDirs.Push(child);
        }
        while (order.TryPop(out var current))
        {
            var totals = Sum(current.Children, current.ReadFailed, ct);
            SetTotals(current, totals);
        }
        problems.Failed.UnionWith(failed);
        problems.Skipped.UnionWith(special);
        progress?.Report(Snapshot());
        return (problems, visited);
    }

    internal static ScanProgress Sum(List<DiskNode>? children, bool failed, CancellationToken ct,
        Func<DiskNode, ScanProgress?>? updatedTotals = null)
    {
        long files = 0, directories = 1, bytes = 0;
        int inaccessible = failed ? 1 : 0, skipped = 0;
        ulong fingerprint = 0;
        if (children is not null)
        {
            foreach (var child in children)
            {
                ct.ThrowIfCancellationRequested();
                if (child.IsUnavailable) skipped++;
                else if (!child.IsDirectory)
                {
                    files++;
                    bytes += child.Bytes;
                    fingerprint = unchecked(fingerprint + EntryFingerprint(child, (ulong)child.ModifiedTicks, (ulong)child.Bytes));
                }
                else
                {
                    var childTotals = updatedTotals?.Invoke(child) ?? child.SubtreeTotals!;
                    files += childTotals.Files;
                    directories += childTotals.Directories;
                    bytes += childTotals.Bytes;
                    inaccessible += childTotals.Inaccessible;
                    skipped += childTotals.Skipped;
                    fingerprint = unchecked(fingerprint + EntryFingerprint(child, childTotals.Fingerprint, 0));
                }
            }
        }
        return new(files, directories, bytes, inaccessible, skipped, fingerprint);
    }

    // In-process metadata fingerprint (not a content hash). Permanent deletion rechecks the
    // selected subtree before removing anything, including nested names, sizes and timestamps.
    private static ulong EntryFingerprint(DiskNode node, ulong value, ulong length)
    {
        unchecked
        {
            ulong hash = ((ulong)(uint)StringComparer.Ordinal.GetHashCode(node.Name) << 32) | (uint)node.Attributes;
            hash ^= value * 0x9e3779b97f4a7c15UL ^ length * 0x94d049bb133111ebUL;
            hash = (hash ^ (hash >> 30)) * 0xbf58476d1ce4e5b9UL;
            return hash ^ (hash >> 27);
        }
    }

    internal static void SetTotals(DiskNode node, ScanProgress totals)
    {
        node.SubtreeTotals = totals;
        node.Bytes = totals.Bytes;
        node.IsPartial = totals.Inaccessible > 0 || totals.Skipped > 0;
    }
}
