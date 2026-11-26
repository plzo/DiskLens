using System.Diagnostics;

namespace DiskLens;

internal sealed record FileTaskSnapshot(long Id, string Label, string? Target, bool Running, bool Finished,
    string Phase, double? Fraction, TimeSpan Elapsed, TimeSpan? Remaining, double BytesPerSecond,
    long CopiedBytes, int QueuePosition, string Detail);

internal sealed class FileTaskQueue(Action? changed = null, Func<FileActionPlan, Task<FileActionResult>>? execute = null,
    int parallelTasks = 4, Func<FileActionPlan, FileTaskProgress, Task<FileActionResult>>? executeWithProgress = null)
{
    private sealed class Work(long id, FileActionPlan plan)
    {
        public long Id { get; } = id;
        public FileActionPlan Plan { get; } = plan;
        public TaskCompletionSource<FileActionResult> Result { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Running { get; set; }
        public bool Finished { get; set; }
        public Stopwatch Clock { get; } = new();
        public FileProgressFrame Progress { get; set; } = new("等待执行", null, 0);
        public FileTaskEstimate Estimate { get; } = new();
        public string Detail { get; set; } = "";
    }
    private readonly object _gate = new();
    private readonly List<Work> _outstanding = [];
    private readonly Queue<Work> _history = new();
    private int _running;
    private int _parallelTasks = Math.Clamp(parallelTasks, 1, 8);
    private long _nextId;

    public int ParallelTasks
    {
        get { lock (_gate) return _parallelTasks; }
        set
        {
            lock (_gate) { _parallelTasks = Math.Clamp(value, 1, 8); StartReady(); }
            changed?.Invoke();
        }
    }
    public (int Running, int Waiting) Counts
    {
        get { lock (_gate) return (_running, _outstanding.Count - _running); }
    }
    public FileTaskSnapshot[] Snapshot()
    {
        lock (_gate)
        {
            var position = 0;
            return _outstanding.Concat(_history.Reverse()).Select(work =>
            {
                var elapsed = work.Clock.Elapsed;
                var (remaining, speed) = work.Running ? work.Estimate.Get(elapsed) : (null, 0d);
                return new FileTaskSnapshot(work.Id, work.Plan.Label, work.Plan.Target, work.Running, work.Finished,
                    work.Progress.Phase, work.Progress.Fraction, elapsed, remaining, speed, work.Progress.CopiedBytes,
                    !work.Running && !work.Finished ? ++position : 0, work.Detail);
            }).ToArray();
        }
    }

    public Task<FileActionResult> Enqueue(FileActionPlan plan)
    {
        if (plan.Items.Length == 0) throw new IOException("没有需要执行的项目。");
        Work work;
        lock (_gate)
        {
            // No fixed submission cap. Both running and queued claims remain reserved.
            foreach (var existing in _outstanding)
                foreach (var claimed in existing.Plan.Claims)
                    if (plan.Claims.Any(path => FileActions.Overlaps(path, claimed)))
                        throw new IOException($"与尚未完成的任务路径冲突（{existing.Plan.Label}）。请换选其他项或等该任务结束后刷新。\n{claimed}");
            work = new(++_nextId, plan);
            _outstanding.Add(work);
            StartReady();
        }
        changed?.Invoke();
        return work.Result.Task;
    }

    private void StartReady()
    {
        foreach (var work in _outstanding)
        {
            if (_running >= _parallelTasks) break;
            if (work.Running) continue;
            work.Running = true;
            work.Clock.Start();
            work.Progress = new("正在准备", null, 0);
            _running++;
            _ = Task.Run(() => RunAsync(work));
        }
    }

    private async Task RunAsync(Work work)
    {
        FileActionResult? result = null;
        Exception? failure = null;
        var progress = new FileTaskProgress(work.Plan, frame =>
        {
            lock (_gate)
            {
                if (!work.Running) return;
                work.Progress = frame;
                work.Estimate.Report(work.Clock.Elapsed, frame.Fraction, frame.CopiedBytes);
            }
        });
        try
        {
            result = await (executeWithProgress?.Invoke(work.Plan, progress) ?? execute?.Invoke(work.Plan) ??
                (work.Plan.Kind == FileActionKind.Move ? FileWorker.RunAsync(work.Plan, progress) :
                    FileActions.RunAsync(work.Plan, progress: progress))).ConfigureAwait(false);
        }
        catch (Exception error) { failure = error; }
        lock (_gate)
        {
            work.Clock.Stop();
            work.Running = false;
            work.Finished = true;
            var success = failure is null && result is { Aborted: false, Errors.Length: 0 } && result.Completed == work.Plan.Items.Length;
            work.Progress = work.Progress with { Phase = success ? "已完成" : result?.Aborted == true ? "已取消剩余操作" : "已结束 · 有异常",
                Fraction = success ? 1 : work.Progress.Fraction ?? 0 };
            work.Detail = failure is not null ? failure.Message :
                $"完成 {result!.Completed:N0}/{work.Plan.Items.Length:N0} 项 · 跳过同名 {result.Skipped:N0} 项\n" +
                string.Join('\n', result.Errors.Concat((result.Backups ?? []).Select(path => $"原目标备份：{path}")));
            _outstanding.Remove(work);
            _running--;
            _history.Enqueue(work);
            while (_history.Count > 32) _history.Dequeue();
            StartReady();
        }
        if (failure is not null) work.Result.SetException(failure);
        else work.Result.SetResult(result!);
        changed?.Invoke();
    }
}
