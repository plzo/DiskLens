using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace DiskLens;

public sealed class Treemap : FrameworkElement
{
    private sealed record Tile(DiskNode? Node, string Label, long Bytes);
    private static readonly Brush[] Palette = [BrushOf(0x245664), BrushOf(0x30466C), BrushOf(0x414875), BrushOf(0x24504A),
        BrushOf(0x544563), BrushOf(0x31536C), BrushOf(0x414B5A), BrushOf(0x43535D)];
    private static readonly Brush Frame = BrushOf(0x0E1929), White = BrushOf(0xF0FAFF), Muted = BrushOf(0xB2C6DE), Accent = BrushOf(0x72E4C7);
    private readonly List<(Tile Tile, Rect Bounds)> _hits = [];
    private Tile[] _tiles = [];
    private Tile? _hover;
    private string _empty = "选择左侧磁盘，或扫描一个文件夹";
    public event Action<DiskNode>? EntryClicked;

    internal void SetFolder(DiskNode? folder, IReadOnlyList<DiskNode> ordered)
    {
        var top = new List<Tile>(17);
        long included = 0;
        var others = 0;
        foreach (var node in ordered)
        {
            if (node.Bytes <= 0 || node.IsUnavailable) continue;
            if (top.Count < 16) { top.Add(new(node, node.Name, node.Bytes)); included += node.Bytes; }
            else others++;
        }
        if (folder is not null && folder.Bytes > included && others > 0)
            top.Add(new(null, $"其他 {others:N0} 项", folder.Bytes - included));
        _tiles = top.ToArray();
        _empty = folder is null ? "选择左侧磁盘，或扫描一个文件夹" : !folder.IsSizeKnown
            ? "未扫描 · 点击“扫描当前范围”查看空间" : "当前目录没有可绘制的非零空间";
        _hover = null;
        ToolTip = null;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        _hits.Clear();
        if (ActualWidth < 10 || ActualHeight < 10) return;
        var all = new Rect(0, 0, ActualWidth, ActualHeight);
        dc.DrawRoundedRectangle(Frame, null, all, 7, 7);
        if (_tiles.Length == 0)
        {
            var mid = new Point(ActualWidth / 2, ActualHeight / 2);
            dc.DrawEllipse(null, new Pen(BrushOf(0x21374D), 1), new(mid.X - 136, mid.Y), 27, 27);
            dc.DrawEllipse(null, new Pen(BrushOf(0x32546B), 1), new(mid.X - 136, mid.Y), 19, 19);
            dc.DrawEllipse(Accent, null, new(mid.X - 136, mid.Y), 3, 3);
            DrawText(dc, _empty, mid.X - 96, mid.Y - 10, Math.Max(20, mid.X + 78), 14, Muted);
            return;
        }
        var inner = new Rect(1, 1, ActualWidth - 2, ActualHeight - 2);
        Split(dc, inner, 0, _tiles.Length, _tiles.Sum(t => (double)t.Bytes));
    }

    private void Split(DrawingContext dc, Rect rect, int start, int count, double total)
    {
        if (rect.Width < 1 || rect.Height < 1) return;
        if (count == 1)
        {
            var tile = _tiles[start];
            var bounds = new Rect(rect.X + 1, rect.Y + 1, Math.Max(0, rect.Width - 2), Math.Max(0, rect.Height - 2));
            dc.DrawRoundedRectangle(Palette[start % Palette.Length], tile == _hover && tile.Node is not null ? new Pen(Accent, 1) : null, bounds, 5, 5);
            _hits.Add((tile, bounds));
            if (bounds.Width > 42 && bounds.Height > 22)
            {
                DrawText(dc, tile.Label, bounds.X + 8, bounds.Y + 5, bounds.Width - 15, 13, White);
                if (bounds.Height > 42) DrawText(dc, Sizes.Format(tile.Bytes), bounds.X + 8, bounds.Y + 25, bounds.Width - 15, 11, Muted);
            }
            return;
        }
        double left = _tiles[start].Bytes;
        var split = 1;
        while (split < count - 1 && Math.Abs(total / 2 - left - _tiles[start + split].Bytes) < Math.Abs(total / 2 - left))
            left += _tiles[start + split++].Bytes;
        var ratio = Math.Clamp(left / total, 0, 1);
        if (rect.Width >= rect.Height)
        {
            var width = rect.Width * ratio;
            Split(dc, new(rect.X, rect.Y, width, rect.Height), start, split, left);
            Split(dc, new(rect.X + width, rect.Y, rect.Width - width, rect.Height), start + split, count - split, total - left);
        }
        else
        {
            var height = rect.Height * ratio;
            Split(dc, new(rect.X, rect.Y, rect.Width, height), start, split, left);
            Split(dc, new(rect.X, rect.Y + height, rect.Width, rect.Height - height), start + split, count - split, total - left);
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var tile = Hit(e.GetPosition(this));
        if (tile == _hover) return;
        _hover = tile;
        ToolTip = tile is null ? null : $"{tile.Label} · {Sizes.Format(tile.Bytes)}";
        Cursor = tile?.Node is null ? Cursors.Arrow : Cursors.Hand;
        InvalidateVisual();
    }
    protected override void OnMouseLeave(MouseEventArgs e) { _hover = null; InvalidateVisual(); }
    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        if (Hit(e.GetPosition(this))?.Node is not { } node) return;
        e.Handled = true;
        EntryClicked?.Invoke(node);
    }

    private Tile? Hit(Point p)
    {
        foreach (var (tile, bounds) in _hits) if (bounds.Contains(p)) return tile;
        return null;
    }

    private void DrawText(DrawingContext dc, string text, double x, double y, double width, double font, Brush color)
    {
        var line = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface((FontFamily)FindResource("AppFont"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal), font, color,
            VisualTreeHelper.GetDpi(this).PixelsPerDip) { MaxTextWidth = width, MaxLineCount = 1, Trimming = TextTrimming.CharacterEllipsis };
        dc.DrawText(line, new(x, y));
    }

    private static Brush BrushOf(int rgb)
    {
        var brush = new SolidColorBrush(Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb));
        brush.Freeze();
        return brush;
    }
}
