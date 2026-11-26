using System.Diagnostics;

namespace DiskLens;

internal sealed record FileProgressFrame(string Phase, double? Fraction, long CopiedBytes);

// Synchronous, bounded-rate reports: never post a dispatcher callback per file or copy chunk.
internal sealed class FileTaskProgress
{
    private readonly object _gate = new();
    private readonly Action<FileProgressFrame> _report;
    private readonly double[] _weights, _fractions;
    private readonly long[] _bytes;
    private long _lastReport;

    public FileTaskProgress(FileActionPlan plan, Action<FileProgressFrame> report)
    {
        _report = report;
        _weights = plan.Items.Select(item => plan.Kind == FileActionKind.Move && item.CrossVolume
            ? Math.Max(1d, item.Snapshot.Bytes + 65536d * (item.Snapshot.FolderTotals?.Files ?? 1)) : 1d).ToArray();
        _fractions = new double[_weights.Length];
        _bytes = new long[_weights.Length];
    }

    public ItemProgress Item(int index) => new(this, index);
    internal void AcceptFrame(FileProgressFrame frame) => _report(frame);
    internal sealed class ItemProgress(FileTaskProgress owner, int index)
    {
        public void Report(string phase, double? fraction = null, long copiedBytes = 0) =>
            owner.Report(index, phase, fraction, copiedBytes);
        public void Complete() => Report("完成选中项", 1);
    }

    private void Report(int index, string phase, double? fraction, long bytes)
    {
        lock (_gate)
        {
            if (fraction is { } value && double.IsFinite(value))
                _fractions[index] = Math.Max(_fractions[index], Math.Clamp(value, 0, 1));
            _bytes[index] = Math.Max(_bytes[index], bytes);
            var now = Stopwatch.GetTimestamp();
            if (now - _lastReport < Stopwatch.Frequency / 8 && fraction != 1) return;
            _lastReport = now;
            var weight = _weights.Sum();
            var overall = weight == 0 ? 0 : _weights.Select((w, i) => w * _fractions[i]).Sum() / weight;
            _report(new(phase, fraction is null ? null : Math.Min(0.995, overall), _bytes.Sum()));
        }
    }
}

// Queue wait is excluded; unknown work or stalled progress invalidates the estimate.
internal sealed class FileTaskEstimate
{
    private readonly Queue<(double Time, double Fraction, long Bytes)> _samples = new();
    public void Report(TimeSpan elapsed, double? fraction, long bytes)
    {
        if (fraction is null) { _samples.Clear(); return; }
        var seconds = elapsed.TotalSeconds;
        if (_samples.TryPeek(out var first) && seconds < first.Time) _samples.Clear();
        if (_samples.Count == 0 || fraction > _samples.Last().Fraction) _samples.Enqueue((seconds, fraction.Value, bytes));
        while (_samples.Count > 2 && seconds - _samples.Peek().Time > 8) _samples.Dequeue();
    }

    public (TimeSpan? Remaining, double BytesPerSecond) Get(TimeSpan elapsed)
    {
        if (_samples.Count < 2) return (null, 0);
        var first = _samples.Peek();
        var last = _samples.Last();
        var seconds = last.Time - first.Time;
        var advance = last.Fraction - first.Fraction;
        if (seconds < 1 || advance <= 0 || elapsed.TotalSeconds - last.Time > 5) return (null, 0);
        var remaining = (1 - last.Fraction) * seconds / advance;
        if (!double.IsFinite(remaining) || remaining > TimeSpan.FromDays(365).TotalSeconds) return (null, 0);
        return (TimeSpan.FromSeconds(Math.Max(0, remaining)), Math.Max(0, last.Bytes - first.Bytes) / seconds);
    }
}
