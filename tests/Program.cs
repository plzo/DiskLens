using System.ComponentModel;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using DiskLens;

internal static class Program
{
    private static string _root = "";
    private static int _passed;
    private static CancellationToken None => CancellationToken.None;

    private static async Task<int> Main(string[] args)
    {
        if (args.Length >= 4 && args[0] == "--move-contents") return await MoveContents(args);
        if (args.Length == 0) { Console.Error.WriteLine("Usage: DiskLens.Tests <new-scratch-parent> [--shell] [--cross-target <scratch-parent>] [--benchmark] [--file-benchmark]"); return 2; }
        _root = Path.Join(Path.GetFullPath(args[0]), "fixture-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        try
        {
            await Check("logical sizes / Unicode / hidden entries", Scan);
            await Check("junctions not followed; same-volume parent rename preserves them", Junction);
            await Check("long Windows paths", LongPaths);
            await Check("cancellation and invalid scan root", Cancel);
            await Check("case-insensitive recursive search and result cap", Search);
            await Check("full SHA-256, sample collisions, and hardlink identity", Duplicates);
            await Check("move file and folder with no replacement", Move);
            await Check("collisions, parent/child, protected and stale paths rejected", Reject);
            await Check("move conflict skip/replace, backups and late changes", MoveConflicts);
            await Check("move diagnostics distinguish ACL denial from file sharing", MoveDiagnostics);
            await Check("live incremental refresh after external resize/move/delete", IncrementalRefresh);
            await Check("retry every inaccessible directory after ACL recovery", PermissionRefresh);
            await Check("refresh acknowledges only processed changes; lost baseline falls back", RefreshTracking);
            await Check("two actual concurrent moves, queue and canonical path conflicts", ConcurrentMoves);
            await Check("40 queued tasks, adjustable parallelism, history and failure recovery", QueuedTasks);
            await Check("unscanned browser, merge, verified duplicates and retained conflicts", DirectMerge);
            await Check("streaming unexpected failure cancels a full channel without hanging", StreamFailure);
            await Check("worker termination cannot crash parent or report success", WorkerFailure);
            await Check("progress estimator excludes unknown work and expires stale ETA", ProgressEstimates);
            await Check("parallel permanent delete and protection for changed/locked entries", DeletePermanent);
            await Check("provider addresses and metadata privacy", Metadata);
            await Check("GET models, exact IDs, no body and no redirects", Models);
            await Check("DPAPI save / reload / forget", Settings);
            await Check("actual HTTP JSON request and text response", HttpSuccess);
            await Check("auth errors and malformed responses keep secrets out", HttpErrors);
            await Check("redirect is not followed and requests can be cancelled", HttpRedirect);
            if (args.Contains("--shell")) await Check("native Windows Recycle Bin with synthetic file", Recycle);
            var target = Array.IndexOf(args, "--cross-target");
            if (target >= 0)
            {
                await Check("native cross-volume parallel move preserves content and named streams", () => CrossVolume(args[target + 1]));
                await Check("cross-volume copy failures and late collisions preserve source", () => CrossVolumeFailures(args[target + 1]));
                await Check("cross-volume local junctions and collision backups", () => CrossVolumeJunction(args[target + 1]));
                await Check("native copy progress reports bytes and final task state", () => CrossVolumeProgress(args[target + 1]));
                await Check("isolated streaming merge with relative symlink and mapped junction", () => StreamCrossVolume(args[target + 1]));
                await Check("two isolated cross-volume merges preserve hashes, conflicts and streams", () => StreamConcurrent(args[target + 1]));
                if (args.Contains("--file-benchmark")) await FileBenchmark(args[target + 1]);
            }
            if (args.Contains("--benchmark")) await Benchmark();
            Console.WriteLine($"PASS: {_passed} checks");
            return 0;
        }
        catch (Exception e) { Console.Error.WriteLine(e); return 1; }
    }

    private static async Task<int> MoveContents(string[] args)
    {
        if (args[1] != "--merge" || (args.Length - 2) % 2 != 0) return 2;
        var queue = new FileTaskQueue(parallelTasks: 2);
        var jobs = new List<Task<FileActionResult>>();
        for (var i = 2; i < args.Length; i += 2)
        {
            var source = PathSafety.Normalize(args[i]);
            var target = PathSafety.Normalize(args[i + 1]);
            if (!Directory.Exists(target)) throw new IOException("请先创建并核对目标目录。");
            var folder = FolderBrowser.Read(source, None);
            if (folder.Children!.Count == 0) continue;
            jobs.Add(queue.Enqueue(FileActions.Prepare(folder.Children.Select(FileSource.Capture).ToArray(), target, conflict: MoveConflict.Merge)));
        }
        var all = Task.WhenAll(jobs);
        while (!all.IsCompleted)
        {
            foreach (var task in queue.Snapshot().Where(task => !task.Finished))
                Console.WriteLine($"TASK #{task.Id} {task.Phase}; copied={task.CopiedBytes}; seconds={task.Elapsed.TotalSeconds:F0}");
            await Task.WhenAny(all, Task.Delay(TimeSpan.FromSeconds(20)));
        }
        try
        {
            var results = await all;
            foreach (var result in results)
            {
                Console.WriteLine($"RESULT completed={result.Completed} errors={result.Errors.Length} denied={result.PermissionDenied}");
                foreach (var error in result.Errors) Console.WriteLine(error);
            }
            return results.All(result => result.Errors.Length == 0) ? 0 : 1;
        }
        catch (Exception error) { Console.Error.WriteLine(error.Message); return 1; }
    }

    private static async Task DirectMerge()
    {
        var source = Folder("direct-source");
        var child = Directory.CreateDirectory(Path.Join(source, "tree", "nested")).FullName;
        File.WriteAllText(Path.Join(child, "same"), "same");
        File.WriteAllText(Path.Join(child, "changed"), "new");
        File.WriteAllText(Path.Join(child, "new-file"), "new");
        File.WriteAllText(Path.Join(child, "file-vs-folder"), "file proof");
        Directory.CreateDirectory(Path.Join(child, "folder-vs-file"));
        File.WriteAllText(Path.Join(child, "folder-vs-file", "proof"), "folder proof");
        var target = Folder("direct-target");
        var existing = Directory.CreateDirectory(Path.Join(target, "tree", "nested")).FullName;
        File.WriteAllText(Path.Join(existing, "same"), "same");
        File.WriteAllText(Path.Join(existing, "changed"), "old");
        File.WriteAllText(Path.Join(existing, "target-only"), "keep");
        Directory.CreateDirectory(Path.Join(existing, "file-vs-folder"));
        File.WriteAllText(Path.Join(existing, "folder-vs-file"), "old file");
        var shallow = FolderBrowser.Read(source, None);
        Must(shallow.Children!.Single() is { IsSizeKnown: false, Children: null, SizeText: "未扫描" });
        var snapshots = shallow.Children!.Select(FileSource.Capture).ToArray();
        Must(snapshots.Single() is { Movable: true, Allowed: false, FolderTotals: null });
        await Throws<IOException>(() => Task.Run(() => FileActions.Prepare(snapshots, null, permanent: true)));
        var result = await new FileTaskQueue().Enqueue(FileActions.Prepare(snapshots, target, conflict: MoveConflict.Merge))
            .WaitAsync(TimeSpan.FromSeconds(25));
        Must(result is { Completed: 1, Errors.Length: 0 }, string.Join("; ", result.Errors));
        Must(!Directory.Exists(Path.GetDirectoryName(child)) && File.ReadAllText(Path.Join(existing, "same")) == "same" &&
            File.ReadAllText(Path.Join(existing, "changed")) == "old" && File.ReadAllText(Path.Join(existing, "new-file")) == "new" &&
            Directory.GetFiles(existing, "changed.盘镜保留-*").Single() is { } alternate && File.ReadAllText(alternate) == "new");
        Must(File.ReadAllText(Directory.GetFiles(existing, "file-vs-folder.盘镜保留-*").Single()) == "file proof" &&
            File.ReadAllText(Path.Join(Directory.GetDirectories(existing, "folder-vs-file.盘镜保留-*").Single(), "proof")) == "folder proof" &&
            File.ReadAllText(Path.Join(existing, "folder-vs-file")) == "old file");
    }

    private static async Task StreamFailure()
    {
        var source = Folder("stream-failure-source");
        var tree = Directory.CreateDirectory(Path.Join(source, "tree")).FullName;
        for (var i = 0; i < 1600; i++) File.WriteAllText(Path.Join(tree, $"small-{i}"), "fixture");
        var target = Folder("stream-failure-target");
        var plan = FileActions.Prepare(FolderBrowser.Read(source, None).Children!.Select(FileSource.Capture).ToArray(), target,
            conflict: MoveConflict.Merge);
        CrossVolumeMover.CreateNewDirectory(plan.Stage!);
        var error = await Throws<IOException>(() => StreamingMergeMover.MoveAsync(plan.Items[0], plan, plan.Stage!, null, 1,
            _ => throw new InvalidOperationException("synthetic fatal worker failure")).WaitAsync(TimeSpan.FromSeconds(10)));
        Must(error.Message.Contains("中断") && Directory.GetFiles(tree).Length == 1600);
    }

    private static async Task WorkerFailure()
    {
        var source = Folder("worker-failure-source");
        var tree = Directory.CreateDirectory(Path.Join(source, "tree")).FullName;
        for (var i = 0; i < 2048; i++) File.WriteAllText(Path.Join(tree, $"small-{i}"), "fixture");
        var target = Folder("worker-failure-target");
        var plan = FileActions.Prepare(FolderBrowser.Read(source, None).Children!.Select(FileSource.Capture).ToArray(), target,
            conflict: MoveConflict.Merge);
        Process? child = null;
        var killed = false;
        var progress = new FileTaskProgress(plan, _ =>
        {
            if (killed || child is null) return;
            killed = true;
            child.Kill();
        });
        var error = await Throws<IOException>(() => FileWorker.RunAsync(plan, progress, process => child = process)
            .WaitAsync(TimeSpan.FromSeconds(20)));
        Must(killed && error.Message.Contains("退出") && Directory.GetFiles(tree).Length == 2048);
        // A later task still works, and the terminated task has not been automatically replayed.
        var proof = Path.Join(source, "proof.txt");
        File.WriteAllText(proof, "still alive");
        var file = FolderBrowser.Read(source, None).Children!.Single(n => n.Name == "proof.txt");
        var result = await new FileTaskQueue().Enqueue(FileActions.Prepare([FileSource.Capture(file)], target));
        Must(result.Completed == 1 && result.Errors.Length == 0 && File.ReadAllText(Path.Join(target, "proof.txt")) == "still alive");
    }

    private static async Task StreamCrossVolume(string targetParent)
    {
        var source = Folder("stream-source");
        var tree = Directory.CreateDirectory(Path.Join(source, "tree")).FullName;
        var target = CrossTarget(targetParent);
        var content = Path.Join(tree, "content.txt");
        File.WriteAllText(content, "synthetic source");
        File.CreateSymbolicLink(Path.Join(tree, "relative-link"), "content.txt");
        var nested = Directory.CreateDirectory(Path.Join(tree, "inside")).FullName;
        File.WriteAllText(Path.Join(nested, "proof"), "inside");
        LocalJunction.CreateFixture(Path.Join(tree, "junction"), nested);
        var existing = Directory.CreateDirectory(Path.Join(target, "tree")).FullName;
        File.WriteAllText(Path.Join(existing, "keep"), "existing");
        var nodes = FolderBrowser.Read(source, None).Children!.Select(FileSource.Capture).ToArray();
        var plan = FileActions.Prepare(nodes, target, conflict: MoveConflict.Merge);
        var moved = await new FileTaskQueue().Enqueue(plan).WaitAsync(TimeSpan.FromSeconds(35));
        try
        {
            Must(moved is { Completed: 1, Errors.Length: 0 }, string.Join("; ", moved.Errors));
            Must(!Directory.Exists(tree) && File.ReadAllText(Path.Join(existing, "relative-link")) == "synthetic source");
            Must(new DirectoryInfo(Path.Join(existing, "junction")).LinkTarget == Path.Join(existing, "inside"));
            Must(File.ReadAllText(Path.Join(existing, "keep")) == "existing");
        }
        finally
        {
            foreach (var root in new[] { tree, existing })
            {
                if (File.Exists(Path.Join(root, "relative-link"))) File.Delete(Path.Join(root, "relative-link"));
                if (Directory.Exists(Path.Join(root, "junction"))) Directory.Delete(Path.Join(root, "junction"));
            }
        }
    }

    private static async Task StreamConcurrent(string targetParent)
    {
        var queue = new FileTaskQueue(parallelTasks: 2);
        var jobs = new List<Task<FileActionResult>>();
        var fixtures = new List<(string Source, string Target)>();
        for (var task = 0; task < 2; task++)
        {
            var source = Folder($"stream-concurrent-{task}");
            var target = CrossTarget(targetParent);
            var tree = Directory.CreateDirectory(Path.Join(source, "tree")).FullName;
            var output = Directory.CreateDirectory(Path.Join(target, "tree")).FullName;
            for (var i = 0; i < 800; i++) File.WriteAllText(Path.Join(tree, $"fixture-{i}"), $"task={task} item={i}");
            File.WriteAllText(Path.Join(tree, "same"), "same");
            File.WriteAllText(Path.Join(output, "same"), "same");
            File.WriteAllText(Path.Join(tree, "different"), "new");
            File.WriteAllText(Path.Join(output, "different"), "old");
            File.WriteAllText(Path.Join(tree, "streamed"), "main data");
            File.WriteAllText(Path.Join(tree, "streamed") + ":proof", "synthetic stream");
            File.WriteAllText(Path.Join(output, "streamed"), "main data");
            fixtures.Add((source, output));
            jobs.Add(queue.Enqueue(FileActions.Prepare(FolderBrowser.Read(source, None).Children!.Select(FileSource.Capture).ToArray(),
                target, conflict: MoveConflict.Merge)));
        }
        var results = await Task.WhenAll(jobs).WaitAsync(TimeSpan.FromSeconds(60));
        Must(results.All(r => r is { Completed: 1, Errors.Length: 0 }), string.Join("; ", results.SelectMany(r => r.Errors)));
        for (var task = 0; task < fixtures.Count; task++)
        {
            var (source, output) = fixtures[task];
            Must(!Directory.EnumerateFileSystemEntries(source).Any());
            for (var i = 0; i < 800; i++)
                Must(File.ReadAllText(Path.Join(output, $"fixture-{i}")) == $"task={task} item={i}");
            Must(File.ReadAllText(Directory.GetFiles(output, "different.盘镜保留-*").Single()) == "new" &&
                File.ReadAllText(Path.Join(output, "different")) == "old");
            var streamed = Directory.GetFiles(output, "streamed.盘镜保留-*").Single();
            Must(File.ReadAllText(streamed) == "main data" && File.ReadAllText(streamed + ":proof") == "synthetic stream");
        }
    }

    private static async Task Check(string name, Func<Task> action)
    {
        await action();
        _passed++;
        Console.WriteLine($"PASS {name}");
    }
    private static void Must(bool condition, string failure = "assertion failed") { if (!condition) throw new InvalidOperationException(failure); }
    private static async Task<T> Throws<T>(Func<Task> action) where T : Exception
    {
        try { await action(); }
        catch (T error) { return error; }
        throw new InvalidOperationException("expected " + typeof(T).Name);
    }
    private static string Folder(string name)
    {
        var path = Path.Join(_root, name);
        Directory.CreateDirectory(path);
        return path;
    }
    private static Task<ScanResult> Read(string path, int workers = 4, CancellationToken token = default) => DiskScanner.ScanAsync(path, workers, null, token);
    private static DiskNode Item(ScanResult scan, string name) => DiskQuery.Descendants(scan.Root).Single(n => n.Name == name);

    private static async Task Scan()
    {
        var path = Folder("目录统计");
        Directory.CreateDirectory(Path.Join(path, "子目录", "空目录"));
        File.WriteAllBytes(Path.Join(path, "子目录", "报告.PDF"), new byte[12345]);
        var hidden = Path.Join(path, "隐藏数据.dat");
        File.WriteAllBytes(hidden, new byte[731]);
        File.SetAttributes(hidden, File.GetAttributes(hidden) | FileAttributes.Hidden);
        File.WriteAllBytes(Path.Join(path, "空文件"), []);
        var r = await Read(path);
        Must(r.Root.Bytes == 13076 && r.Totals is { Files: 3, Directories: 3, Bytes: 13076, Skipped: 0, Inaccessible: 0 });
        Must(Item(r, "报告.PDF").FullPath == Path.Join(path, "子目录", "报告.PDF"));
        Must(Item(r, "子目录").Bytes == 12345 && Item(r, "空目录").Bytes == 0);
        var one = await Read(path, 1);
        Must(one.Totals == r.Totals);
    }

    private static async Task Junction()
    {
        var path = Folder("junction-scan");
        var child = Directory.CreateDirectory(Path.Join(path, "parent")).FullName;
        var outside = Folder("junction-target");
        File.WriteAllBytes(Path.Join(outside, "must-not-follow.txt"), new byte[400]);
        var link = Path.Join(child, "junction");
        LocalJunction.CreateFixture(link, outside);
        var dst = Folder("junction-same-drive");
        try
        {
            var r = await Read(path);
            Must(r.Totals is { Skipped: 1, Files: 0 } && r.Root.IsPartial && Item(r, "junction").IsUnavailable);
            Must(Item(r, "parent").IsPartial && r.Issues.Length == 1 && r.Issues[0].Path == link);
            await Throws<IOException>(() => Read(link));
            await Throws<IOException>(() => FileActions.RunAsync([Item(r, "junction")], dst, 0));
            await Throws<IOException>(() => Task.Run(() => FileActions.Prepare([FileSource.Capture(Item(r, "parent"))], null, permanent: true)));
            var moved = await FileActions.RunAsync([Item(r, "parent")], dst, 0);
            Must(moved is { Completed: 1, Errors.Length: 0 }, string.Join("; ", moved.Errors));
            link = Path.Join(dst, "parent", "junction");
            Must(new DirectoryInfo(link).LinkTarget == outside && File.Exists(Path.Join(outside, "must-not-follow.txt")));
        }
        finally { Directory.Delete(link); } // remove only the junction, not its target
    }

    private static async Task MoveConflicts()
    {
        var src = Folder("conflict-source");
        var dst = Folder("conflict-target");
        foreach (var pair in new[] { (src, "new"), (dst, "old") })
        {
            File.WriteAllText(Path.Join(pair.Item1, "same.txt"), pair.Item2);
            Directory.CreateDirectory(Path.Join(pair.Item1, "tree"));
            File.WriteAllText(Path.Join(pair.Item1, "tree", pair.Item2), pair.Item2);
        }
        File.WriteAllText(Path.Join(src, "free.txt"), "free");
        var scan = await Read(src);
        var snapshots = scan.Root.Children!.Select(FileSource.Capture).ToArray();
        var review = FileActions.Prepare(snapshots, dst, conflict: MoveConflict.Review);
        Must(review.Items.Count(item => item.Collision is not null) == 2);
        await Throws<IOException>(() => FileActions.RunAsync(review));
        var skip = await FileActions.RunAsync(FileActions.WithConflicts(review, MoveConflict.Skip));
        Must(skip is { Completed: 1, Skipped: 2, Errors.Length: 0 } && File.Exists(Path.Join(dst, "free.txt")));
        Must(File.ReadAllText(Path.Join(src, "same.txt")) == "new" && File.ReadAllText(Path.Join(dst, "same.txt")) == "old");
        var replacement = FileActions.WithConflicts(FileActions.Prepare(snapshots.Where(item => item.Name != "free.txt").ToArray(),
            dst, conflict: MoveConflict.Review), MoveConflict.Replace);
        var done = await FileActions.RunAsync(replacement);
        Must(done is { Completed: 2, Errors.Length: 0, Backups.Length: 2 }, string.Join("; ", done.Errors));
        Must(File.ReadAllText(Path.Join(dst, "same.txt")) == "new" && File.Exists(Path.Join(dst, "tree", "new")) &&
            !File.Exists(Path.Join(dst, "tree", "old")));
        Must(done.Backups!.Any(path => File.Exists(path) && File.ReadAllText(path) == "old") &&
            done.Backups!.Any(path => File.Exists(Path.Join(path, "old"))));
        File.WriteAllText(Path.Join(src, "same.txt"), "again");
        var file = Item(await Read(src), "same.txt");
        var stale = FileActions.Prepare([FileSource.Capture(file)], dst, conflict: MoveConflict.Replace);
        File.AppendAllText(Path.Join(dst, "same.txt"), " changed");
        var refused = await FileActions.RunAsync(stale);
        Must(refused is { Completed: 0, Errors.Length: 1, Backups.Length: 0 } && File.Exists(file.FullPath));
        var rollback = FileActions.Prepare([FileSource.Capture(file)], dst, conflict: MoveConflict.Replace);
        using (File.Open(file.FullPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var failed = await FileActions.RunAsync(rollback);
            Must(failed is { Completed: 0, Errors.Length: 1, Backups.Length: 0 }, string.Join("; ", failed.Errors));
            Must(File.ReadAllText(Path.Join(dst, "same.txt")) == "new changed");
        }
    }

    private static async Task MoveDiagnostics()
    {
        var src = Folder("diagnostic-source");
        var dst = new DirectoryInfo(Folder("diagnostic-target"));
        var file = Path.Join(src, "test.txt");
        File.WriteAllText(file, "fixture");
        var node = Item(await Read(src), "test.txt");
        using var user = WindowsIdentity.GetCurrent();
        var original = dst.GetAccessControl(AccessControlSections.Access);
        var changed = dst.GetAccessControl(AccessControlSections.Access);
        changed.AddAccessRule(new FileSystemAccessRule(user.User!, FileSystemRights.CreateFiles, AccessControlType.Deny));
        try
        {
            dst.SetAccessControl(changed);
            var result = await FileActions.RunAsync([node], dst.FullName, 0);
            Must(result is { Completed: 0, Errors.Length: 1, PermissionDenied: true }, string.Join("; ", result.Errors));
        }
        finally { dst.SetAccessControl(original); }
        using (File.Open(file, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var result = await FileActions.RunAsync([node], dst.FullName, 0);
            Must(result is { Completed: 0, PermissionDenied: false } && result.Errors.Single().Contains("占用"), string.Join("; ", result.Errors));
        }
        Must(File.Exists(file));
    }

    private static async Task CrossVolumeJunction(string targetParent)
    {
        var src = Folder("cross-link-source");
        var dst = CrossTarget(targetParent);
        var outside = Folder("cross-link-outside");
        File.WriteAllText(Path.Join(outside, "untouched"), "outside");
        var sourceTree = Directory.CreateDirectory(Path.Join(src, "tree")).FullName;
        var originalLink = Path.Join(sourceTree, "bazel-bin");
        LocalJunction.CreateFixture(originalLink, outside);
        File.WriteAllText(Path.Join(sourceTree, "content"), "new");
        var targetTree = Directory.CreateDirectory(Path.Join(dst, "tree")).FullName;
        File.WriteAllText(Path.Join(targetTree, "previous"), "old");
        var snapshots = new[] { FileSource.Capture(Item(await Read(src), "tree")) };
        var plan = FileActions.Prepare(snapshots, dst, conflict: MoveConflict.Replace);
        Must(plan.Items.Single().CrossVolume);
        var result = await FileActions.RunAsync(plan);
        var newLink = Path.Join(targetTree, "bazel-bin");
        try
        {
            Must(result is { Completed: 1, Errors.Length: 0, Backups.Length: 1 }, string.Join("; ", result.Errors));
            Must(!Directory.Exists(sourceTree) && File.ReadAllText(Path.Join(result.Backups!.Single(), "previous")) == "old");
            Must(new DirectoryInfo(newLink).LinkTarget == outside && File.ReadAllText(Path.Join(outside, "untouched")) == "outside");
        }
        finally
        {
            if (Directory.Exists(originalLink)) Directory.Delete(originalLink);
            if (Directory.Exists(newLink)) Directory.Delete(newLink);
        }
    }

    private static async Task LongPaths()
    {
        var root = Folder("long-path");
        var target = root;
        for (var i = 0; target.Length < 305; i++) target = Path.Join(target, $"{i:00}-" + new string('长', 45));
        Directory.CreateDirectory(target);
        File.WriteAllBytes(Path.Join(target, "很长.txt"), [1, 2, 3]);
        var scan = await Read(root);
        Must(scan.Root.Bytes == 3 && Item(scan, "很长.txt").FullPath == Path.Join(target, "很长.txt"));
        File.Delete(Path.Join(target, "很长.txt"));
        while (!string.Equals(target, root, StringComparison.Ordinal))
        {
            Directory.Delete(target);
            target = Path.GetDirectoryName(target)!;
        }
    }
    private static async Task Cancel()
    {
        using var stop = new CancellationTokenSource();
        stop.Cancel();
        await Throws<OperationCanceledException>(() => Read(_root, token: stop.Token));
        await Throws<IOException>(() => Read(Path.Join(_root, "missing-root")));
    }
    private static async Task Search()
    {
        var path = Folder("search");
        Directory.CreateDirectory(Path.Join(path, "APple-dir"));
        File.WriteAllText(Path.Join(path, "apple.txt"), "one");
        File.WriteAllText(Path.Join(path, "APple-dir", "APPLE.png"), "two");
        var r = await Read(path);
        var (rows, matches) = DiskQuery.Search(r.Root, "aPpLe", None, limit: 2);
        Must(matches == 3 && rows.Length == 2 && rows.All(n => n.Name.Contains("apple", StringComparison.OrdinalIgnoreCase)));
    }
    private static async Task Duplicates()
    {
        var path = Folder("duplicates");
        var same = new byte[240_000];
        same[120_000] = 9;
        var first = Path.Join(path, "original.bin");
        File.WriteAllBytes(first, same);
        File.WriteAllBytes(Path.Join(path, "真实副本.bin"), same);
        File.WriteAllBytes(Path.Join(path, "相同首尾不同中段.bin"), new byte[same.Length]);
        if (!CreateHardLink(Path.Join(path, "hardlink.bin"), first, 0)) throw new Win32Exception();
        File.WriteAllBytes(Path.Join(path, "small-a.bin"), [1, 8, 2]);
        File.WriteAllBytes(Path.Join(path, "small-b.bin"), [1, 8, 2]);
        File.WriteAllBytes(Path.Join(path, "small-no.bin"), [1, 8, 3]);
        File.WriteAllBytes(Path.Join(path, "changed.bin"), [1, 8, 2]);
        File.WriteAllBytes(Path.Join(path, "empty-a"), []);
        File.WriteAllBytes(Path.Join(path, "empty-b"), []);
        var r = await Read(path);
        File.WriteAllBytes(Path.Join(path, "changed.bin"), [1]);
        var result = DuplicateFinder.Find(r.Root, None);
        Must(result is { Candidates: 8, Unreadable: 1, HardLinkAliases: 1 } && result.Groups.Length == 2, JsonSerializer.Serialize(result with { Groups = [] }));
        Must(result.Groups[0].Files.Length == 2 && result.Groups[0].Files.Any(n => n.Name == "真实副本.bin") &&
            !result.Groups[0].Files.Any(n => n.Name == "相同首尾不同中段.bin"));
        Must(result.Groups[1].Files.Select(n => n.Name).ToHashSet().SetEquals(["small-a.bin", "small-b.bin"]));
        Must(r.Root.Bytes == 960012); // logical totals include directory entries for hardlinks
    }
    private static async Task Move()
    {
        var src = Folder("move-source");
        var dst = Folder("move-dest");
        Directory.CreateDirectory(Path.Join(src, "子文件夹"));
        File.WriteAllText(Path.Join(src, "子文件夹", "nested.txt"), "nested");
        File.WriteAllText(Path.Join(src, "移动我.txt"), "synthetic test data");
        var r = await Read(src);
        var result = await FileActions.RunAsync([Item(r, "移动我.txt"), Item(r, "子文件夹")], dst, 0);
        Must(result.Completed == 2 && result.Errors.Length == 0 && !result.Aborted, string.Join(";", result.Errors));
        Must(!File.Exists(Path.Join(src, "移动我.txt")) && !Directory.Exists(Path.Join(src, "子文件夹")));
        Must(File.ReadAllText(Path.Join(dst, "移动我.txt")) == "synthetic test data" && File.ReadAllText(Path.Join(dst, "子文件夹", "nested.txt")) == "nested");
    }
    private static async Task Reject()
    {
        var src = Folder("reject-source");
        var dst = Folder("reject-dest");
        var sub = Directory.CreateDirectory(Path.Join(src, "parent", "child")).FullName;
        File.WriteAllText(Path.Join(src, "same.txt"), "source");
        File.WriteAllText(Path.Join(dst, "same.txt"), "preserve me");
        var r = await Read(src);
        await Throws<IOException>(() => FileActions.RunAsync([Item(r, "same.txt")], dst, 0));
        Must(File.ReadAllText(Path.Join(dst, "same.txt")) == "preserve me" && File.ReadAllText(Path.Join(src, "same.txt")) == "source");
        await Throws<IOException>(() => FileActions.RunAsync([Item(r, "parent")], sub, 0));
        await Throws<IOException>(() => FileActions.RunAsync([Item(r, "parent"), Item(r, "child")], dst, 0));
        File.WriteAllText(Path.Join(src, "same.txt"), "changed since scan");
        await Throws<IOException>(() => FileActions.RunAsync([Item(r, "same.txt")], Folder("new-target"), 0));
        await Throws<IOException>(() => FileActions.RunAsync([r.Root], dst, 0));
        await Throws<IOException>(() => Task.Run(() => PathSafety.RequireUnprotected(Environment.GetFolderPath(Environment.SpecialFolder.Windows), true)));
        Must(!PathSafety.Within(Path.Join(dst + "-other", "abc"), dst));
    }
    private static async Task Metadata()
    {
        var path = Folder("do-not-upload-this-parent-name");
        File.WriteAllText(Path.Join(path, "项目.zip"), "do-not-upload-contents");
        var r = await Read(path);
        var node = Item(r, "项目.zip");
        var metadata = AiProvider.Metadata([node], new Dictionary<DiskNode, string> { [node] = "#007" });
        Must(metadata.Contains("项目.zip") && metadata.Contains("#007") && !metadata.Contains("do-not-upload"));
        node.IsSizeKnown = false;
        using (var unknown = JsonDocument.Parse(AiProvider.Metadata([node], new Dictionary<DiskNode, string>())))
        {
            var item = unknown.RootElement[0];
            Must(item.GetProperty("logicalBytes").ValueKind == JsonValueKind.Null && item.GetProperty("incomplete").GetBoolean());
        }
        Must(AiProvider.Endpoint(new("https://example.test", "m")).AbsolutePath == "/v1/chat/completions");
        Must(AiProvider.Endpoint(new("https://example.test/compatible/v1/", "m")).AbsolutePath == "/compatible/v1/chat/completions");
        Must(AiProvider.Endpoint(new("http://127.0.0.1:8888/v1/chat/completions", "m")).AbsolutePath == "/v1/chat/completions");
        foreach (var uri in new[] { "http://example.test", "https://user:token@example.test", "https://example.test/v1?key=oops", "file:///D:/test" })
            await Throws<IOException>(() => Task.Run(() => AiProvider.Endpoint(new(uri, "m"))));
    }
    private static Task Settings()
    {
        var path = Path.Join(Folder("settings"), "settings.json");
        var store = new SettingsStore(path);
        var settings = new ProviderSettings("https://example.test/v1", "demo-model", "mock-only-secret-123", true);
        store.Save(settings);
        Must(!File.ReadAllText(path).Contains(settings.ApiKey));
        Must(store.Load(out var notice) == settings && notice is null);
        store.Save(settings with { RememberKey = false });
        Must(store.Load(out notice) == settings with { RememberKey = false, ApiKey = "" } && notice is null);
        File.WriteAllText(path, "{broken}");
        Must(store.Load(out notice).ApiKey.Length == 0 && notice is not null);
        return Task.CompletedTask;
    }
    private static async Task<DiskRefresh> WaitDelta(ScanSession session, Func<DiskRefresh, bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        while (true)
        {
            var delta = await session.PrepareRefreshAsync(4, timeout.Token) ?? throw new InvalidOperationException("unexpected full refresh");
            if (predicate(delta)) return delta;
            await Task.Delay(25, timeout.Token);
        }
    }

    private static async Task IncrementalRefresh()
    {
        var path = Folder("watch-incremental");
        var from = Directory.CreateDirectory(Path.Join(path, "src", "nested")).FullName;
        var to = Directory.CreateDirectory(Path.Join(path, "dst")).FullName;
        for (var i = 0; i < 48; i++)
        {
            var quiet = Directory.CreateDirectory(Path.Join(path, $"quiet-{i:D2}", $"sub-{i:D2}")).FullName;
            File.WriteAllBytes(Path.Join(quiet, "untouched.bin"), new byte[7]);
        }
        var gone = Path.Join(from, "gone.bin");
        var moved = Path.Join(from, "move.bin");
        var grown = Path.Join(from, "resize.bin");
        File.WriteAllBytes(gone, new byte[35]); File.WriteAllBytes(moved, new byte[104]); File.WriteAllBytes(grown, new byte[80]);
        using var session = await ScanSession.OpenAsync(path, 4, null, None);
        var old = session.Result;
        var untouched = Item(old, "quiet-31");
        var untouchedChild = Item(old, "sub-31");
        var initialDelta = await session.PrepareRefreshAsync(4, None) ?? throw new InvalidOperationException("unexpected initial fallback");
        Must(session.TryCommit(initialDelta));
        Must((await WaitDelta(session, d => d.VisitedDirectories == 0)).Totals == old.Totals);
        using (var file = new FileStream(grown, FileMode.Open, FileAccess.Write)) file.SetLength(713);
        var resize = await WaitDelta(session, d => d.Totals.Bytes == old.Totals.Bytes + 633);
        Must(resize.VisitedDirectories <= 3, $"resize visited {resize.VisitedDirectories} directories");
        Must(session.TryCommit(resize));
        File.Move(moved, Path.Join(to, "moved.bin"));
        File.Delete(gone);
        var delta = await WaitDelta(session, d => d.Totals.Files == 50 && d.Totals.Bytes == 1153 &&
            d.View(Item(old, "dst"), None).Rows.Any(n => n.Name == "moved.bin"));
        Must(delta.VisitedDirectories <= 5, $"incremental visited {delta.VisitedDirectories} of {old.VisitedDirectories}");
        Must(Item(session.Result, "nested").Bytes == 852, "delta touched live nodes before commit");
        Must(session.TryCommit(delta));
        Must(Item(session.Result, "nested").Bytes == 713 && Item(session.Result, "dst").Bytes == 104);
        Must(ReferenceEquals(untouched, Item(session.Result, "quiet-31")) && ReferenceEquals(untouchedChild, Item(session.Result, "sub-31")));
        Must(session.Result.Totals == (await Read(path)).Totals, "incremental and full scan disagree");
        Directory.Move(Path.Join(path, "dst"), Path.Join(path, "renamed"));
        var rename = await WaitDelta(session, d => d.View(session.Result.Root, None).Rows.Any(n => n.Name == "renamed"));
        Must(session.TryCommit(rename));
        Must(Item(session.Result, "renamed").Bytes == 104 && session.Result.Totals == (await Read(path)).Totals);
        Console.WriteLine($"  REFRESH changed-dir-reads={delta.VisitedDirectories} baseline-dir-reads={old.VisitedDirectories}");
    }

    private static async Task PermissionRefresh()
    {
        var path = Folder("watch-acl");
        using var user = WindowsIdentity.GetCurrent();
        var deny = new FileSystemAccessRule(user.User!, FileSystemRights.ListDirectory, AccessControlType.Deny);
        var saved = new List<(DirectoryInfo Dir, byte[] Original)>();
        void Restore()
        {
            foreach (var (dir, data) in saved)
            {
                var acl = new DirectorySecurity();
                acl.SetSecurityDescriptorBinaryForm(data, AccessControlSections.Access);
                dir.SetAccessControl(acl);
            }
        }
        try
        {
            for (var i = 0; i < 36; i++)
            {
                var dir = Directory.CreateDirectory(Path.Join(path, $"restricted-{i:D2}"));
                File.WriteAllBytes(Path.Join(dir.FullName, "proof.bin"), new byte[321]);
                saved.Add((dir, dir.GetAccessControl(AccessControlSections.Access).GetSecurityDescriptorBinaryForm()));
                var denied = dir.GetAccessControl(AccessControlSections.Access);
                denied.AddAccessRule(deny);
                dir.SetAccessControl(denied);
            }
            var stable = Directory.CreateDirectory(Path.Join(path, "unaffected"));
            File.WriteAllText(Path.Join(stable.FullName, "ok.txt"), "ok");
            using var session = await ScanSession.OpenAsync(path, 4, null, None);
            var before = session.Result;
            Must(before.Totals.Inaccessible == 36 && before.Issues.Length == 32 && before.Totals.Bytes == 2, "ACL denial not reproduced");
            var quiet = Item(before, "unaffected");
            var stillDenied = await WaitDelta(session, d => d.Totals.Inaccessible == 36 && d.VisitedDirectories >= 36);
            Must(session.TryCommit(stillDenied));
            Restore();
            var recovered = await WaitDelta(session, d => d.Totals.Inaccessible == 0 && d.Totals.Bytes == 11558);
            Must(session.TryCommit(recovered));
            Must(!session.Result.Root.IsPartial && session.Result.Issues.Length == 0 && session.Result.Problems.Failed.Count == 0);
            Must(ReferenceEquals(quiet, Item(session.Result, "unaffected")));
        }
        finally { Restore(); }
    }

    private static async Task RefreshTracking()
    {
        var parent = Folder("watch-tracking");
        var path = Directory.CreateDirectory(Path.Join(parent, "root")).FullName;
        using var session = await ScanSession.OpenAsync(path, 4, null, None);
        var a = Path.Join(path, "a.txt");
        File.WriteAllText(a, "one");
        session.MarkChanged(a);
        var first = await WaitDelta(session, d => d.Totals.Bytes == 3);
        using (var canceled = new CancellationTokenSource())
        {
            canceled.Cancel();
            await Throws<OperationCanceledException>(() => session.PrepareRefreshAsync(4, canceled.Token));
        }
        File.WriteAllText(a, "two words");
        session.MarkChanged(a, replaced: false);
        Must(session.TryCommit(first));
        var second = await WaitDelta(session, d => d.Totals.Bytes == 9);
        Must(session.TryCommit(second));
        for (var i = 0; i <= ScanChanges.Limit; i++) session.MarkChanged(Path.Join(path, $"change-{i:D5}"));
        Must(await session.PrepareRefreshAsync(4, None) is null, "lost tracking reused an invalid cache");
        using var next = await ScanSession.OpenAsync(path, 4, null, None);
        Must(next.Result.Totals.Bytes == 9);
        Directory.Move(path, Path.Join(parent, "old-root"));
        Directory.CreateDirectory(path);
        Must(await next.PrepareRefreshAsync(4, None) is null, "a replaced root reused its old cache");
    }

    private static async Task ConcurrentMoves()
    {
        var source = Folder("queue-source");
        var dir = Directory.CreateDirectory(Path.Join(source, "tree")).FullName;
        File.WriteAllText(Path.Join(dir, "child.txt"), "a");
        File.WriteAllText(Path.Join(source, "independent.txt"), "b");
        File.WriteAllText(Path.Join(source, "third.txt"), "c");
        var target = Folder("queue-target");
        var another = Folder("queue-other");
        File.WriteAllText(Path.Join(another, "independent.txt"), "collision");
        var scan = await Read(source);
        var other = await Read(another);
        FileActionPlan Plan(string name, string? dest) => FileActions.Prepare([FileSource.Capture(Item(scan, name))], dest);
        var bothStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = 0;
        var queue = new FileTaskQueue(execute: async plan =>
        {
            if (Interlocked.Increment(ref started) == 2) bothStarted.TrySetResult();
            await release.Task;
            return await FileActions.RunAsync(plan);
        }, parallelTasks: 2);
        var first = queue.Enqueue(Plan("tree", target));
        var second = queue.Enqueue(Plan("independent.txt", target));
        await bothStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var third = queue.Enqueue(Plan("third.txt", target));
        Must(queue.Counts == (2, 1) && !first.IsCompleted && !second.IsCompleted);
        try
        {
            var overlappingRecycle = Plan("child.txt", null);
            var conflictingOutput = FileActions.Prepare([FileSource.Capture(Item(other, "independent.txt"))], target);
            await Throws<IOException>(() => queue.Enqueue(overlappingRecycle));
            await Throws<IOException>(() => queue.Enqueue(conflictingOutput));
        }
        finally { release.TrySetResult(); }
        var results = await Task.WhenAll(first, second, third).WaitAsync(TimeSpan.FromSeconds(10));
        Must(results.All(r => r.Completed == 1 && r.Errors.Length == 0));
        Must(queue.Counts == (0, 0) && File.ReadAllText(Path.Join(target, "tree", "child.txt")) == "a" &&
            File.ReadAllText(Path.Join(target, "independent.txt")) == "b" && File.ReadAllText(Path.Join(target, "third.txt")) == "c");
    }

    private static async Task QueuedTasks()
    {
        var source = Folder("many-tasks-source");
        var target = Folder("many-tasks-target");
        for (var i = 0; i < 40; i++) File.WriteAllText(Path.Join(source, $"file-{i:D2}"), $"fixture-{i}");
        var scan = await Read(source);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var fourStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var startCount = 0;
        var queue = new FileTaskQueue(parallelTasks: 1, executeWithProgress: async (plan, progress) =>
        {
            var count = Interlocked.Increment(ref startCount);
            firstStarted.TrySetResult();
            if (count == 4) fourStarted.TrySetResult();
            progress.Item(0).Report("测试传输", .25, 2);
            await release.Task;
            return await FileActions.RunAsync(plan, progress: progress);
        });
        var jobs = scan.Root.Children!.OrderBy(node => node.Name).Select(node =>
            queue.Enqueue(FileActions.Prepare([FileSource.Capture(node)], target))).ToArray();
        try
        {
            await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Must(queue.Counts == (1, 39), "submission count was capped");
            var snapshots = queue.Snapshot();
            Must(snapshots.Length == 40 && snapshots[1].QueuePosition == 1 && snapshots[^1].QueuePosition == 39);
            Must(snapshots.Skip(1).All(task => task.Elapsed == TimeSpan.Zero && task.Remaining is null && task.Fraction is null));
            queue.ParallelTasks = 4;
            await fourStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Must(queue.Counts == (4, 36));
            queue.ParallelTasks = 1; // Already-running tasks must not be interrupted.
            Must(queue.Counts == (4, 36));
            await Throws<IOException>(() => queue.Enqueue(FileActions.Prepare([FileSource.Capture(Item(scan, "file-39"))], target)));
        }
        finally { release.TrySetResult(); }
        var results = await Task.WhenAll(jobs).WaitAsync(TimeSpan.FromSeconds(15));
        Must(results.All(result => result.Completed == 1 && result.Errors.Length == 0) && queue.Counts == (0, 0));
        var history = queue.Snapshot();
        Must(history.Length == 32 && history.All(task => task.Finished && !task.Running && task.Fraction == 1 && task.Remaining is null));
        Must(Directory.GetFiles(source).Length == 0 && Directory.GetFiles(target).Length == 40);

        var failureSource = Folder("queue-failure");
        File.WriteAllText(Path.Join(failureSource, "fail"), "fail");
        File.WriteAllText(Path.Join(failureSource, "next"), "next");
        var failureScan = await Read(failureSource);
        var failureQueue = new FileTaskQueue(parallelTasks: 1, execute: plan =>
            plan.Items[0].Snapshot.Name == "fail" ? Task.FromException<FileActionResult>(new IOException("synthetic failure")) :
                FileActions.RunAsync(plan));
        var failed = failureQueue.Enqueue(FileActions.Prepare([FileSource.Capture(Item(failureScan, "fail"))], target));
        var next = failureQueue.Enqueue(FileActions.Prepare([FileSource.Capture(Item(failureScan, "next"))], target));
        await Throws<IOException>(() => failed);
        Must((await next.WaitAsync(TimeSpan.FromSeconds(5))).Completed == 1 && failureQueue.Counts == (0, 0));
        var failure = failureQueue.Snapshot().Single(task => task.Label.Contains("fail"));
        Must(failure.Finished && failure.Fraction < 1 && failure.Detail == "synthetic failure");
    }

    private static Task ProgressEstimates()
    {
        var estimate = new FileTaskEstimate();
        estimate.Report(TimeSpan.FromSeconds(100), null, 0);
        Must(estimate.Get(TimeSpan.FromSeconds(100)).Remaining is null);
        estimate.Report(TimeSpan.FromSeconds(101), .1, 100);
        Must(estimate.Get(TimeSpan.FromSeconds(101)).Remaining is null);
        estimate.Report(TimeSpan.FromSeconds(103), .3, 300);
        var known = estimate.Get(TimeSpan.FromSeconds(103));
        Must(Math.Abs(known.Remaining!.Value.TotalSeconds - 7) < .01 && Math.Abs(known.BytesPerSecond - 100) < .01);
        Must(estimate.Get(TimeSpan.FromSeconds(109)).Remaining is null);
        estimate.Report(TimeSpan.FromSeconds(110), null, 300);
        Must(estimate.Get(TimeSpan.FromSeconds(110)).Remaining is null);
        estimate.Report(TimeSpan.FromSeconds(112), .4, 400);
        Must(estimate.Get(TimeSpan.FromSeconds(112)).Remaining is null);
        return Task.CompletedTask;
    }

    private static async Task CrossVolumeProgress(string targetParent)
    {
        var source = Folder("copy-progress");
        var tree = Directory.CreateDirectory(Path.Join(source, "tree")).FullName;
        for (var i = 0; i < 300; i++) File.WriteAllBytes(Path.Join(tree, $"small-{i:D4}"), new byte[4096]);
        using (var large = File.Create(Path.Join(source, "large.bin"))) large.SetLength(32 * 1024 * 1024);
        var target = CrossTarget(targetParent);
        var scan = await Read(source);
        var frames = new ConcurrentQueue<FileProgressFrame>();
        var plan = FileActions.Prepare(scan.Root.Children!.Select(FileSource.Capture).ToArray(), target);
        var queue = new FileTaskQueue(executeWithProgress: async (action, progress) =>
        {
            // Run native progress through both the queue and an evidence collector.
            var collection = new FileTaskProgress(action, frame =>
            {
                frames.Enqueue(frame);
                progress.Item(0).Report(frame.Phase, frame.Fraction, frame.CopiedBytes);
            });
            return await FileActions.RunAsync(action, progress: collection);
        });
        var result = await queue.Enqueue(plan).WaitAsync(TimeSpan.FromSeconds(35));
        Must(result is { Completed: 2, Errors.Length: 0 }, string.Join("; ", result.Errors));
        Must(frames.Any(frame => frame.Fraction is > 0 and < 1) && frames.All(frame => frame.Fraction is null or >= 0 and < 1));
        Must(frames.Max(frame => frame.CopiedBytes) == scan.Totals.Bytes, "native copied byte count mismatch");
        var finished = queue.Snapshot().Single();
        Must(finished is { Finished: true, Fraction: 1, Remaining: null } && !Directory.Exists(tree) &&
            new FileInfo(Path.Join(target, "large.bin")).Length == 32 * 1024 * 1024);
    }

    private static async Task DeletePermanent()
    {
        var path = Folder("delete-permanent");
        var tree = Directory.CreateDirectory(Path.Join(path, "selected", "nested")).FullName;
        Parallel.For(0, 360, i => File.WriteAllText(Path.Join(tree, $"small-{i:D4}.txt"), $"synthetic {i}"));
        var single = Path.Join(path, "one.txt");
        File.WriteAllText(single, "one");
        File.SetAttributes(single, FileAttributes.ReadOnly);
        var locked = Path.Join(path, "locked.txt");
        File.WriteAllText(locked, "locked");
        var before = await Read(path);
        var stale = FileActions.Prepare([FileSource.Capture(Item(before, "selected"))], null, permanent: true);
        // No change to top-level folder timestamp, file count or byte count.
        File.Move(Path.Join(tree, "small-0000.txt"), Path.Join(tree, "changed.txt"));
        var refused = await FileActions.RunAsync(stale);
        Must(refused.Completed == 0 && refused.Errors.Length == 1 && Directory.GetFiles(tree).Length == 360);
        var fresh = await Read(path);
        var nodes = new[] { "one.txt", "locked.txt", "selected" }.Select(name => FileSource.Capture(Item(fresh, name))).ToArray();
        var plan = FileActions.Prepare(nodes, null, permanent: true);
        using var handle = new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.Read);
        var result = await FileActions.RunAsync(plan);
        Must(result.Completed == 2 && result.Errors.Length == 1, string.Join(";", result.Errors));
        Must(!File.Exists(single) && !Directory.Exists(Path.GetDirectoryName(tree)) && File.Exists(locked));
    }

    private static async Task Models()
    {
        using var endpoint = new Server();
        var serve = endpoint.Reply(200, "{\"data\":[{\"id\":\"Case/Model-A\"},{\"id\":\"case/model-a\"},{\"id\":\"Case/Model-A\"},{\"id\":null},{\"id\":\"\"},5]}");
        var models = await AiProvider.ListModelsAsync(new(endpoint.Address + "/gateway/v9/chat/completions", "", "dummy-list-key"), None);
        var request = await serve;
        Must(models.SequenceEqual(["Case/Model-A", "case/model-a"]));
        Must(request.Headers.StartsWith("GET /gateway/v9/models HTTP/") && request.Body == "" &&
            request.Headers.Contains("Authorization: Bearer dummy-list-key", StringComparison.OrdinalIgnoreCase));
        using var empty = new Server();
        var response = empty.Reply(200, "{\"data\":[]}");
        Must((await AiProvider.ListModelsAsync(new(empty.Address + "/v1/models"), None)).Length == 0);
        Must((await response).Headers.StartsWith("GET /v1/models HTTP/"));
        using var invalid = new Server();
        var missing = invalid.Reply(404, "dummy-list-key");
        var rejected = await Throws<IOException>(() => AiProvider.ListModelsAsync(new(invalid.Address, "", "dummy-list-key"), None));
        Must(rejected.Message.Contains("404") && !rejected.Message.Contains("dummy-list-key"));
        Must((await missing).Headers.StartsWith("GET /v1/models HTTP/"));
        using var sink = new Server();
        using var redirect = new Server();
        var attempt = redirect.Reply(307, "", $"Location: {sink.Address}/v1/models\r\n");
        await Throws<IOException>(() => AiProvider.ListModelsAsync(new(redirect.Address, "", "dummy-list-key"), None));
        await attempt;
        Must(!sink.Pending, "model request followed redirect");
    }

    private static async Task HttpSuccess()
    {
        using var endpoint = new Server();
        var serve = endpoint.Reply(200, "{\"choices\":[{\"message\":{\"content\":\"建议保留\"}}]}");
        var answer = await AiProvider.SuggestAsync(new(endpoint.Address, "test-id", "dummy-local-test-key"), "[{\"name\":\"测试.bin\"}]", None);
        var request = await serve;
        Must(answer == "建议保留" && request.Headers.StartsWith("POST /v1/chat/completions HTTP/"));
        Must(request.Headers.Contains("Authorization: Bearer dummy-local-test-key", StringComparison.OrdinalIgnoreCase));
        using var json = JsonDocument.Parse(request.Body);
        var data = json.RootElement;
        Must(data.GetProperty("model").GetString() == "test-id" && !data.GetProperty("stream").GetBoolean() &&
            data.GetProperty("messages")[1].GetProperty("content").GetString()!.Contains("测试.bin"));
        using var probe = new Server();
        var testReply = probe.Reply(200, "{\"choices\":[{\"message\":{\"content\":\"OK\"}}]}");
        await AiProvider.TestAsync(new(probe.Address, "no-auth"), None);
        var testRequest = await testReply;
        Must(!testRequest.Headers.Contains("Authorization", StringComparison.OrdinalIgnoreCase) && !testRequest.Body.Contains("测试.bin"));
    }
    private static async Task HttpErrors()
    {
        using var unauthorized = new Server();
        var serve = unauthorized.Reply(401, "dangerous-echo-of-key");
        var ex = await Throws<IOException>(() => AiProvider.TestAsync(new(unauthorized.Address, "m", "dangerous-echo-of-key"), None));
        Must(ex.Message.Contains("401") && !ex.Message.Contains("dangerous-echo"));
        await serve;
        using var malformed = new Server();
        var bad = malformed.Reply(200, "not-json");
        await Throws<IOException>(() => AiProvider.TestAsync(new(malformed.Address, "m"), None));
        await bad;
    }
    private static async Task HttpRedirect()
    {
        using var first = new Server();
        using var target = new Server();
        var serve = first.Reply(307, "", "Location: " + target.Address + "\r\n");
        var ex = await Throws<IOException>(() => AiProvider.TestAsync(new(first.Address, "m", "never-forward-this"), None));
        await serve;
        Must(ex.Message.Contains("307") && !target.Pending);
        using var stop = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));
        await Throws<OperationCanceledException>(() => AiProvider.TestAsync(new(target.Address, "m"), stop.Token));
    }

