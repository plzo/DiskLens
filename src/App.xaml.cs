using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Automation;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace DiskLens;

public partial class App : Application
{
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (e.Args is ["--file-worker"])
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            Shutdown(await FileWorker.ServeAsync());
            return;
        }
        if (e.Args is ["--self-test", _] or ["--self-test", _, "--capture"])
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            var output = Path.GetFullPath(e.Args[1]);
            var exit = 1;
            try
            {
                await SelfTestAsync(output, e.Args.Length == 3);
                exit = 0;
            }
            catch (Exception failure)
            {
                try { WriteReport(output, new { ok = false, error = failure.ToString() }); } catch (IOException) { }
            }
            Shutdown(exit);
            return;
        }
        MainWindow = new MainWindow();
        MainWindow.Show();
    }

    private static async Task SelfTestAsync(string report, bool capture)
    {
        var parent = Path.GetDirectoryName(report)!;
        await VerifyDirectBrowserAsync(parent, report, capture);
        var fixture = Path.Join(parent, "测试磁盘-" + Guid.NewGuid().ToString("N")[..8]);
        var expected = new (string Name, int Size)[]
        {
            (@"项目素材\演示视频.mp4", 17 * 1024 * 1024), (@"项目素材\项目报告.pdf", 1024 * 1024),
            (@"备份归档\周度快照.zip", 11 * 1024 * 1024), (@"系统日志\演示日志.log", 3 * 1024 * 1024),
            (@"文档资料\使用说明.pdf", 5 * 1024 * 1024), (@"设计图片\预览.png", 7 * 1024 * 1024),
            (@"临时缓存\sample.bin", 2 * 1024 * 1024), ("README.txt", 16384), ("project-notes.txt", 1024)
        };
        foreach (var (name, bytes) in expected)
        {
            var path = Path.Join(fixture, name);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using var stream = File.Create(path);
            stream.SetLength(bytes);
        }
        var releaseMoves = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var movesStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = 0;
        var window = new MainWindow(interactive: false, executeWithProgress: async (plan, progress) =>
        {
            progress.Item(0).Report("正在复制（合成测试）", .25);
            if (Interlocked.Increment(ref started) == 2) movesStarted.TrySetResult();
            await Task.Delay(1200).ConfigureAwait(false);
            progress.Item(0).Report("正在复制（合成测试）", .5);
            await releaseMoves.Task.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
            return await FileActions.RunAsync(plan, progress: progress).ConfigureAwait(false);
        }) { ShowInTaskbar = false, ShowActivated = false, Opacity = 0 };
        window.Show();
        try
        {
            if (!await window.ScanAsync(fixture)) throw new InvalidOperationException(((TextBlock)window.FindName("StatusText")).Text);
            var grid = (DataGrid)window.FindName("Entries");
            var result = grid.Items.OfType<IEntryRow>().Select(r => r.Item).ToArray();
            if (result.Length != 8 || result.Sum(n => n.Bytes) != expected.Sum(n => (long)n.Size) || result[0].Name != "项目素材")
                throw new InvalidOperationException("Scan/UI fixture mismatch.");
            grid.SelectedIndex = 0;
            if (!((Button)window.FindName("MoveButton")).IsEnabled || !((Button)window.FindName("AiButton")).IsEnabled)
                throw new InvalidOperationException("Selection did not enable commands.");
            var dpi = await VerifySettingsAsync(window, report, capture);
            await window.Dispatcher.InvokeAsync(() => window.UpdateLayout(), DispatcherPriority.ApplicationIdle);
            if (capture && window.Content is FrameworkElement visual) Capture(visual, Path.ChangeExtension(report, ".png"), 1);
            await VerifyCachedScopesAsync(window, grid, fixture);
            await VerifyFileTasksAsync(window, grid, result, fixture, movesStarted.Task, releaseMoves, report, capture);
            WriteReport(report, new { ok = true, ui = "WPF direct browser and isolated worker, cached scopes, non-modal task progress/ETA and parallelism controls, non-blocking moves, manual refresh, AI settings",
                settingsDpiScale = dpi, files = expected.Length, entries = result.Length, bytes = result.Sum(n => n.Bytes),
                runtime = Environment.Version.ToString(), os = Environment.OSVersion.VersionString });
        }
        finally { releaseMoves.TrySetResult(); window.Close(); }
    }

    private static async Task VerifyDirectBrowserAsync(string parent, string report, bool capture)
    {
        var source = Directory.CreateDirectory(Path.Join(parent, "直接浏览-" + Guid.NewGuid().ToString("N"))).FullName;
        var target = Directory.CreateDirectory(source + "-目标").FullName;
        var nested = Directory.CreateDirectory(Path.Join(source, "子文件夹")).FullName;
        File.WriteAllText(Path.Join(source, "未扫描即可移动.txt"), "synthetic browser proof");
        File.WriteAllText(Path.Join(nested, "未扫描的内容.txt"), "nested proof");
        var window = new MainWindow(interactive: false) { ShowInTaskbar = false, ShowActivated = false, Opacity = 0 };
        window.Show();
        try
        {
            if (!await window.BrowseAsync(source)) throw new InvalidOperationException("Direct browsing failed.");
            var grid = (DataGrid)window.FindName("Entries");
            var nodes = grid.Items.OfType<DiskNode>().ToArray();
            if (nodes.Length != 2 || nodes.Any(n => n.IsSizeKnown || n.Children is not null || n.SizeText != "未扫描") ||
                ((TextBlock)window.FindName("MetricSize")).Text != "—")
                throw new InvalidOperationException("Direct browser scanned or displayed invented sizes.");
            grid.SelectAll();
            if (!((Button)window.FindName("MoveButton")).IsEnabled || ((Button)window.FindName("DeleteButton")).IsEnabled ||
                ((Button)window.FindName("DuplicateButton")).IsEnabled || ((Button)window.FindName("SearchButton")).IsEnabled)
                throw new InvalidOperationException("Unscanned command protection is incorrect.");
            await window.Dispatcher.InvokeAsync(() => window.UpdateLayout(), DispatcherPriority.ApplicationIdle);
            if (capture && window.Content is FrameworkElement visual) Capture(visual, Path.ChangeExtension(report, ".browser.png"), 1);
            var result = await window.SubmitFileActionAsync(nodes.Select(FileSource.Capture).ToArray(), target)
                .WaitAsync(TimeSpan.FromSeconds(20));
            if (result is not { Completed: 2, Errors.Length: 0 } || grid.Items.Count != 2 || Directory.EnumerateFileSystemEntries(source).Any())
                throw new InvalidOperationException($"Isolated unscanned move failed or automatically refreshed the browser: " +
                    $"result={JsonSerializer.Serialize(result)}, rows={grid.Items.Count}, remaining={Directory.EnumerateFileSystemEntries(source).Count()}");
            Click((Button)window.FindName("RefreshButton"));
            await WaitUntilAsync(() => grid.Items.Count == 0 && ((Button)window.FindName("RefreshButton")).IsEnabled);
            if (!await window.BrowseAsync(target)) throw new InvalidOperationException("Cannot browse moved items.");
            await window.NavigateAsync(grid.Items.OfType<DiskNode>().Single(n => n.IsDirectory));
            if (grid.Items.OfType<DiskNode>().Single().IsSizeKnown) throw new InvalidOperationException("Opening an unscanned folder triggered a scan.");
            Click((Button)window.FindName("UpButton"));
            await WaitUntilAsync(() => ((TextBox)window.FindName("PathBox")).Text == target && ((Button)window.FindName("RescanButton")).IsEnabled);
            Click((Button)window.FindName("RescanButton"));
            await WaitUntilAsync(() => grid.Items.OfType<DiskNode>().All(n => n.IsSizeKnown) && ((Button)window.FindName("RefreshButton")).IsEnabled);
            grid.SelectAll();
            if (!((Button)window.FindName("DeleteButton")).IsEnabled) throw new InvalidOperationException("Scanning did not restore measured commands.");
        }
        finally { window.Close(); }
    }

    private static async Task VerifyCachedScopesAsync(MainWindow window, DataGrid grid, string first)
    {
        var second = Directory.CreateDirectory(first + "-第二个范围").FullName;
        var file = Path.Join(second, "added-after-scan.txt");
        var folder = grid.Items.OfType<DiskNode>().First();
        var pathBox = (TextBox)window.FindName("PathBox");
        await window.NavigateAsync(folder);
        if (!await window.ScanAsync(second) || grid.Items.Count != 0)
            throw new InvalidOperationException("Second scan did not show its own tree.");
        File.WriteAllText(file, "external change");
        if (!await window.ScanAsync(first + Path.DirectorySeparatorChar) || pathBox.Text != folder.FullPath ||
            !ReferenceEquals(grid.Items.OfType<DiskNode>().First().Parent, folder))
            throw new InvalidOperationException("Switching back did not reuse the first tree and folder.");
        Click((Button)window.FindName("UpButton"));
        await WaitUntilAsync(() => pathBox.Text == first);
        if (!await window.ScanAsync(second) || grid.Items.Count != 0)
            throw new InvalidOperationException("Switching scopes scanned instead of restoring the snapshot.");
        await WaitUntilAsync(() => ((Button)window.FindName("RefreshButton")).IsEnabled);
        if (!await window.RefreshScanAsync() || grid.Items.OfType<DiskNode>().Single().FullPath != file)
            throw new InvalidOperationException("Refreshing restored scope did not apply its own changes.");
        if (!await window.ScanAsync(first) || pathBox.Text != first)
            throw new InvalidOperationException("First scope was lost when refreshing the second.");
    }

    private static async Task VerifyFileTasksAsync(MainWindow window, DataGrid grid, DiskNode[] items, string fixture,
        Task movesStarted, TaskCompletionSource release, string report, bool capture)
    {
        var target = Directory.CreateDirectory(fixture + "-移入").FullName;
        if (!await window.ScanAsync(target) || grid.Items.Count != 0 || !await window.ScanAsync(fixture))
            throw new InvalidOperationException("Could not cache the move destination.");
        var readme = items.Single(n => n.Name == "README.txt");
        var notes = items.Single(n => n.Name == "project-notes.txt");
        var first = window.SubmitFileActionAsync([FileSource.Capture(readme)], target);
        Task<FileActionResult?>? second = null;
        try
        {
            grid.SelectedItem = notes;
            if (!((Button)window.FindName("MoveButton")).IsEnabled || !((Button)window.FindName("DeleteButton")).IsEnabled ||
                !((Button)window.FindName("RefreshButton")).IsEnabled) throw new InvalidOperationException("File job locked the main window.");
            second = window.SubmitFileActionAsync([FileSource.Capture(notes)], target);
            await movesStarted.WaitAsync(TimeSpan.FromSeconds(5));
            Click((Button)window.FindName("FileTasksButton"));
            var panel = window.OwnedWindows.OfType<FileTasksWindow>().Single();
            panel.ShowInTaskbar = false;
            panel.Opacity = 0;
            try
            {
                await WaitUntilAsync(() => VisualChildren(panel).OfType<ProgressBar>().Count() == 2);
                if (!window.IsEnabled || !((Button)window.FindName("MoveButton")).IsEnabled)
                    throw new InvalidOperationException("The task panel locked the main window.");
                var parallel = (ComboBox)panel.FindName("ParallelBox");
                parallel.SelectedItem = 1;
                parallel.SelectedItem = 8;
                await WaitUntilAsync(() => VisualChildren(panel).OfType<ProgressBar>().All(bar => bar.Value >= 50 && !bar.IsIndeterminate) &&
                    VisualChildren(panel).OfType<TextBlock>().Any(text => text.Text.Contains("预计剩余约")));
                await window.Dispatcher.InvokeAsync(() => panel.UpdateLayout(), DispatcherPriority.ApplicationIdle);
                if (capture && panel.Content is FrameworkElement content)
                    Capture(content, Path.ChangeExtension(report, ".tasks.png"), 1);
            }
            finally { panel.Close(); }
            if (first.IsCompleted || second.IsCompleted) throw new InvalidOperationException("Closing task panel stopped or released tasks.");
            ((TextBox)window.FindName("SearchBox")).Text = "README";
            Click((Button)window.FindName("SearchButton"));
            await WaitUntilAsync(() => grid.Items.Count == 1 && ((Button)window.FindName("BackToFolderButton")).IsEnabled);
            Click((Button)window.FindName("BackToFolderButton"));
            await WaitUntilAsync(() => grid.Items.Count == items.Length && ((Button)window.FindName("RefreshButton")).IsEnabled);
        }
        finally
        {
            release.TrySetResult();
            await first.WaitAsync(TimeSpan.FromSeconds(15));
            if (second is not null) await second.WaitAsync(TimeSpan.FromSeconds(15));
        }
        if (first.Result is not { Completed: 1 } || second?.Result is not { Completed: 1 } || grid.Items.Count != items.Length ||
            File.Exists(readme.FullPath) || File.Exists(notes.FullPath) || !File.Exists(Path.Join(target, notes.Name)))
            throw new InvalidOperationException("Background moves failed or caused automatic rescan.");
        var refresh = (Button)window.FindName("RefreshButton");
        Click(refresh);
        await WaitUntilAsync(() => refresh.IsEnabled);
        if (grid.Items.Count != items.Length - 2 || grid.Items.OfType<IEntryRow>().Sum(row => row.Item.Bytes) !=
            items.Sum(n => n.Bytes) - readme.Bytes - notes.Bytes)
            throw new InvalidOperationException("Manual refresh did not update table and totals.");
        if (!await window.ScanAsync(target) || grid.Items.Count != 0 || !(refresh.Content as string ?? "").EndsWith('·'))
            throw new InvalidOperationException("The inactive destination scope did not retain its snapshot and refresh marker.");
        if (!await window.RefreshScanAsync() || grid.Items.Count != 2)
            throw new InvalidOperationException("Destination refresh did not see both moved files.");
    }

    private static async Task<double> VerifySettingsAsync(Window owner, string report, bool capture)
    {
        var sample = new ProviderSettings("https://example.test/v1", "示例模型/demo-model", "synthetic-self-test-key");
        var store = new SettingsStore(Path.Join(Path.GetDirectoryName(report)!, "unused-settings.json"));
        var settings = new SettingsWindow(owner, sample, store) { ShowActivated = false, Opacity = 0 };
        settings.Show();
        try
        {
            await owner.Dispatcher.InvokeAsync(() => settings.UpdateLayout(), DispatcherPriority.ApplicationIdle);
            var visual = (FrameworkElement)settings.Content;
            var actualDpi = VisualTreeHelper.GetDpi(visual).DpiScaleY;
            CheckFields(visual, actualDpi);
            if (capture) Capture(visual, Path.ChangeExtension(report, ".settings.png"), actualDpi);

            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var serve = ReplyToModelListAsync(listener);
            var address = VisualChildren(visual).OfType<TextBox>().Single(box => AutomationProperties.GetName(box) == "服务地址");
            var key = VisualChildren(visual).OfType<PasswordBox>().Single();
            var model = VisualChildren(visual).OfType<ComboBox>().Single();
            var fetch = VisualChildren(visual).OfType<Button>().Single(b => b.Content is "拉取模型");
            address.Text = $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/v1";
            model.Text = "";
            Click(fetch);
            await serve;
            await WaitUntilAsync(() => fetch.IsEnabled && model.Items.Count == 2);
            if (!model.Items.OfType<string>().SequenceEqual(["demo/Case-A", "demo/case-b"]))
                throw new InvalidOperationException("The model picker did not show exact model IDs.");
            model.SelectedIndex = 1;
            model.IsDropDownOpen = false;
            if (model.Text != "demo/case-b") throw new InvalidOperationException("Model choice did not update the editable field.");
            key.Password = "changed-self-test-key";
            var editor = (TextBox)model.Template.FindName("PART_EditableTextBox", model);
            editor.Text = "manual/model-id";
            if (model.HasItems || model.Text != "manual/model-id") throw new InvalidOperationException("Model picker is not editable or retained stale choices.");
            return actualDpi;
        }
        finally { settings.Close(); }
    }

    private static async Task ReplyToModelListAsync(TcpListener listener)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        using var client = await listener.AcceptTcpClientAsync(timeout.Token).ConfigureAwait(false);
        using var stream = client.GetStream();
        using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
        var requestLine = await reader.ReadLineAsync(timeout.Token).ConfigureAwait(false);
        var authorized = false;
        for (int i = 0; i < 32; i++)
        {
            var header = await reader.ReadLineAsync(timeout.Token).ConfigureAwait(false);
            if (string.IsNullOrEmpty(header)) break;
            authorized |= header.Equals("Authorization: Bearer synthetic-self-test-key", StringComparison.OrdinalIgnoreCase);
        }
        if (requestLine?.StartsWith("GET /v1/models HTTP/", StringComparison.Ordinal) != true || !authorized)
            throw new InvalidOperationException("Settings button did not send an authenticated model-list GET.");
        const string json = "{\"data\":[{\"id\":\"demo/Case-A\"},{\"id\":\"demo/case-b\"}]}";
        await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {json.Length}\r\nConnection: close\r\n\r\n{json}"),
            timeout.Token).ConfigureAwait(false);
    }

    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition()) await Task.Delay(15, timeout.Token);
    }

    private static void CheckFields(FrameworkElement layout, double scale)
    {
        var fields = VisualChildren(layout).OfType<Control>().Where(control => control is TextBox or PasswordBox).ToArray();
        if (fields.Length != 3) throw new InvalidOperationException("Settings inputs did not render.");
        foreach (var field in fields)
        {
            if (Math.Abs(VisualTreeHelper.GetDpi(field).DpiScaleY - scale) > 0.01 ||
                field.Template.FindName("PART_ContentHost", field) is not ScrollViewer viewer || viewer.ExtentHeight <= 0 ||
                viewer.ViewportHeight + 0.05 < viewer.ExtentHeight)
                throw new InvalidOperationException($"Settings input is clipped at DPI scale {scale}: {field.GetType().Name} " +
                    $"({(field.Template.FindName("PART_ContentHost", field) as ScrollViewer)?.ViewportHeight}/" +
                    $"{(field.Template.FindName("PART_ContentHost", field) as ScrollViewer)?.ExtentHeight}).");
        }
    }

    private static IEnumerable<DependencyObject> VisualChildren(DependencyObject root)
    {
        yield return root;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var child in VisualChildren(VisualTreeHelper.GetChild(root, i))) yield return child;
    }

    private static void Capture(FrameworkElement visual, string path, double scale)
    {
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(visual.ActualWidth * scale), (int)Math.Ceiling(visual.ActualHeight * scale),
            96 * scale, 96 * scale, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var png = File.Create(path);
        encoder.Save(png);
    }

    private static void WriteReport(string report, object data)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(report)!);
        File.WriteAllText(report, JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true }));
    }
}
