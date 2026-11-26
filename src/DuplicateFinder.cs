using System.Buffers;
using System.ComponentModel;
using System.Security.Cryptography;

namespace DiskLens;

internal sealed record DuplicateGroup(string Hash, DiskNode[] Files);
internal sealed record DuplicateResult(DuplicateGroup[] Groups, int Candidates, int Unreadable, int HardLinkAliases);
internal sealed record DuplicateRow(DiskNode Item, string GroupLabel) : IEntryRow;

internal static class DuplicateFinder
{
    private const int SampleSize = 64 * 1024;
    private sealed record Candidate(DiskNode Node, PathSafety.FileIdentity Id, string Hash);

    public static DuplicateResult Find(DiskNode root, CancellationToken ct, IProgress<string>? progress = null)
    {
        var buckets = new Dictionary<long, (DiskNode First, List<DiskNode>? More)>();
        foreach (var node in DiskQuery.Descendants(root))
        {
            ct.ThrowIfCancellationRequested();
            if (node.IsDirectory || node.IsUnavailable || node.Bytes == 0) continue;
            if (!buckets.TryGetValue(node.Bytes, out var bucket)) buckets.Add(node.Bytes, (node, null));
            else if (bucket.More is null) buckets[node.Bytes] = (bucket.First, [bucket.First, node]);
            else bucket.More.Add(node);
        }
        var count = buckets.Values.Sum(b => b.More?.Count ?? 0);
        var groups = new List<DuplicateGroup>();
        var processed = 0;
        var unreadable = 0;
        var aliases = 0;
        var buffer = ArrayPool<byte>.Shared.Rent(SampleSize * 2);
        try
        {
            foreach (var (length, bucket) in buckets)
            {
                ct.ThrowIfCancellationRequested();
                if (bucket.More is not { } nodes) continue;
                var samples = new Dictionary<string, List<Candidate>>(StringComparer.Ordinal);
                foreach (var node in nodes)
                {
                    ct.ThrowIfCancellationRequested();
                    if (++processed == 1 || processed % 100 == 0) progress?.Report($"查重：正在检查同大小候选 {processed:N0} / {count:N0}");
                    try
                    {
                        var candidate = Hash(node, buffer, sample: length > SampleSize * 2, ct);
                        if (!samples.TryGetValue(candidate.Hash, out var list)) samples.Add(candidate.Hash, list = []);
                        list.Add(candidate);
                    }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException or Win32Exception) { unreadable++; }
                }
                foreach (var list in samples.Values)
                {
                    if (list.Count < 2) continue;
                    var unique = list.DistinctBy(c => c.Id).ToArray();
                    aliases += list.Count - unique.Length;
                    if (unique.Length < 2) continue;
                    if (length <= SampleSize * 2)
                    {
                        groups.Add(new(unique[0].Hash, unique.Select(c => c.Node).ToArray()));
                        continue;
                    }
                    var verified = new List<Candidate>();
                    foreach (var candidate in unique)
                    {
                        ct.ThrowIfCancellationRequested();
                        try
                        {
                            var actual = Hash(candidate.Node, buffer, sample: false, ct);
                            if (actual.Id == candidate.Id) verified.Add(actual);
                            else unreadable++;
                        }
                        catch (Exception e) when (e is IOException or UnauthorizedAccessException or Win32Exception) { unreadable++; }
                    }
                    foreach (var match in verified.GroupBy(c => c.Hash, StringComparer.Ordinal))
                    {
                        var files = match.Select(c => c.Node).ToArray();
                        if (files.Length > 1) groups.Add(new(match.Key, files));
                    }
                }
            }
        }
        finally { ArrayPool<byte>.Shared.Return(buffer, clearArray: true); }
        return new(groups.OrderByDescending(g => g.Files[0].Bytes).ThenBy(g => g.Hash, StringComparer.Ordinal).ToArray(), count, unreadable, aliases);
    }

    private static Candidate Hash(DiskNode node, byte[] buffer, bool sample, CancellationToken ct)
    {
        var path = node.FullPath;
        PathSafety.RequireOrdinaryPath(path);
        using var handle = PathSafety.OpenReadNoFollow(path);
        if (!PathSafety.GetFileInformationByHandle(handle, out var info)) throw new Win32Exception();
        if (PathSafety.IsSpecial((FileAttributes)info.Attributes) || (info.Attributes & (uint)FileAttributes.Directory) != 0 ||
            info.Length != node.Bytes || info.LastWriteTicks != node.ModifiedTicks)
            throw new IOException("文件已改变或不可用。");
        var id = PathSafety.Identity(handle, info);
        using var stream = new FileStream(handle, FileAccess.Read, 1, isAsync: false);
        byte[] hash;
        if (sample)
        {
            stream.ReadExactly(buffer.AsSpan(0, SampleSize));
            stream.Seek(-SampleSize, SeekOrigin.End);
            stream.ReadExactly(buffer.AsSpan(SampleSize, SampleSize));
            hash = SHA256.HashData(buffer.AsSpan(0, SampleSize * 2));
        }
        else
        {
            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            int read;
            while ((read = stream.Read(buffer, 0, SampleSize * 2)) > 0)
            {
                ct.ThrowIfCancellationRequested();
                sha.AppendData(buffer.AsSpan(0, read));
            }
            hash = sha.GetHashAndReset();
        }
        return new(node, id, Convert.ToHexString(hash));
    }
}
