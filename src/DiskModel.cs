using System.Globalization;

namespace DiskLens;

public interface IEntryRow
{
    DiskNode Item { get; }
    string GroupLabel { get; }
}

public sealed class DiskNode(string name, DiskNode? parent, long bytes, long modifiedTicks, FileAttributes attributes) : IEntryRow
{
    private const FileAttributes UnavailableAttributes = FileAttributes.ReparsePoint | FileAttributes.Offline |
        (FileAttributes)0x40000 | (FileAttributes)0x400000; // recall-on-open/access

    public string Name { get; } = name;
    public DiskNode? Parent { get; } = parent;
    public long Bytes { get; internal set; } = bytes;
    public long ModifiedTicks { get; internal set; } = modifiedTicks;
    public FileAttributes Attributes { get; } = attributes;
    public bool IsDirectory => (Attributes & FileAttributes.Directory) != 0;
    public bool IsUnavailable => (Attributes & UnavailableAttributes) != 0;
    public bool IsPartial { get; internal set; }
    public bool IsSizeKnown { get; internal set; } = true;
    internal string? ReadFailure { get; set; }
    internal bool ReadFailed => ReadFailure is not null;
    internal ScanProgress? SubtreeTotals { get; set; }
    internal bool IsUnmeasurable => IsUnavailable || (ReadFailed && Children is null);
    internal List<DiskNode>? Children { get; set; }
    public DiskNode Item => this;
    public string GroupLabel => "";
    public string FullPath
    {
        get
        {
            if (Parent is null) return Name;
            var names = new Stack<string>();
            for (DiskNode? node = this; node is not null; node = node.Parent) names.Push(node.Name);
            return Path.Join(names.ToArray());
        }
    }
    public string Extension => IsDirectory ? "" : Path.GetExtension(Name);
    public string Kind => IsUnavailable ? "链接 / 离线项" : IsUnmeasurable ? "无法读取" : ReadFailed ? "读取不完整" : IsDirectory ? "文件夹" :
        Extension is { Length: > 1 } ext ? ext[1..].ToUpperInvariant() : "文件";
    public string SizeText => !IsSizeKnown ? "未扫描" : IsUnmeasurable ? "—" : Sizes.Format(Bytes) + (IsPartial ? " *" : "");
    public double Share => Parent is { Bytes: > 0 } ? Bytes * 100d / Parent.Bytes : 0;
    public string ShareText => !IsSizeKnown || IsUnmeasurable ? "—" : Share.ToString("0.#", CultureInfo.CurrentCulture) + "%";
    public string ModifiedText => ModifiedTicks <= 0 ? "—" : new DateTime(ModifiedTicks, DateTimeKind.Utc)
        .ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
}

internal static class Sizes
{
    private static readonly string[] Units = ["B", "KiB", "MiB", "GiB", "TiB", "PiB", "EiB"];

    public static string Format(long bytes)
    {
        double size = bytes;
        var unit = 0;
        while (size >= 1024 && unit < Units.Length - 1) { size /= 1024; unit++; }
        return $"{size.ToString(unit == 0 ? "N0" : "0.##", CultureInfo.CurrentCulture)} {Units[unit]}";
    }
}

internal static class DiskQuery
{
    public static IEnumerable<DiskNode> Descendants(DiskNode root)
    {
        var remaining = new Stack<DiskNode>();
        remaining.Push(root);
        while (remaining.TryPop(out var directory))
        {
            if (directory.Children is not { } children) continue;
            foreach (var child in children)
            {
                yield return child;
                if (child.Children is { Count: > 0 }) remaining.Push(child);
            }
        }
    }

    public static (DiskNode[] Rows, int Matches) Search(DiskNode root, string query, CancellationToken ct, int limit = 5000)
    {
        var rows = new List<DiskNode>();
        var matches = 0;
        foreach (var node in Descendants(root))
        {
            ct.ThrowIfCancellationRequested();
            if (!node.Name.Contains(query, StringComparison.OrdinalIgnoreCase)) continue;
            matches++;
            if (rows.Count < limit) rows.Add(node);
        }
        return (Sorted(rows, ct), matches);
    }

    public static DiskNode[] Sorted(IEnumerable<DiskNode> nodes, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var result = nodes.ToArray();
        Array.Sort(result, CompareSize);
        ct.ThrowIfCancellationRequested();
        return result;
    }

    public static int CompareSize(DiskNode a, DiskNode b)
    {
        int cmp = b.Bytes.CompareTo(a.Bytes);
        return cmp != 0 ? cmp : StringComparer.OrdinalIgnoreCase.Compare(a.Name, b.Name);
    }
}
