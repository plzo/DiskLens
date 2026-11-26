using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace DiskLens;

public partial class FileTasksWindow : Window
{
    private readonly FileTaskQueue _queue;
    private readonly Func<string> _history;
    private readonly ObservableCollection<FileTaskRow> _rows = [];
    private readonly DispatcherTimer _timer;

    internal FileTasksWindow(Window owner, FileTaskQueue queue, Func<string> history)
    {
        _queue = queue;
        _history = history;
        Owner = owner;
        InitializeComponent();
        Chrome.Attach(this);
        TaskList.ItemsSource = _rows;
        ParallelBox.ItemsSource = new[] { 1, 2, 4, 8 };
        ParallelBox.SelectedItem = queue.ParallelTasks;
        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Background,
            (_, _) => Refresh(), Dispatcher);
        Closed += (_, _) => _timer.Stop();
        Refresh();
    }

    private void Refresh()
    {
        var snapshots = _queue.Snapshot();
        var existing = _rows.ToDictionary(row => row.Id);
        var ids = snapshots.Select(task => task.Id).ToHashSet();
        for (var i = _rows.Count - 1; i >= 0; i--) if (!ids.Contains(_rows[i].Id)) _rows.RemoveAt(i);
        for (var i = 0; i < snapshots.Length; i++)
        {
            var snapshot = snapshots[i];
            if (!existing.TryGetValue(snapshot.Id, out var row))
            {
                row = new(snapshot);
                _rows.Insert(i, row);
            }
            else
            {
                if (!ReferenceEquals(_rows[i], row)) _rows.Move(_rows.IndexOf(row), i);
                row.Update(snapshot);
            }
        }
        var (running, waiting) = _queue.Counts;
        CountsText.Text = $"执行中 {running:N0} · 排队 {waiting:N0} · 保留最近 32 项结果";
        EmptyText.Visibility = snapshots.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Parallel_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (ParallelBox.SelectedItem is int count) _queue.ParallelTasks = count;
    }

    private void History_Click(object sender, RoutedEventArgs e) =>
        Sheet.Inform(this, "提交记录", "包括未启动、取消和最近完成的文件任务。", _history(), copy: true);

    private void Details_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: FileTaskRow row })
            Sheet.Inform(this, row.Label, row.Phase, (row.Target.Length == 0 ? "" : row.Target + "\n\n") +
                row.Timing + "\n" + row.Transfer + "\n\n" + row.Detail, copy: true);
    }
}

internal sealed class FileTaskRow(FileTaskSnapshot snapshot) : INotifyPropertyChanged
{
    private FileTaskSnapshot _snapshot = snapshot;
    public long Id => _snapshot.Id;
    public string Label => $"#{Id}  {_snapshot.Label}";
    public string Target => _snapshot.Target is { } target ? $"目标：{target}" : "";
    public string Phase => _snapshot.QueuePosition > 0 ? $"排队中 · 第 {_snapshot.QueuePosition} 位" : _snapshot.Phase;
    public bool Indeterminate => _snapshot.Running && _snapshot.Fraction is null;
    public double Percent => (_snapshot.Fraction ?? 0) * 100;
    public string Percentage => _snapshot.Fraction is { } value ? $"{value * 100:0.#}%" : _snapshot.Running ? "统计中" : "等待中";
    public string Timing => _snapshot.Finished ? $"实际用时 {Duration(_snapshot.Elapsed)}" :
        !_snapshot.Running ? "等待前序任务 · 开始后估算耗时" :
        _snapshot.Remaining is { } remaining
            ? $"已用 {Duration(_snapshot.Elapsed)} · 预计剩余约 {Duration(remaining)} · 预计总耗时约 {Duration(_snapshot.Elapsed + remaining)}"
            : $"已用 {Duration(_snapshot.Elapsed)} · 剩余时间正在估算…";
    public string Transfer => _snapshot.CopiedBytes > 0 ? $"已复制 {Sizes.Format(_snapshot.CopiedBytes)}" +
        (_snapshot.BytesPerSecond > 0 ? $" · 近期约 {Sizes.Format((long)_snapshot.BytesPerSecond)}/秒" : "") : "";
    public string Detail => _snapshot.Detail.Length == 0 ? "任务仍在处理，尚无最终结果。" : _snapshot.Detail;
    public event PropertyChangedEventHandler? PropertyChanged;
    public void Update(FileTaskSnapshot value)
    {
        if (_snapshot == value) return;
        _snapshot = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(""));
    }
    private static string Duration(TimeSpan time) => time.TotalSeconds < 1 ? "不到 1 秒" :
        time.TotalMinutes < 1 ? $"{Math.Ceiling(time.TotalSeconds):0} 秒" :
        time.TotalHours < 1 ? $"{(int)time.TotalMinutes} 分 {time.Seconds} 秒" :
        $"{(int)time.TotalHours} 小时 {time.Minutes} 分";
}
