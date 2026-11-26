using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;

namespace DiskLens;

internal static class Chrome
{
    public static void Attach(Window window) => window.SourceInitialized += (_, _) =>
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        var enabled = 1;
        _ = DwmSetWindowAttribute(hwnd, 20, ref enabled, sizeof(int));
    };

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}

internal static class Sheet
{
    public static void Inform(Window owner, string title, string description, string content, bool copy = false) =>
        _ = Show(owner, title, description, content, null, false, copy, null);
    public static bool Confirm(Window owner, string title, string description, string content, string action, bool danger = false, string? confirmation = null) =>
        Show(owner, title, description, content, action, danger, false, confirmation);
    public static MoveConflict? ResolveMoveConflicts(Window owner, MoveCollision[] collisions)
    {
        var canReplace = collisions.All(item => item.Backup.Length != 0);
        var details = string.Join('\n', collisions.Select(item => item.Output + (item.Backup.Length == 0 ? "（只能跳过）" : "")));
        var skipped = false;
        var merged = false;
        var replace = Show(owner, "发现同名项", canReplace
            ? $"共 {collisions.Length:N0} 个同名目标。覆盖会替换整个同名文件或目录，不合并目录。旧目标会保留为同级备份。"
            : $"共 {collisions.Length:N0} 个同名目标，含受保护、链接或不可读取的项。请选择跳过同名，或取消后分批处理。",
            details + "\n\n也可合并目录：同名文件逐项核对，相同内容保留目标，内容不同时两份均保留。", canReplace ? "覆盖同名（保留备份）" : null,
            true, false, null, "跳过同名", () => skipped = true, canReplace ? "合并（保留两份）" : null, () => merged = true);
        return replace ? MoveConflict.Replace : merged ? MoveConflict.Merge : skipped ? MoveConflict.Skip : null;
    }

    private static bool Show(Window owner, string title, string description, string content, string? action, bool danger, bool copy, string? confirmation,
        string? alternate = null, Action? chooseAlternate = null, string? extra = null, Action? chooseExtra = null)
    {
        var window = new Window { Owner = owner, Title = title + " · 盘镜", Width = extra is null ? 670 : 850, Height = 440, MinWidth = 460, MinHeight = 300,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false };
        Chrome.Attach(window);
        var layout = new DockPanel { Margin = new Thickness(20) };
        var heading = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
        heading.Children.Add(new TextBlock { Text = title, FontSize = 23, FontWeight = FontWeights.SemiBold });
        heading.Children.Add(new TextBlock { Text = description, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 4, 0, 0), Foreground = Brush("MutedBrush") });
        DockPanel.SetDock(heading, Dock.Top);
        layout.Children.Add(heading);
        var buttons = new WrapPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        DockPanel.SetDock(buttons, Dock.Bottom);
        layout.Children.Add(buttons);
        var close = new Button { Content = action is null && alternate is null ? "关闭" : "取消", IsCancel = true, MinWidth = 82 };
        buttons.Children.Add(close);
        if (copy)
        {
            var copyButton = new Button { Content = "复制文本", Margin = new Thickness(7, 0, 0, 0) };
            copyButton.Click += (_, _) =>
            {
                try { Clipboard.SetText(content); copyButton.Content = "已复制"; }
                catch (ExternalException) { copyButton.Content = "复制失败，请手动选择文本"; }
            };
            buttons.Children.Add(copyButton);
        }
        if (alternate is not null)
        {
            var other = new Button { Content = alternate, Margin = new Thickness(7, 0, 0, 0) };
            other.Click += (_, _) => { chooseAlternate?.Invoke(); window.DialogResult = false; };
            buttons.Children.Add(other);
        }
        if (extra is not null)
        {
            var other = new Button { Content = extra, Margin = new Thickness(7, 0, 0, 0) };
            other.Click += (_, _) => { chooseExtra?.Invoke(); window.DialogResult = false; };
            buttons.Children.Add(other);
        }
        if (action is not null)
        {
            var confirm = new Button { Content = action, Margin = new Thickness(7, 0, 0, 0),
                Style = (Style)Application.Current.FindResource(danger ? "DangerButton" : "PrimaryButton") };
            if (confirmation is not null)
            {
                var row = new DockPanel { Margin = new Thickness(0, 10, 0, 0) };
                var hint = new TextBlock { Text = $"请输入「{confirmation}」", VerticalAlignment = VerticalAlignment.Center,
                    Foreground = Brush("WarnBrush"), Margin = new Thickness(0, 0, 10, 0) };
                var entry = new TextBox { MinHeight = 34, VerticalContentAlignment = VerticalAlignment.Center };
                System.Windows.Automation.AutomationProperties.SetName(entry, hint.Text);
                confirm.IsEnabled = false;
                entry.TextChanged += (_, _) => confirm.IsEnabled = entry.Text.Trim() == confirmation;
                DockPanel.SetDock(row, Dock.Bottom);
                row.Children.Add(hint);
                row.Children.Add(entry);
                layout.Children.Add(row);
            }
            confirm.Click += (_, _) => window.DialogResult = true;
            buttons.Children.Add(confirm);
        }
        layout.Children.Add(new TextBox { Text = content, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Padding = new Thickness(10), AcceptsReturn = true });
        window.Content = layout;
        window.Loaded += (_, _) => close.Focus(); // the destructive button is never the default
        return window.ShowDialog() == true;
    }

    internal static Brush Brush(string key) => (Brush)Application.Current.FindResource(key);
}

