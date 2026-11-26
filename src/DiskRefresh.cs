using System.Diagnostics;

namespace DiskLens;

// Build the delta without touching the live tree. Only dirty directories are enumerated on disk;
// clean siblings contribute their cached totals, and are not walked recursively.
internal sealed class DiskRefresh
{
    private sealed class Branch
    {
        public bool Read;
        public bool Replace;
        public Dictionary<string, Branch>? Children;
        public Branch Child(string name)
        {
            Children ??= new(StringComparer.OrdinalIgnoreCase);
            if (!Children.TryGetValue(name, out var child)) Children.Add(name, child = new());
            return child;
        }
        public Branch? Get(string name) => Children?.GetValueOrDefault(name);
    }

    private sealed class Patch(DiskNode node, List<DiskNode>? children, long ticks, string? failure)
    {
        public DiskNode Node { get; } = node;
        public List<DiskNode>? Children { get; } = children;
        public long Ticks { get; } = ticks;
        public string? Failure { get; } = failure;
        public ScanProgress Totals { get; set; } = null!;
    }

    private readonly Dictionary<DiskNode, Patch> _patches = [];
    private readonly HashSet<DiskNode> _removed = [];
    private readonly ScanProblems _added;
    private readonly ScanResult _result;
    public ScanChanges.Batch Batch { get; }
    public long ObservedRootTicks { get; }
    public bool Changed => _patches.Count > 0;
    public ScanProgress Totals => _result.Totals;
    public int VisitedDirectories => _result.VisitedDirectories;
    public TimeSpan Elapsed => _result.Elapsed;

    private DiskRefresh(ScanChanges.Batch batch, Dictionary<DiskNode, Patch> patches, HashSet<DiskNode> removed,
        ScanProblems added, ScanResult result, long observedRootTicks)
    { Batch = batch; _patches = patches; _removed = removed; _added = added; _result = result; ObservedRootTicks = observedRootTicks; }

    public static async Task<DiskRefresh> PrepareAsync(ScanResult previous, ScanChanges.Batch batch, long observedRootTicks, bool rootChanged, int workers,
        CancellationToken ct)
    {
        var started = Stopwatch.GetTimestamp();
        var root = previous.Root;
        var dirty = new Branch { Read = rootChanged || root.ReadFailed };
        foreach (var (relative, change) in batch.Paths)
        {
            ct.ThrowIfCancellationRequested();
            if (relative == ".") { dirty.Read = true; continue; }
            var parts = relative.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0 || parts.Any(p => p is "." or "..")) continue;
            var branch = dirty;
            for (var i = 0; i < parts.Length - 1; i++) branch = branch.Child(parts[i]);
            branch.Read = true; // An entry was created, removed or changed in this directory.
            var leaf = branch.Child(parts[^1]);
            leaf.Read = true; // If it is itself a directory, e.g. its ACL may have changed.
            leaf.Replace |= change.Replaced;
        }
        foreach (var failure in previous.Problems.Failed)
        {
            ct.ThrowIfCancellationRequested();
            var names = new Stack<string>();
            for (var node = failure; node.Parent is not null; node = node.Parent) names.Push(node.Name);
            var branch = dirty;
            foreach (var name in names) branch = branch.Child(name);
            branch.Read = true;
        }

        var work = new Stack<(DiskNode Node, string Path, Branch? Branch, bool Read)>();
        if (dirty.Read || dirty.Children is not null) work.Push((root, root.FullPath, dirty, dirty.Read));
        var patches = new Dictionary<DiskNode, Patch>();
        var order = new Stack<Patch>();
        var seeds = new List<DiskScanner.Pending>();
        var removed = new HashSet<DiskNode>();
        var newSpecial = new List<DiskNode>();
        int visited = 0;

        void Discard(DiskNode node)
        {
            // Only prune the issue index of removed subtrees; clean branches need no walk.
            var pending = new Stack<DiskNode>();
            pending.Push(node);
            while (pending.TryPop(out var old))
            {
                ct.ThrowIfCancellationRequested();
                if (old.ReadFailed || old.IsUnavailable) removed.Add(old);
                if (old.IsPartial && old.Children is { } children)
                    foreach (var child in children)
                        if (child.IsPartial || child.IsUnavailable) pending.Push(child);
            }
        }