    private static async Task Recycle()
    {
        var src = Folder("recycle");
        var name = "DiskLens-self-test-" + Guid.NewGuid().ToString("N") + ".txt";
        var path = Path.Join(src, name);
        File.WriteAllText(path, "synthetic Recycle Bin test");
        var r = await Read(src);
        var result = await FileActions.RunAsync([Item(r, name)], null, 0).WaitAsync(TimeSpan.FromSeconds(35));
        Must(result.Completed == 1 && !result.Aborted && !File.Exists(path), string.Join(";", result.Errors));
        dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application", throwOnError: true)!)!;
        try
        {
            bool found = false;
            foreach (dynamic item in shell.NameSpace(10).Items())
                if (string.Equals((string)item.Name, name, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals((string)item.Name, Path.GetFileNameWithoutExtension(name), StringComparison.OrdinalIgnoreCase)) { found = true; break; }
            Must(found, "synthetic file was not visible in the Windows Recycle Bin");
        }
        finally { Marshal.ReleaseComObject((object)shell); }
    }
    private static string CrossTarget(string targetParent)
    {
        var dst = Path.Join(Path.GetFullPath(targetParent), "destination-" + Guid.NewGuid().ToString("N"));
        Must(!string.Equals(Path.GetPathRoot(_root), Path.GetPathRoot(dst), StringComparison.OrdinalIgnoreCase));
        Directory.CreateDirectory(dst);
        return dst;
    }

    private static async Task CrossVolume(string targetParent)
    {
        var src = Folder("cross-volume");
        var dst = CrossTarget(targetParent);
        var sourceTree = Directory.CreateDirectory(Path.Join(src, "测试文件夹", "小文件")).FullName;
        Parallel.For(0, 640, i => File.WriteAllText(Path.Join(sourceTree, $"数据-{i:D4}.txt"), $"跨卷数据 {i}"));
        var proof = Path.Join(src, "readonly.bin");
        File.WriteAllText(proof, "synthetic cross-volume data");
        var targetRoot = Path.GetPathRoot(dst)!;
        var ntfs = new DriveInfo(targetRoot).DriveFormat == "NTFS" && new DriveInfo(Path.GetPathRoot(src)!).DriveFormat == "NTFS";
        if (ntfs) File.WriteAllText(proof + ":disklens-proof", "synthetic named stream");
        File.SetAttributes(proof, FileAttributes.ReadOnly);
        var scan = await Read(src);
        var result = await FileActions.RunAsync([Item(scan, "测试文件夹"), Item(scan, "readonly.bin")], dst, 0).WaitAsync(TimeSpan.FromSeconds(35));
        Must(result.Completed == 2 && !Directory.EnumerateFileSystemEntries(src).Any(), string.Join(";", result.Errors));
        var finalTree = Path.Join(dst, "测试文件夹", "小文件");
        Must(Directory.EnumerateFiles(finalTree).Count() == 640 && Enumerable.Range(0, 640).All(i =>
            File.ReadAllText(Path.Join(finalTree, $"数据-{i:D4}.txt")) == $"跨卷数据 {i}"));
        var copied = Path.Join(dst, "readonly.bin");
        Must(File.ReadAllText(copied) == "synthetic cross-volume data" && (File.GetAttributes(copied) & FileAttributes.ReadOnly) != 0);
        if (ntfs) Must(File.ReadAllText(copied + ":disklens-proof") == "synthetic named stream");
        Must(Directory.EnumerateFileSystemEntries(dst).Count() == 2, "move staging directory leaked");
        File.SetAttributes(copied, FileAttributes.Normal);
        File.Delete(copied);
        foreach (var file in Directory.EnumerateFiles(finalTree)) File.Delete(file);
        Directory.Delete(finalTree);
        Directory.Delete(Path.GetDirectoryName(finalTree)!);
        Directory.Delete(dst);
    }

    private static async Task CrossVolumeFailures(string targetParent)
    {
        var src = Folder("cross-volume-errors");
        var tree = Directory.CreateDirectory(Path.Join(src, "tree")).FullName;
        var locked = Path.Join(tree, "locked.txt");
        var free = Path.Join(tree, "copiable.txt");
        File.WriteAllText(locked, "locked data");
        File.WriteAllText(free, "copiable data");
        var dst = CrossTarget(targetParent);
        var scan = await Read(src);
        var node = Item(scan, "tree");
        using (var handle = new FileStream(locked, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var plan = FileActions.Prepare([FileSource.Capture(node)], dst);
            var blocked = await FileActions.RunAsync(plan).WaitAsync(TimeSpan.FromSeconds(15));
            Must(blocked.Completed == 0 && blocked.Errors.Length == 1 && !File.Exists(free) &&
                File.ReadAllText(Path.Join(dst, "tree", "copiable.txt")) == "copiable data" && File.Exists(locked));
        }
        var retry = FileActions.Prepare(FolderBrowser.Read(src, None).Children!.Select(FileSource.Capture).ToArray(), dst,
            conflict: MoveConflict.Merge);
        var resumed = await FileActions.RunAsync(retry);
        Must(resumed.Completed == 1 && resumed.Errors.Length == 0 && !Directory.Exists(tree) &&
            File.ReadAllText(Path.Join(dst, "tree", "locked.txt")) == "locked data");
        // A newly appearing root is not silently merged by the no-conflict path.
        src = Folder("cross-volume-late-root");
        tree = Directory.CreateDirectory(Path.Join(src, "tree")).FullName;
        locked = Path.Join(tree, "locked.txt");
        File.WriteAllText(locked, "locked data");
        dst = CrossTarget(targetParent);
        var late = FileActions.Prepare(FolderBrowser.Read(src, None).Children!.Select(FileSource.Capture).ToArray(), dst);
        var collision = Directory.CreateDirectory(Path.Join(dst, "tree")).FullName;
        var keep = Path.Join(collision, "kept.txt");
        File.WriteAllText(keep, "do not replace");
        var refused = await FileActions.RunAsync(late).WaitAsync(TimeSpan.FromSeconds(15));
        Must(refused.Completed == 0 && refused.Errors.Length == 1 && File.ReadAllText(keep) == "do not replace" &&
            File.ReadAllText(locked) == "locked data" && Directory.EnumerateFileSystemEntries(dst).Count() == 1);
        File.Delete(keep);
        Directory.Delete(collision);
        if (new DriveInfo(Path.GetPathRoot(src)!).DriveFormat == "NTFS")
        {
            File.WriteAllText(tree + ":disklens-proof", "directory stream");
            var withStream = await Read(src);
            var result = await FileActions.RunAsync([Item(withStream, "tree")], dst, 0);
            Must(result.Completed == 0 && result.Errors.Length == 1 && File.ReadAllText(tree + ":disklens-proof") == "directory stream" &&
                File.Exists(locked) && !Directory.EnumerateFileSystemEntries(dst).Any());
        }
        Directory.Delete(dst);
    }

    private static async Task FileBenchmark(string targetParent)
    {
        const int count = 4096;
        var observations = new List<(int Workers, double Move, double Delete)>();
        for (var iteration = 0; iteration < 6; iteration++)
        {
            var workers = iteration % 2 == 0 ? 1 : 4;
            var src = Folder($"file-bench-{iteration}");
            var dst = CrossTarget(targetParent);
            var data = new byte[4096];
            var tree = Directory.CreateDirectory(Path.Join(src, "payload")).FullName;
            Parallel.For(0, 16, bucket =>
            {
                var dir = Directory.CreateDirectory(Path.Join(tree, $"group-{bucket:D2}")).FullName;
                for (var i = 0; i < count / 16; i++) File.WriteAllBytes(Path.Join(dir, $"small-{i:D3}.bin"), data);
            });
            var scan = await Read(src);
            var plan = FileActions.Prepare([FileSource.Capture(Item(scan, "payload"))], dst);
            var watch = Stopwatch.StartNew();
            var mover = new CrossVolumeMover(plan.Stage!, workers);
            try { mover.Move(plan.Items[0]); }
            finally { mover.CleanUp(); }
            var move = watch.Elapsed.TotalMilliseconds;
            Must(!Directory.Exists(tree) && mover.CleanupError is null);
            var installed = await Read(dst);
            Must(installed.Totals.Files == count && installed.Totals.Bytes == count * data.Length);
            var delete = FileActions.Prepare([FileSource.Capture(Item(installed, "payload"))], null, permanent: true);
            watch.Restart();
            var deleted = await PermanentDeletion.RunAsync(delete, workers);
            var elapsed = watch.Elapsed.TotalMilliseconds;
            Must(deleted.Completed == 1 && deleted.Errors.Length == 0 && !Directory.EnumerateFileSystemEntries(dst).Any());
            Directory.Delete(dst);
            observations.Add((workers, move, elapsed));
            Console.WriteLine($"  SMALL-FILES workers={workers} files={count} move_ms={move:F1} delete_ms={elapsed:F1}");
        }
        foreach (var group in observations.GroupBy(x => x.Workers))
            Console.WriteLine($"  MEDIAN workers={group.Key} move_ms={group.Select(x => x.Move).Order().ElementAt(1):F1} delete_ms={group.Select(x => x.Delete).Order().ElementAt(1):F1}");
    }

    private static async Task Benchmark()
    {
        var root = Folder("benchmark");
        var dirs = Enumerable.Range(0, 160).Select(i => Directory.CreateDirectory(Path.Join(root, $"set-{i:D4}")).FullName).ToArray();
        Parallel.ForEach(dirs, new ParallelOptions { MaxDegreeOfParallelism = 4 }, dir =>
        {
            for (int j = 0; j < 320; j++)
                using (var file = File.Create(Path.Join(dir, $"fixture-{j:D4}.bin"))) file.SetLength(3072);
        });
        for (int i = 0; i < 4; i++)
        {
            var r = await Read(root, i == 0 ? 1 : 4);
            Must(r.Totals is { Files: 51200, Directories: 161, Bytes: 157286400, Skipped: 0, Inaccessible: 0 });
            Console.WriteLine($"BENCH run={i + 1} workers={(i == 0 ? 1 : 4)} files={r.Totals.Files} elapsed_ms={r.Elapsed.TotalMilliseconds:F1}");
        }
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLink(string newPath, string existing, IntPtr reserved);

    private sealed class Server : IDisposable
    {
        private readonly TcpListener _socket = new(IPAddress.Loopback, 0);
        public Server() => _socket.Start();
        public string Address => $"http://127.0.0.1:{((IPEndPoint)_socket.LocalEndpoint).Port}";
        public bool Pending => _socket.Pending();
        public async Task<(string Headers, string Body)> Reply(int code, string payload, string additionalHeaders = "")
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            using var client = await _socket.AcceptTcpClientAsync(timeout.Token);
            using var stream = client.GetStream();
            var header = new List<byte>();
            var single = new byte[1];
            while (header.Count < 32_768)
            {
                await stream.ReadExactlyAsync(single, timeout.Token);
                header.Add(single[0]);
                if (header.Count >= 4 && header[^4] == 13 && header[^3] == 10 && header[^2] == 13 && header[^1] == 10) break;
            }
            var text = Encoding.ASCII.GetString(header.ToArray());
            var lengthHeader = text.Split("\r\n").SingleOrDefault(l => l.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase));
            var len = lengthHeader is null ? 0 : int.Parse(lengthHeader.Split(':')[1]);
            var request = new byte[len];
            await stream.ReadExactlyAsync(request, timeout.Token);
            var data = Encoding.UTF8.GetBytes(payload);
            var response = Encoding.ASCII.GetBytes($"HTTP/1.1 {code} Response\r\nContent-Type: application/json\r\nContent-Length: {data.Length}\r\n{additionalHeaders}Connection: close\r\n\r\n");
            await stream.WriteAsync(response, timeout.Token);
            await stream.WriteAsync(data, timeout.Token);
            return (text, Encoding.UTF8.GetString(request));
        }
        public void Dispose() => _socket.Stop();
    }
}