internal sealed class SettingsWindow : Window
{
    private readonly TextBox _address = new() { MaxLength = 2048 };
    private readonly ComboBox _model = new();
    private readonly PasswordBox _key = new() { MaxLength = 4096 };
    private readonly CheckBox _remember = new() { Content = "用 Windows DPAPI 记住 Key（仅当前 Windows 用户）" };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 7, 0, 0) };
    private readonly Button _fetch = new() { Content = "拉取模型", Margin = new Thickness(7, 0, 0, 0), ToolTip = "使用当前地址和 API Key 请求模型列表" };
    private readonly Button _test = new() { Content = "测试连接" };
    private readonly Button _save = new() { Content = "保存设置" };
    private readonly SettingsStore _store;
    private CancellationTokenSource? _request;
    public ProviderSettings Result { get; private set; }

    public SettingsWindow(Window owner, ProviderSettings settings, SettingsStore store)
    {
        _store = store;
        Style = (Style)Application.Current.FindResource(typeof(Window));
        Result = settings;
        Owner = owner; Title = "AI 服务设置 · 盘镜"; Width = 580; MinWidth = 460; SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize; ShowInTaskbar = false; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Chrome.Attach(this);
        _model.Style = (Style)Application.Current.FindResource("ModelPicker");
        _address.Text = settings.Address; _model.Text = settings.Model; _key.Password = settings.ApiKey; _remember.IsChecked = settings.RememberKey;
        _remember.Foreground = Sheet.Brush("TextBrush");
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock { Text = "连接模型服务", FontSize = 24, FontWeight = FontWeights.SemiBold });
        panel.Children.Add(new TextBlock { Text = "兼容 Chat Completions。未配置时，扫描、移动、回收和本地查重均可离线使用。",
            TextWrapping = TextWrapping.Wrap, Foreground = Sheet.Brush("MutedBrush"), Margin = new Thickness(0, 4, 0, 4) });
        Field(panel, "服务地址", "例如 https://服务商域名/v1，也可填完整的 /chat/completions 或 /models 地址。", _address);
        Field(panel, "API Key", "使用 Bearer 鉴权；无需鉴权的本地服务可留空。", _key);
        Field(panel, "模型 ID", "填好地址和 Key 后点击「拉取模型」，也可以手动填写模型 ID。", _model, _fetch);
        _remember.Margin = new Thickness(0, 12, 0, 0);
        panel.Children.Add(_remember);
        panel.Children.Add(new TextBlock { Text = "拉取模型不发送文件信息；测试连接只发送固定测试消息。",
            TextWrapping = TextWrapping.Wrap, Foreground = Sheet.Brush("MutedBrush"), Margin = new Thickness(0, 9, 0, 0), FontSize = 12 });
        panel.Children.Add(_status);
        var footer = new DockPanel { Margin = new Thickness(0, 11, 0, 0) };
        _test.HorizontalAlignment = HorizontalAlignment.Left;
        DockPanel.SetDock(_test, Dock.Left); footer.Children.Add(_test);
        var right = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        right.Children.Add(new Button { Content = "关闭", IsCancel = true, Margin = new Thickness(0, 0, 7, 0) });
        _save.Style = (Style)Application.Current.FindResource("PrimaryButton");
        right.Children.Add(_save); footer.Children.Add(right); panel.Children.Add(footer);
        Content = new Border { Child = panel, Background = Background, Padding = new Thickness(21, 18, 21, 16) };
        _save.Click += Save_Click; _test.Click += Test_Click; _fetch.Click += async (_, _) => await FetchModelsAsync();
        _address.TextChanged += (_, _) => ProviderChanged();
        _key.PasswordChanged += (_, _) => ProviderChanged();
        Closed += (_, _) => _request?.Cancel();
        UpdateControls();
    }

    private static void Field(Panel panel, string label, string detail, Control input, Button? action = null)
    {
        panel.Children.Add(new Label { Content = label, Target = input, Foreground = Sheet.Brush("TextBrush"), FontWeight = FontWeights.SemiBold,
            Padding = new Thickness(0), Margin = new Thickness(0, 8, 0, 4) });
        input.MinHeight = 38;
        input.VerticalContentAlignment = VerticalAlignment.Center;
        System.Windows.Automation.AutomationProperties.SetName(input, label);
        if (action is null) panel.Children.Add(input);
        else
        {
            var row = new DockPanel();
            DockPanel.SetDock(action, Dock.Right);
            row.Children.Add(action); row.Children.Add(input);
            panel.Children.Add(row);
        }
        panel.Children.Add(new TextBlock { Text = detail, Foreground = Sheet.Brush("MutedBrush"), FontSize = 11, Margin = new Thickness(0, 3, 0, 0), TextWrapping = TextWrapping.Wrap });
    }

    private ProviderSettings Read() => new(_address.Text.Trim(), _model.Text.Trim(), _key.Password.Trim(), _remember.IsChecked == true);
    private void SetStatus(string text, bool error = false) { _status.Text = text; _status.Foreground = Sheet.Brush(error ? "WarnBrush" : "AccentBrush"); }

    private void UpdateControls()
    {
        var ready = _request is null;
        _test.IsEnabled = _save.IsEnabled = _address.IsEnabled = _model.IsEnabled = _key.IsEnabled = ready;
        _fetch.IsEnabled = ready && !string.IsNullOrWhiteSpace(_address.Text);
    }

    private void ProviderChanged()
    {
        if (_model.HasItems) SetModels([], _model.Text);
        SetStatus("");
        UpdateControls();
    }

    private void SetModels(string[] models, string text)
    {
        _model.IsDropDownOpen = false;
        _model.ItemsSource = models;
        _model.SelectedIndex = Array.IndexOf(models, text);
        _model.Text = text;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var settings = Read();
            if (settings.Address.Length != 0 || settings.Model.Length != 0 || settings.ApiKey.Length != 0) _ = AiProvider.Endpoint(settings);
            _store.Save(settings);
            Result = settings;
            DialogResult = true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or Win32Exception)
        { SetStatus(error.Message, true); }
    }

    private async Task FetchModelsAsync()
    {
        var open = false;
        await RunRequestAsync("正在拉取模型列表…", async (settings, ct) =>
        {
            var models = await AiProvider.ListModelsAsync(settings, ct);
            ct.ThrowIfCancellationRequested();
            var text = models.Length == 1 && settings.Model.Length == 0 ? models[0] : _model.Text;
            SetModels(models, text);
            open = models.Length > 1;
            SetStatus(models.Length switch
            {
                0 => "服务没有返回可选模型，可手动填写模型 ID。",
                1 when settings.Model.Length == 0 => "已填入唯一返回的模型；请测试连接或保存设置。",
                _ => $"已拉取 {models.Length:N0} 个模型，可从列表选择，也可手动填写。"
            });
        });
        if (open && IsVisible)
        {
            _model.Focus();
            _model.IsDropDownOpen = true;
        }
    }

    private async void Test_Click(object sender, RoutedEventArgs e) => await RunRequestAsync("正在发送固定测试消息…", async (settings, ct) =>
    {
        await AiProvider.TestAsync(settings, ct);
        ct.ThrowIfCancellationRequested();
        SetStatus("连接成功，服务已返回模型文本。测试不保存设置。");
    });

    private async Task RunRequestAsync(string busy, Func<ProviderSettings, CancellationToken, Task> action)
    {
        if (_request is not null) return;
        var settings = Read();
        using var request = new CancellationTokenSource();
        _request = request;
        UpdateControls();
        SetStatus(busy);
        try { await action(settings, request.Token); }
        catch (OperationCanceledException) when (request.IsCancellationRequested) { }
        catch (IOException error) { if (!request.IsCancellationRequested) SetStatus(error.Message, true); }
        finally { _request = null; UpdateControls(); }
    }
}