        while (work.TryPop(out var next))
        {
            ct.ThrowIfCancellationRequested();
            var (node, path, branch, read) = next;
            var children = node.Children;
            var ticks = node.ModifiedTicks;
            var failure = node.ReadFailure;
            if (read)
            {
                visited++;
                var before = node.Children?.ToDictionary(n => n.Name, StringComparer.Ordinal);
                children = null;
                failure = null;
                try
                {
                    // A path from the cache may now have a different ancestor (junction, etc.).
                    PathSafety.RequireOrdinaryPath(path);
                    ticks = DiskScanner.DirectoryTicks(path);
                    foreach (var entry in DiskScanner.Entries(path))
                    {
                        ct.ThrowIfCancellationRequested();
                        var childBranch = branch?.Get(entry.Name);
                        DiskNode? old = null;
                        before?.Remove(entry.Name, out old);
                        if (!node.ReadFailed && childBranch?.Replace != true && old is not null && old.Attributes == entry.Attributes &&
                            (old.IsDirectory && !old.IsUnavailable || old.ModifiedTicks == entry.ModifiedTicks && (old.IsUnavailable || old.Bytes == entry.Length)))
                        {
                            (children ??= []).Add(old);
                            if (old.IsDirectory && !old.IsUnavailable)
                            {
                                var reread = childBranch?.Read == true || old.ReadFailed;
                                if (reread || childBranch?.Children is not null)
                                    work.Push((old, Path.Join(path, old.Name), childBranch, reread));
                            }
                            continue;
                        }
                        if (old is not null && (old.IsUnavailable || old.IsPartial)) Discard(old);
                        var child = entry.Create(node);
                        (children ??= []).Add(child);
                        if (child.IsUnavailable) newSpecial.Add(child);
                        else if (child.IsDirectory) seeds.Add(new(Path.Join(path, child.Name), child));
                    }
                }
                catch (Exception error) when (DiskScanner.IsReadError(error))
                {
                    if (node.Parent is null && children is null) throw new IOException("无法刷新扫描根目录，请检查权限或磁盘连接。", error);
                    failure = DiskScanner.ReadError(error);
                }
                if (before is not null)
                    foreach (var old in before.Values)
                        if (old.IsUnavailable || old.IsPartial) Discard(old);
            }
            else if (children is not null && branch?.Children is not null)
            {
                foreach (var child in children)
                    if (child.IsDirectory && !child.IsUnavailable && branch.Get(child.Name) is { } change)
                        work.Push((child, Path.Join(path, child.Name), change, change.Read));
            }
            var patch = new Patch(node, children, ticks, failure);
            patches.Add(node, patch);
            order.Push(patch);
        }

        var (added, subtreeVisits) = await DiskScanner.ReadSubtreesAsync(seeds, workers, null, ct).ConfigureAwait(false);
        added.Skipped.UnionWith(newSpecial);
        while (order.TryPop(out var patch))
        {
            ct.ThrowIfCancellationRequested();
            patch.Totals = DiskScanner.Sum(patch.Children, patch.Failure is not null, ct, child => patches.GetValueOrDefault(child)?.Totals);
            if (patch.Failure is not null) added.Failed.Add(patch.Node);
            else if (patch.Node.ReadFailed) removed.Add(patch.Node);
        }
        var totals = patches.GetValueOrDefault(root)?.Totals ?? previous.Totals;
        var problems = previous.Problems;
        var issues = added.Failed.Concat(problems.Failed.Where(n => !removed.Contains(n) && !added.Failed.Contains(n)))
            .Select(n => new ScanIssue(n.FullPath, patches.GetValueOrDefault(n)?.Failure ?? n.ReadFailure!))
            .Concat(added.Skipped.Concat(problems.Skipped.Where(n => !removed.Contains(n)))
                .Select(n => new ScanIssue(n.FullPath, ScanProblems.SkippedReason))).Take(32).ToArray();
        return new(batch, patches, removed, added,
            new(root, totals, issues, Stopwatch.GetElapsedTime(started), problems, visited + subtreeVisits), observedRootTicks);
    }

    public (DiskNode Folder, DiskNode[] Rows) View(DiskNode previous, CancellationToken ct)
    {
        var names = new Stack<string>();
        for (var node = previous; node.Parent is not null; node = node.Parent) names.Push(node.Name);
        var folder = _result.Root;
        List<DiskNode>? Children(DiskNode node) => _patches.TryGetValue(node, out var patch) ? patch.Children : node.Children;
        foreach (var name in names)
        {
            ct.ThrowIfCancellationRequested();
            var child = Children(folder)?.Find(n => n.Name == name && n.IsDirectory && !n.IsUnavailable);
            if (child is null || Children(child) is null && (_patches.TryGetValue(child, out var p) ? p.Failure is not null : child.ReadFailed)) break;
            folder = child;
        }
        var rows = Children(folder)?.ToArray() ?? [];
        long Bytes(DiskNode node) => _patches.GetValueOrDefault(node)?.Totals.Bytes ?? node.Bytes;
        Array.Sort(rows, (a, b) => Bytes(b).CompareTo(Bytes(a)) is var size && size != 0 ? size :
            StringComparer.OrdinalIgnoreCase.Compare(a.Name, b.Name));
        ct.ThrowIfCancellationRequested();
        return (folder, rows);
    }

    public ScanResult Apply(ScanResult previous)
    {
        foreach (var patch in _patches.Values)
        {
            patch.Node.Children = patch.Children;
            patch.Node.ModifiedTicks = patch.Ticks;
            patch.Node.ReadFailure = patch.Failure;
            DiskScanner.SetTotals(patch.Node, patch.Totals);
        }
        previous.Problems.Failed.ExceptWith(_removed);
        previous.Problems.Skipped.ExceptWith(_removed);
        previous.Problems.Failed.UnionWith(_added.Failed);
        previous.Problems.Skipped.UnionWith(_added.Skipped);
        return _result;
    }
}
