using System.ComponentModel;
using System.Diagnostics;
using System.Security.Principal;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

using Microsoft.Win32;

namespace DiskLens;

public partial class MainWindow : Window
{
    private enum ViewMode { Folder, Search, Duplicates }
    private sealed record DriveCard(string Root, string Letter, string Label, long Free, long Total)
    {
        public double UsedPercent => Total == 0 ? 0 : Math.Clamp((Total - Free) * 100d / Total, 0, 100);
        public string Caption => $"{Sizes.Format(Free)} 可用 / {Sizes.Format(Total)}";
    }

    private readonly SettingsStore _store = SettingsStore.ForCurrentUser();
    private ProviderSettings _provider = new();
    private readonly bool _interactive;
    private sealed class ScanScope(ScanSession scan, DriveCard? drive)
    {
        public ScanSession Scan { get; } = scan;
        public DriveCard? Drive { get; set; } = drive;
        public DiskNode? Folder { get; set; }
        public long FileRevision { get; set; }
        public bool NeedsRefresh { get; set; }
    }

    private readonly Dictionary<string, ScanScope> _scopes = new(StringComparer.OrdinalIgnoreCase);
    private ScanScope? _scope;
    private ScanSession? _scan => _scope?.Scan;
    private ScanResult? _result => _scan?.Result;
    private readonly FileTaskQueue _fileTasks;
    private FileTasksWindow? _tasksWindow;
    private readonly Queue<string> _fileHistory = new();
    private int _fileActionAwaiters;
    private long _fileRevision;
    private DiskNode? _directory;
    private ViewMode _view;
    private IEntryRow[] _rows = [];
    private Dictionary<DiskNode, string> _duplicateGroups = [];
    private CancellationTokenSource? _job;
    private bool _cancellable;

