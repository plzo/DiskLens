using System.ComponentModel;

namespace DiskLens;

internal sealed class ScanSession : IDisposable
{
    private readonly ScanChanges _changes;
    private readonly PathSafety.FileIdentity? _rootIdentity;
    private long _observedRootTicks;
    public ScanResult Result { get; private set; }

    private ScanSession(ScanResult result, ScanChanges changes, PathSafety.DirectoryStamp? rootStamp)
    { Result = result; _changes = changes; _rootIdentity = rootStamp?.Identity; _observedRootTicks = rootStamp?.ModifiedTicks ?? 0; }

    public static async Task<ScanSession> OpenAsync(string path, int workers, IProgress<ScanProgress>? progress, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        var stamp = PathSafety.TryDirectoryStamp(root);
        var identity = stamp?.Identity;
        string? canonical = null;
        if (identity is not null)
        {
            try { canonical = PathSafety.CanonicalLocal(root, out _, out _); }
            catch (Exception error) when (DiskScanner.IsReadError(error) || error is Win32Exception) { }
        }
        // Start before enumeration so changes during the baseline scan remain pending for refresh.
        var changes = new ScanChanges(root, canonical, identity is not null);
        try
        {
            var result = await DiskScanner.ScanAsync(root, workers, progress, ct).ConfigureAwait(false);
            if (identity is not null && identity != PathSafety.TryDirectoryStamp(root)?.Identity)
                throw new IOException("扫描期间根目录已改变，请重新选择扫描范围。");
            return new(result, changes, stamp);
        }
        catch { changes.Dispose(); throw; }
    }

    // Null means the baseline can no longer be trusted; the caller should open a new full scan.
    public async Task<DiskRefresh?> PrepareRefreshAsync(int workers, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var batch = _changes.Capture();
        if (batch is null || PathSafety.TryDirectoryStamp(Result.Root.FullPath) is not { } stamp ||
            stamp.Identity != _rootIdentity || stamp.Attributes != Result.Root.Attributes) return null;
        var delta = await DiskRefresh.PrepareAsync(Result, batch, stamp.ModifiedTicks, stamp.ModifiedTicks != _observedRootTicks, workers, ct)
            .ConfigureAwait(false);
        if (!_changes.IsReliable || PathSafety.TryDirectoryStamp(Result.Root.FullPath) is not { } latest ||
            latest.Identity != stamp.Identity || latest.Attributes != stamp.Attributes) return null;
        return delta;
    }

    // Called on the owner thread, with no other readers of the mutable tree.
    public bool TryCommit(DiskRefresh delta)
    {
        if (!_changes.IsReliable) return false;
        Result = delta.Apply(Result);
        _observedRootTicks = delta.ObservedRootTicks;
        _changes.Acknowledge(delta.Batch);
        return true;
    }

    public bool MarkChanged(string path, bool replaced = true) => _changes.MarkAbsolute(path, replaced);
    public void Dispose() => _changes.Dispose();
}

internal sealed class ScanChanges : IDisposable
{
    internal const int Limit = 8192;
    internal readonly record struct Change(bool Replaced, long Version);
    internal sealed record Batch(KeyValuePair<string, Change>[] Paths);
    private readonly object _gate = new();
    private readonly string _root;
    private readonly string? _canonical;
    private readonly Dictionary<string, Change> _pending = new(StringComparer.OrdinalIgnoreCase);
    private FileSystemWatcher? _watcher;
    private long _version;
    private bool _lost;

    public ScanChanges(string root, string? canonical, bool watch)
    {
        _root = root;
        _canonical = canonical;
        if (!watch) { LoseTracking(); return; }
        try
        {
            _watcher = new FileSystemWatcher(root)
            {
                IncludeSubdirectories = true, InternalBufferSize = 64 * 1024,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.Attributes |
                    NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.CreationTime | NotifyFilters.Security
            };
            _watcher.Changed += (_, e) => Record(e.Name, false);
            _watcher.Created += (_, e) => Record(e.Name, true);
            _watcher.Deleted += (_, e) => Record(e.Name, true);
            _watcher.Renamed += (_, e) => { Record(e.OldName, true); Record(e.Name, true); };
            _watcher.Error += (_, _) => LoseTracking();
            _watcher.EnableRaisingEvents = true;
        }
        catch (Exception error) when (DiskScanner.IsReadError(error) || error is Win32Exception or ArgumentException or PlatformNotSupportedException)
        {
            _watcher?.Dispose();
            _watcher = null;
            LoseTracking();
        }
    }

    public bool IsReliable { get { lock (_gate) return !_lost; } }

    public Batch? Capture()
    {
        lock (_gate) return _lost ? null : new(_pending.ToArray());
    }

    public void Acknowledge(Batch batch)
    {
        lock (_gate)
            foreach (var (path, saved) in batch.Paths)
                if (_pending.TryGetValue(path, out var current) && saved.Version == current.Version) _pending.Remove(path);
    }

    public bool MarkAbsolute(string path, bool replaced)
    {
        var scope = PathSafety.Within(path, _root) ? _root : _canonical;
        if (scope is not null && PathSafety.Within(path, scope))
        {
            Record(Path.GetRelativePath(scope, path), replaced);
            return true;
        }
        if (PathSafety.Within(_root, path) || _canonical is not null && PathSafety.Within(_canonical, path))
        {
            if (replaced) LoseTracking();
            return true;
        }
        return false;
    }

    private void Record(string? path, bool replaced)
    {
        // The OS reports paths relative to the watched root. Never walk out of that root.
        if (string.IsNullOrEmpty(path) || Path.IsPathRooted(path)) { LoseTracking(); return; }
        lock (_gate)
        {
            if (_lost) return;
            if (_pending.TryGetValue(path, out var before)) _pending[path] = new(replaced || before.Replaced, ++_version);
            else if (_pending.Count < Limit) _pending.Add(path, new(replaced, ++_version));
            else LoseTracking();
        }
    }

    private void LoseTracking()
    {
        lock (_gate) { _lost = true; _pending.Clear(); }
    }

    public void Dispose() { LoseTracking(); _watcher?.Dispose(); }
}
