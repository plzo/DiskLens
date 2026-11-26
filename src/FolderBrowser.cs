namespace DiskLens;

// One directory only. No subtree size calculation, watcher, or recursive scan allocation.
internal static class FolderBrowser
{
    public static DiskNode Read(string path, CancellationToken ct)
    {
        var full = PathSafety.Normalize(path);
        PathSafety.RequireOrdinaryPath(full);
        var root = new DiskNode(full, null, 0, Directory.GetLastWriteTimeUtc(full).Ticks, File.GetAttributes(full))
            { IsSizeKnown = false, Children = [] };
        foreach (var entry in DiskScanner.Entries(full))
        {
            ct.ThrowIfCancellationRequested();
            var child = entry.Create(root);
            child.IsSizeKnown = false;
            // NTFS directory enumeration can expose an older cached timestamp than a fresh handle.
            if (child.IsDirectory && !child.IsUnavailable)
            {
                try
                {
                    using var handle = PathSafety.OpenMetadataNoFollow(child.FullPath, share: 7);
                    child.ModifiedTicks = PathSafety.OrdinaryInfo(handle, directory: true).LastWriteTicks;
                }
                catch (Exception error) when (FileActions.IsActionError(error))
                { /* Keep the entry visible; entering or moving it will report the actual access error. */ }
            }
            root.Children.Add(child);
        }
        return root;
    }
}