    public MainWindow() : this(true) { }
    internal MainWindow(bool interactive, Func<FileActionPlan, Task<FileActionResult>>? executeFileAction = null,
        Func<FileActionPlan, FileTaskProgress, Task<FileActionResult>>? executeWithProgress = null)
    {
        _interactive = interactive;
        _fileTasks = new(() => { if (!Dispatcher.HasShutdownStarted) _ = Dispatcher.InvokeAsync(UpdateActions); },
            executeFileAction, executeWithProgress: executeWithProgress);
        InitializeComponent();
        Closed += (_, _) =>
        {
            foreach (var scope in _scopes.Values) scope.Scan.Dispose();
            _scopes.Clear();
        };
        Chrome.Attach(this);
        SpaceMap.EntryClicked += async node =>
        {
            if (_job is not null) return;
            if (node.IsDirectory) await NavigateAsync(node);
            else
            {
                if (_view != ViewMode.Folder && _directory is not null) await NavigateAsync(_directory);
                var row = _rows.FirstOrDefault(r => ReferenceEquals(r.Item, node));
                if (row is null) return;
                Entries.SelectedItem = row;
                Entries.ScrollIntoView(row);
                Entries.Focus();
            }
        };
        if (interactive)
        {
            Width = Math.Min(Width, SystemParameters.WorkArea.Width);
            Height = Math.Min(Height, SystemParameters.WorkArea.Height);
            _provider = _store.Load(out var notice);
            if (notice is not null) StatusText.Text = notice;
            Loaded += async (_, _) => await RefreshDrivesAsync();
        }
        else
        {
            DriveList.ItemsSource = new DriveCard[] { new(@"C:\", "C:", "示例系统盘", 93L << 30, 256L << 30),
                new(@"D:\", "D:", "示例工作盘", 416L << 30, 1024L << 30), new(@"E:\", "E:", "示例归档盘", 768L << 30, 1024L << 30) };
        }
        UpdateActions();
    }

    private static int ScanWorkers => Math.Clamp(Environment.ProcessorCount, 1, 4);

    internal Task<bool> BrowseAsync(string path) => RunJobAsync("正在列出文件（不扫描）…", async ct =>
    {
        var (folder, rows) = await Task.Run(() =>
        {
            var current = FolderBrowser.Read(path, ct);
            var entries = current.Children!.OrderByDescending(node => node.IsDirectory)
                .ThenBy(node => node.Name, StringComparer.OrdinalIgnoreCase).ToArray();
            ct.ThrowIfCancellationRequested();
            return (current, entries);
        }, ct);
        var drive = await ReadDriveAsync(folder, ct);
        _scope = null;
        _duplicateGroups.Clear();
        UpdateMetrics(drive);
        DisplayDirectory(folder, rows);
        ScopeText.Text = "直接浏览 · 未扫描占用大小";
        StatusText.Text = $"当前目录 {rows.Length:N0} 项 · 可直接多选并移动；点击“扫描当前范围”才统计空间";
    });

    internal Task<bool> ScanAsync(string path) => RunJobAsync("正在打开扫描范围…", async ct =>
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (!_scopes.TryGetValue(root, out var cached))
        {
            await FullScanAsync(root, null, false, ct);
            return;
        }
        var folder = cached.Folder ?? cached.Scan.Result.Root;
        var rows = await Task.Run(() => DiskQuery.Sorted(folder.Children ?? [], ct), ct);
        _scope = cached;
        _duplicateGroups.Clear();
        UpdateMetrics(cached.Drive);
        DisplayDirectory(folder, rows);
        StatusText.Text = "已恢复缓存 · 如需更新请按 F5" + Incomplete(cached.Scan.Result);
    });

    private async Task FullScanAsync(string path, DiskNode? previousFolder, bool fallback, CancellationToken ct)
    {
        var scanning = true;
        var progress = new Progress<ScanProgress>(p =>
        {
            if (!scanning || ct.IsCancellationRequested) return;
            var label = fallback ? "增量记录不可用，正在全量重扫" : "扫描中";
            StatusText.Text = $"{label} · {p.Files:N0} 文件 / {p.Directories:N0} 目录 · {Sizes.Format(p.Bytes)}";
        });
        ScanSession? next = null;
        var revision = _fileRevision;
        try
        {
            next = await Task.Run(() => ScanSession.OpenAsync(path, ScanWorkers, progress, ct), ct);
            var result = next.Result;
            var (folder, rows) = await Task.Run(() =>
            {
                var target = result.Root;
                var names = new Stack<string>();
                for (var n = previousFolder; n?.Parent is not null; n = n.Parent) names.Push(n.Name);
                foreach (var name in names)
                {
                    ct.ThrowIfCancellationRequested();
                    var child = target.Children?.Find(n => n.Name == name && n.IsDirectory && !n.IsUnmeasurable);
                    if (child is null) break;
                    target = child;
                }
                return (target, DiskQuery.Sorted(target.Children ?? [], ct));
            }, ct);
            var drive = await ReadDriveAsync(result.Root, ct);
            ct.ThrowIfCancellationRequested();
            _scopes.TryGetValue(result.Root.FullPath, out var old);
            _scope = new ScanScope(next, drive) { NeedsRefresh = _fileRevision != revision };
            _scopes[result.Root.FullPath] = _scope;
            next = null;
            old?.Scan.Dispose();
            _duplicateGroups.Clear();
            UpdateMetrics(drive);
            DisplayDirectory(folder, rows);
            var label = fallback ? "增量记录不可用，已全量重扫" : "扫描完成";
            StatusText.Text = $"{label} · {result.Elapsed.TotalSeconds:0.00} 秒" + Incomplete(result);
        }
        finally { scanning = false; next?.Dispose(); }
    }

    private Task<DriveCard?> ReadDriveAsync(DiskNode root, CancellationToken ct) => _interactive ?
        Task.Run(() => DriveAt(Path.GetPathRoot(root.FullPath)!), ct) : Task.FromResult<DriveCard?>(((DriveCard[])DriveList.ItemsSource)[1]);

    private static string Incomplete(ScanResult result)
    {
        var skipped = result.Totals.Skipped + result.Totals.Inaccessible;
        return skipped > 0 ? $" · {skipped:N0} 项未完整读取" : "";
    }

    internal Task<bool> RefreshScanAsync() => RunJobAsync("正在增量刷新…", async ct =>
    {
        if (_scope is not { } scope) return;
        var scan = scope.Scan;
        var folder = _directory ?? scan.Result.Root;
        var revision = scope.FileRevision;
        var delta = await Task.Run(() => scan.PrepareRefreshAsync(ScanWorkers, ct), ct);
        if (delta is null)
        {
            StatusText.Text = "增量记录不可用，正在全量重扫…";
            await FullScanAsync(scan.Result.Root.FullPath, folder, true, ct);
            return;
        }
        var view = delta.Changed ? await Task.Run(() => delta.View(folder, ct), ct) : default;
        var drive = await ReadDriveAsync(scan.Result.Root, ct);
        ct.ThrowIfCancellationRequested();
        if (!scan.TryCommit(delta))
        {
            StatusText.Text = "增量记录不可用，正在全量重扫…";
            await FullScanAsync(scan.Result.Root.FullPath, folder, true, ct);
            return;
        }
        scope.NeedsRefresh = scope.FileRevision != revision;
        scope.Drive = drive;
        UpdateMetrics(drive);
        if (delta.Changed)
        {
            _duplicateGroups.Clear();
            DisplayDirectory(view.Folder!, view.Rows!);
        }
        var message = delta.VisitedDirectories == 0 ? "未发现变更 · 复用扫描结果" : $"增量刷新 · 重读 {delta.VisitedDirectories:N0} 个目录";
        StatusText.Text = $"{message} · {delta.Elapsed.TotalSeconds:0.00} 秒" + Incomplete(scan.Result);
    });

    internal async Task NavigateAsync(DiskNode node)
    {
        if (node.IsUnavailable || node.ReadFailed && node.Children is null) return;
        if (!node.IsSizeKnown) { await BrowseAsync(node.FullPath); return; }
        await RunJobAsync("正在打开目录…", async ct =>
        {
            var rows = await Task.Run(() => DiskQuery.Sorted(node.Children ?? [], ct), ct);
            DisplayDirectory(node, rows);
            StatusText.Text = $"当前目录 · {rows.Length:N0} 项 · {node.SizeText}";
        });
    }

    private void DisplayDirectory(DiskNode node, DiskNode[] rows)
    {
        _directory = node;
        if (_scope is not null) _scope.Folder = node;
        PathBox.Text = node.FullPath;
        SpaceMap.SetFolder(node, node.IsSizeKnown ? rows : []);
        SetView(rows, ViewMode.Folder, $"文件列表 · {rows.Length:N0}", "此目录没有可显示的条目");
        SizeColumn.SortDirection = ListSortDirection.Descending;
    }

    private void SetView(IEntryRow[] rows, ViewMode view, string label, string empty)
    {
        _rows = rows;
        _view = view;
        ViewLabel.Text = label;
        EmptyLabel.Text = empty;
        EmptyLabel.Visibility = rows.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        PathColumn.Visibility = view != ViewMode.Folder ? Visibility.Visible : Visibility.Collapsed;
        GroupColumn.Visibility = view == ViewMode.Duplicates ? Visibility.Visible : Visibility.Collapsed;
        BackToFolderButton.Visibility = view == ViewMode.Folder ? Visibility.Collapsed : Visibility.Visible;
        foreach (var column in Entries.Columns) column.SortDirection = null;
        Entries.ItemsSource = rows;
        UpdateActions();
    }

    private void UpdateMetrics(DriveCard? drive)
    {
        var total = _result?.Totals;
        MetricSize.Text = total is null ? "—" : Sizes.Format(total.Bytes);
        MetricSizeDetail.Text = total is { Inaccessible: > 0 } or { Skipped: > 0 } ? "逻辑大小 · 部分位置未计入" : "文件逻辑大小之和";
        MetricFiles.Text = total is null ? "—" : $"{total.Files:N0}";
        MetricFileDetail.Text = total is null ? "文件夹数将同时统计" : $"另有 {Math.Max(0, total.Directories - 1):N0} 个文件夹（不含根）";
        MetricFree.Text = drive is null ? "—" : Sizes.Format(drive.Free);
        MetricFreeDetail.Text = drive is null ? "来自 Windows 容量统计" : $"{drive.Letter}  总计 {Sizes.Format(drive.Total)} · 已用 {drive.UsedPercent:0.#}%";
        ScopeText.Text = _result is null ? "选择一个磁盘或文件夹，查看空间分布。" : $"扫描范围  {_result.Root.FullPath}";
        ScopeText.ToolTip = ScopeText.Text;
        if (_interactive && drive is not null && DriveList.ItemsSource is DriveCard[] drives)
        {
            var index = Array.FindIndex(drives, item => string.Equals(item.Root, drive.Root, StringComparison.OrdinalIgnoreCase));
            if (index >= 0 && drives[index] != drive)
            {
                var updated = (DriveCard[])drives.Clone();
                updated[index] = drive;
                DriveList.ItemsSource = updated;
            }
        }
    }

    private async Task<bool> RunJobAsync(string status, Func<CancellationToken, Task> execute, bool cancellable = true)
    {
        if (_job is not null) return false;
        using var job = new CancellationTokenSource();
        _job = job;
        _cancellable = cancellable;
        StatusText.Text = status;
        UpdateActions();
        try { await execute(job.Token); return true; }
        catch (OperationCanceledException) when (job.IsCancellationRequested)
        {
            StatusText.Text = "任务已取消 · 未提交新的扫描结果";
            return false;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or Win32Exception or ArgumentException or
            System.Runtime.InteropServices.COMException or System.Security.SecurityException)
        {
            StatusText.Text = "操作未完成 · " + e.Message;
            if (_interactive) Sheet.Inform(this, "操作未完成", "请根据下方提示核对后重试。", e.Message);
            return false;
        }
        finally { _job = null; _cancellable = false; UpdateActions(); }
    }

    private void UpdateActions()
    {
        var ready = _job is null;
        var count = Entries.SelectedItems.Count;
        var single = count == 1 ? (Entries.SelectedItem as IEntryRow)?.Item : null;
        PickButton.IsEnabled = DriveList.IsEnabled = RefreshDrivesButton.IsEnabled = SettingsButton.IsEnabled = ready;
        RescanButton.IsEnabled = ready && _directory is not null;
        RescanButton.Content = _scan is null ? "扫描当前范围" : "全量重扫";
        RefreshButton.IsEnabled = ready && _directory is not null;
        RefreshButton.Content = _scope?.NeedsRefresh == true ? "⟳  刷新 ·" : "⟳  刷新";
        var (running, waiting) = _fileTasks.Counts;
        FileTasksButton.Visibility = _fileActionAwaiters > 0 || _fileHistory.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        FileTasksButton.Content = running + waiting > 0 ? $"文件任务 {running + waiting}" : "文件任务";
        FileTasksButton.ToolTip = running + waiting > 0 ? $"执行中 {running} · 排队 {waiting} · 可继续浏览或刷新" : _fileHistory.LastOrDefault()?.Split('\n')[0];
        UpButton.IsEnabled = ready && (_directory?.Parent is not null ||
            _directory is { IsSizeKnown: false } folder && Path.GetDirectoryName(folder.FullPath) is not null);
        SearchBox.IsEnabled = SearchButton.IsEnabled = ready && _result is not null;
        BackToFolderButton.IsEnabled = ready && _directory is not null;
        DuplicateButton.IsEnabled = ready && _directory is { IsSizeKnown: true };
        var allowed = count is > 0 and <= FileActions.BatchLimit && Entries.SelectedItems.OfType<IEntryRow>()
            .All(row => row.Item is { IsSizeKnown: true, Parent: not null, IsUnavailable: false, IsPartial: false, ReadFailed: false });
        RevealButton.IsEnabled = ready && single is { IsUnavailable: false };
        RecycleButton.IsEnabled = DeleteButton.IsEnabled = ready && allowed;
        MoveButton.IsEnabled = ready && count is > 0 and <= FileActions.BatchLimit &&
            Entries.SelectedItems.OfType<IEntryRow>().All(row => row.Item is { Parent: not null, IsUnavailable: false });
        AiButton.IsEnabled = ready && count is > 0 and <= AiProvider.SelectionLimit;
        IssuesButton.IsEnabled = _result is not null;
        CancelButton.Visibility = !ready && _cancellable ? Visibility.Visible : Visibility.Collapsed;
        CancelButton.IsEnabled = _job?.IsCancellationRequested != true;
        SelectionText.Text = single is not null ? single.FullPath : count == 0 ? "" : $"已选中 {count:N0} 项";
    }

    private static DriveCard? DriveAt(string root)
    {
        try
        {
            var drive = new DriveInfo(root);
            if (!drive.IsReady) return null;
            var label = drive.VolumeLabel;
            return new(drive.Name, drive.Name.TrimEnd('\\'), label.Length == 0 ? "本地磁盘" : label,
                drive.AvailableFreeSpace, drive.TotalSize);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) { return null; }
    }

    private async Task RefreshDrivesAsync()
    {
        var drives = await Task.Run(() => DriveInfo.GetDrives()
            .Where(d => d.DriveType is DriveType.Fixed or DriveType.Removable)
            .Select(d => DriveAt(d.Name)).OfType<DriveCard>().ToArray());
        DriveList.ItemsSource = drives;
    }
    private async void RefreshDrives_Click(object sender, RoutedEventArgs e) => await RefreshDrivesAsync();
    private async void Drive_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is DriveCard drive)
        {
            if (_scopes.ContainsKey(drive.Root)) await ScanAsync(drive.Root);
            else await BrowseAsync(drive.Root);
        }
    }
    private async void Pick_Click(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFolderDialog { Title = "选择要浏览的文件夹（不扫描即可移动）", Multiselect = false };
        if (picker.ShowDialog(this) == true)
        {
            var path = PathSafety.Normalize(picker.FolderName);
            if (_scopes.ContainsKey(path)) await ScanAsync(path);
            else await BrowseAsync(path);
        }
    }
    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        if (_scope is null && _directory is { } folder) await BrowseAsync(folder.FullPath);
        else await RefreshScanAsync();
    }
    private async void Rescan_Click(object sender, RoutedEventArgs e)
    {
        if (_result is { } result) await RunJobAsync("正在准备全量重扫…", ct => FullScanAsync(result.Root.FullPath, _directory, false, ct));
        else if (_directory is { } folder) await ScanAsync(folder.FullPath);
    }
    private async void Up_Click(object sender, RoutedEventArgs e)
    {
        if (_directory is { IsSizeKnown: false } folder && Path.GetDirectoryName(folder.FullPath) is { } path) await BrowseAsync(path);
        else if (_directory?.Parent is { } parent) await NavigateAsync(parent);
    }
    private async void BackToFolder_Click(object sender, RoutedEventArgs e) { if (_directory is { } dir) await NavigateAsync(dir); }
    private async void Search_Click(object sender, RoutedEventArgs e) => await SearchAsync();
    private async void Search_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { e.Handled = true; await SearchAsync(); }
    }
    private async Task SearchAsync()
    {
        if (_result is null || _directory is null) return;
        var query = SearchBox.Text.Trim();
        if (query.Length == 0) { if (_view != ViewMode.Folder) await NavigateAsync(_directory); return; }
        var root = _result.Root;
        await RunJobAsync("正在搜索文件名…", async ct =>
        {
            var (rows, matches) = await Task.Run(() => DiskQuery.Search(root, query, ct), ct);
            SetView(rows, ViewMode.Search, $"搜索 · {matches:N0} 项", "没有名称匹配的条目");
            SizeColumn.SortDirection = ListSortDirection.Descending;
            StatusText.Text = matches > rows.Length ? $"扫描范围内找到 {matches:N0} 项，仅显示前 {rows.Length:N0} 项。" : $"扫描范围内找到 {matches:N0} 项。";
        });
    }

    private async void Duplicates_Click(object sender, RoutedEventArgs e)
    {
        if (_directory is not { } directory) return;
        await RunJobAsync("正在筛选同大小文件…", async ct =>
        {
            var report = true;
            var progress = new Progress<string>(text => { if (report && !ct.IsCancellationRequested) StatusText.Text = text; });
            DuplicateResult result;
            try { result = await Task.Run(() => DuplicateFinder.Find(directory, ct, progress), ct); }
            finally { report = false; }
            var rows = new List<DuplicateRow>();
            var mapping = new Dictionary<DiskNode, string>();
            for (var i = 0; i < result.Groups.Length; i++)
            {
                var label = $"#{i + 1:000}";
                foreach (var node in result.Groups[i].Files) { rows.Add(new(node, label)); mapping.Add(node, label); }
            }
            _duplicateGroups = mapping;
            SetView(rows.ToArray(), ViewMode.Duplicates, $"重复 · {result.Groups.Length:N0} 组", "未找到已确认的重复副本");
            StatusText.Text = $"SHA-256 查重 · {result.Groups.Length:N0} 组 · 跳过 {result.Unreadable:N0} 项、{result.HardLinkAliases:N0} 个硬链接别名";
        });
    }

    private async void Entries_Sorting(object sender, DataGridSortingEventArgs e)
    {
        e.Handled = true;
        if (_job is not null || _rows.Length == 0) return;
        var column = e.Column;
        var ascending = column.SortDirection != ListSortDirection.Ascending;
        var key = column.SortMemberPath;
        var original = _rows;
        await RunJobAsync("正在排序…", async ct =>
        {
            var sorted = await Task.Run(() =>
            {
                var rows = original.ToArray();
                Array.Sort(rows, (a, b) => ascending ? Compare(a, b, key) : Compare(b, a, key));
                ct.ThrowIfCancellationRequested();
                return rows;
            }, ct);
            SetView(sorted, _view, ViewLabel.Text, EmptyLabel.Text);
            column.SortDirection = ascending ? ListSortDirection.Ascending : ListSortDirection.Descending;
            StatusText.Text = $"已排序 · {sorted.Length:N0} 项";
        });
    }
    private static int Compare(IEntryRow a, IEntryRow b, string key)
    {
        var first = a.Item;
        var second = b.Item;
        var cmp = key switch
        {
            "Size" => first.Bytes.CompareTo(second.Bytes),
            "Share" => first.Share.CompareTo(second.Share),
            "Modified" => first.ModifiedTicks.CompareTo(second.ModifiedTicks),
            "Path" => StringComparer.OrdinalIgnoreCase.Compare(first.Parent?.FullPath, second.Parent?.FullPath),
            "Kind" => StringComparer.OrdinalIgnoreCase.Compare(first.Kind, second.Kind),
            "GroupLabel" => StringComparer.OrdinalIgnoreCase.Compare(a.GroupLabel, b.GroupLabel),
            _ => 0
        };
        return cmp != 0 ? cmp : StringComparer.OrdinalIgnoreCase.Compare(first.Name, second.Name);
    }

    private void Entries_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateActions();
    private async void Entries_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (_job is not null || e.OriginalSource is not DependencyObject source ||
            ItemsControl.ContainerFromElement(Entries, source) is not DataGridRow { Item: IEntryRow row }) return;
        await ActivateAsync(row.Item);
    }
    private async Task ActivateAsync(DiskNode node)
    {
        if (node.IsDirectory) await NavigateAsync(node);
        else Reveal(node);
    }
    private void Reveal_Click(object sender, RoutedEventArgs e)
    {
        if (_job is null && Entries.SelectedItem is IEntryRow row) Reveal(row.Item);
    }
    private void Reveal(DiskNode node)
    {
        if (node.IsUnavailable) return;
        try
        {
            PathSafety.RequireOrdinaryPath(node.FullPath);
            Process.Start(new ProcessStartInfo(Path.Join(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"),
                "/select,\"" + node.FullPath + "\"") { UseShellExecute = false })?.Dispose();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or Win32Exception)
        { Sheet.Inform(this, "无法打开位置", "请重新扫描后再试。", e.Message); }
    }

    private DiskNode[] Selection() => Entries.SelectedItems.OfType<IEntryRow>().Select(r => r.Item).ToArray();
    private async void Move_Click(object sender, RoutedEventArgs e) => await FileActionAsync(FileActionKind.Move);
    private async void Recycle_Click(object sender, RoutedEventArgs e) => await FileActionAsync(FileActionKind.Recycle);
    private async void Delete_Click(object sender, RoutedEventArgs e) => await FileActionAsync(FileActionKind.Delete);

    private async Task FileActionAsync(FileActionKind kind)
    {
        if (_job is not null || Entries.SelectedItems.Count is 0 or > FileActions.BatchLimit) return;
        var selected = Selection();
        var sources = selected.Select(FileSource.Capture).ToArray();
        string? destination = null;
        if (kind == FileActionKind.Move)
        {
            var picker = new OpenFolderDialog { Title = "选择移动到的目标文件夹", Multiselect = false };
            if (picker.ShowDialog(this) != true) return;
            destination = picker.FolderName;
        }
        var verb = kind switch { FileActionKind.Move => "移动", FileActionKind.Delete => "彻底删除", _ => "移到回收站" };
        var header = kind switch
        {
            FileActionKind.Move => $"目标：{destination}\n发现同名项时会询问如何处理。移动在后台运行，列表不会自动重扫。",
            FileActionKind.Delete => "此操作不经过回收站，无法用盘镜撤销。选中的文件夹及其内容都会被删除，请务必核对备份。",
            _ => "将请求 Windows 移到回收站。若 Windows 另行警告将永久删除，请取消并核对。回收站未清空前不一定释放磁盘空间。"
        };
        var lines = $"{header}\n\n本次选中 {selected.Length:N0} 项，请核对文件名和路径：\n\n" +
            string.Join('\n', selected.Select(n => $"{n.FullPath}  [{n.SizeText}]"));
        if (!Sheet.Confirm(this, $"确认{verb}", "只操作下方手动选择的项。", lines, verb, danger: kind != FileActionKind.Move,
            confirmation: kind == FileActionKind.Delete ? "彻底删除" : null)) return;
        await SubmitFileActionAsync(sources, destination, kind == FileActionKind.Delete, reviewConflicts: kind == FileActionKind.Move);
    }

    internal async Task<FileActionResult?> SubmitFileActionAsync(FileSource[] sources, string? destination, bool permanent = false,
        bool reviewConflicts = false)
    {
        _fileActionAwaiters++;
        UpdateActions();
        FileActionPlan? submitted = null;
        try
        {
            var plan = await Task.Run(() => FileActions.Prepare(sources, destination, permanent,
                reviewConflicts ? MoveConflict.Review : MoveConflict.Reject));
            if (reviewConflicts && plan.Items.Select(item => item.Collision).OfType<MoveCollision>().ToArray() is { Length: > 0 } collisions)
            {
                var choice = Sheet.ResolveMoveConflicts(this, collisions);
                if (choice is null) { RecordFileTask("已取消移动 · 未更改文件", []); return null; }
                plan = FileActions.WithConflicts(plan, choice.Value);
                if (plan.Items.Length == 0)
                {
                    RecordFileTask($"移动 · 已跳过全部 {plan.Skipped:N0} 个同名项", []);
                    return new(0, false, [], Skipped: plan.Skipped);
                }
            }
            var job = _fileTasks.Enqueue(plan);
            submitted = plan;
            if (_interactive) ShowFileTasks(activate: false);
            var result = await job;
            var summary = $"{plan.Label} · 完成 {result.Completed:N0}/{sources.Length:N0} 项" +
                (result.Skipped > 0 ? $" · 跳过 {result.Skipped:N0} 个同名项" : "") +
                (result.Aborted ? " · 已取消剩余操作" : "") + (result.Errors.Length > 0 ? $" · {result.Errors.Length:N0} 条异常" : "");
            string[] details = [.. result.Errors, .. (result.Backups ?? []).Select(path => $"原目标备份：{path}")];
            RecordFileTask(summary, details);
            if (reviewConflicts && result.Errors.Length > 0)
                ShowMoveFailure(summary, string.Join('\n', details), result.PermissionDenied);
            else if (reviewConflicts && _job is null)
                StatusText.Text = summary;
            return result;
        }
        catch (Exception error) when (FileActions.IsActionError(error) || error is ArgumentException)
        {
            var title = submitted is null ? "未启动文件任务" : $"{submitted.Label} · 未完成";
            var detail = reviewConflicts ? FileActions.MoveError(error) : error.Message;
            RecordFileTask(title, [detail]);
            if (submitted is null && _job is null) StatusText.Text = "未启动文件任务 · " + error.Message.Split('\n')[0];
            if (reviewConflicts) ShowMoveFailure(title, detail, FileActions.IsAccessDenied(error));
            return null;
        }
        finally
        {
            if (submitted is not null)
            {
                _fileRevision++;
                foreach (var scope in _scopes.Values)
                {
                    var affected = false;
                    foreach (var path in submitted.Claims) affected |= scope.Scan.MarkChanged(path);
                    if (!affected) continue;
                    scope.FileRevision++;
                    scope.NeedsRefresh = true;
                }
            }
            _fileActionAwaiters--;
            UpdateActions();
        }
    }

    private void ShowMoveFailure(string title, string detail, bool accessDenied)
    {
        // Elevate a clean instance only. Never replay a job: some items or target copies may already exist.
        using var identity = WindowsIdentity.GetCurrent();
        var elevated = new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        var exe = Environment.ProcessPath;
        if (!accessDenied || elevated || exe is null ||
            !string.Equals(Path.GetFileName(exe), "DiskLens.exe", StringComparison.OrdinalIgnoreCase))
        {
            Sheet.Inform(this, title, "请核对下方原因和源、目标状态。没有自动重试。", detail, copy: true);
            return;
        }
        if (!Sheet.Confirm(this, title, "Windows 拒绝访问。可请求管理员权限打开新窗口；请在其中核对已完成的项，再手动重试。原窗口和扫描缓存会保留。",
            detail, "以管理员身份打开")) return;
        try { Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true, Verb = "runas" })?.Dispose(); }
        catch (Win32Exception error) when (error.NativeErrorCode == 1223) { if (_job is null) StatusText.Text = "已取消管理员权限请求"; }
        catch (Win32Exception error) { Sheet.Inform(this, "未能请求管理员权限", "没有重新执行移动。", error.Message); }
    }

    private void RecordFileTask(string summary, string[] errors)
    {
        if (_fileHistory.Count == 16) _fileHistory.Dequeue();
        _fileHistory.Enqueue(errors.Length == 0 ? summary : $"{summary}\n{string.Join('\n', errors)}");
    }

    private void FileTasks_Click(object sender, RoutedEventArgs e)
    {
        ShowFileTasks(activate: true);
    }

    private void ShowFileTasks(bool activate)
    {
        if (_tasksWindow is null)
        {
            _tasksWindow = new FileTasksWindow(this, _fileTasks,
                () => _fileHistory.Count == 0 ? "尚无提交结果。" : string.Join("\n\n", _fileHistory.Reverse()));
            _tasksWindow.Closed += (_, _) => _tasksWindow = null;
            _tasksWindow.ShowActivated = activate;
            _tasksWindow.Show();
        }
        if (activate)
        {
            if (_tasksWindow.WindowState == WindowState.Minimized) _tasksWindow.WindowState = WindowState.Normal;
            _tasksWindow.Activate();
        }
    }

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        if (_job is not null) return;
        var settings = new SettingsWindow(this, _provider, _store);
        if (settings.ShowDialog() == true) { _provider = settings.Result; StatusText.Text = "AI 设置已保存 · 不会自动上传数据"; }
    }
    private async void Ai_Click(object sender, RoutedEventArgs e)
    {
        if (_job is not null || Entries.SelectedItems.Count is 0 or > AiProvider.SelectionLimit) return;
        if (string.IsNullOrWhiteSpace(_provider.Address) || string.IsNullOrWhiteSpace(_provider.Model))
        { Settings_Click(sender, e); return; }
        var settings = _provider;
        Uri endpoint;
        try { endpoint = AiProvider.Endpoint(settings); }
        catch (IOException error) { Sheet.Inform(this, "请检查 AI 设置", "服务地址、模型 ID 或 Key 不可用。", error.Message); return; }
        var metadata = AiProvider.Metadata(Selection(), _duplicateGroups);
        if (!Sheet.Confirm(this, "预览将发送的元数据", "名称可能含个人信息。仅发送下方 JSON，不发送完整路径、文件内容或哈希。", $"服务接口：{endpoint}\n\n{metadata}", "发送并获取建议")) return;
        await RunJobAsync("正在向用户配置的模型服务请求建议…", async ct =>
        {
            var advice = await AiProvider.SuggestAsync(settings, metadata, ct);
            StatusText.Text = "收到 AI 建议 · 未执行文件操作";
            Sheet.Inform(this, "AI 文件建议", "模型只看到了有限元数据。请自行核对，建议不会触发任何文件操作。", advice, copy: true);
        });
    }

    private void Issues_Click(object sender, RoutedEventArgs e)
    {
        if (_result is not { } r) return;
        var p = r.Totals;
        var issues = r.Issues.Length == 0 ? "没有记录到跳过项。" : "部分明细（最多 32 条）：\n" + string.Join('\n', r.Issues.Select(x => $"{x.Path}\n  {x.Reason}"));
        Sheet.Inform(this, "扫描详情", $"已读取 {p.Files:N0} 文件 · {p.Directories - 1:N0} 子文件夹 · {r.Elapsed.TotalSeconds:0.00} 秒",
            $"范围：{r.Root.FullPath}\n逻辑大小：{Sizes.Format(p.Bytes)}\n链接 / 离线项：{p.Skipped:N0} · 无法完整读取的目录：{p.Inaccessible:N0}\n\n" +
            "逻辑大小按目录项累计。压缩、稀疏、硬链接、云文件、文件系统元数据、权限和回收站等会使其不同于 Windows 的实际盘用量；星号表示目录统计不完整。\n\n" + issues);
    }
    private void Cancel_Click(object sender, RoutedEventArgs e) => CancelJob();
    private void CancelJob()
    {
        if (_job is null || !_cancellable) return;
        _job.Cancel();
        StatusText.Text = "正在取消…";
        UpdateActions();
    }
    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_fileActionAwaiters > 0)
        {
            e.Cancel = true;
            StatusText.Text = "还有文件任务未完成，完成后才能退出。";
            return;
        }
        if (_job is null) return;
        e.Cancel = true;
        if (_cancellable) CancelJob();
    }
    private async void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && _job is not null && _cancellable) { e.Handled = true; CancelJob(); }
        else if (e.Key == Key.F5 && RefreshButton.IsEnabled) { e.Handled = true; Refresh_Click(sender, e); }
        else if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control && SearchBox.IsEnabled)
        { e.Handled = true; SearchBox.Focus(); SearchBox.SelectAll(); }
        else if (Entries.IsKeyboardFocusWithin && e.Key == Key.Delete && RecycleButton.IsEnabled)
        { e.Handled = true; await FileActionAsync(FileActionKind.Recycle); }
        else if (Entries.IsKeyboardFocusWithin && e.Key == Key.Enter && _job is null && Entries.SelectedItem is IEntryRow row)
        { e.Handled = true; await ActivateAsync(row.Item); }
    }
}
