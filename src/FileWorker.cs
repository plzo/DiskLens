using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace DiskLens;

// The desktop process never executes native move callbacks. Worker crashes become an ordinary failed task.
internal static class FileWorker
{
    private sealed record Message(FileProgressFrame? Progress = null, FileActionResult? Result = null);
    private static readonly JsonSerializerOptions Json = new() { IncludeFields = true };

    public static async Task<FileActionResult> RunAsync(FileActionPlan plan, FileTaskProgress progress, Action<Process>? started = null)
    {
        var host = Environment.ProcessPath!;
        var dotnet = string.Equals(Path.GetFileName(host), "dotnet.exe", StringComparison.OrdinalIgnoreCase);
        var start = new ProcessStartInfo(dotnet || Path.GetFileName(host).Equals("DiskLens.exe", StringComparison.OrdinalIgnoreCase)
            ? host : Path.Join(AppContext.BaseDirectory, "DiskLens.exe"))
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true,
            RedirectStandardError = true, StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = Encoding.UTF8 };
        if (dotnet) start.ArgumentList.Add(typeof(App).Assembly.Location);
        start.ArgumentList.Add("--file-worker");
        using var child = Process.Start(start) ?? throw new IOException("无法启动移动工作进程。");
        var drain = Task.Run(async () => { var buffer = new char[2048]; while (await child.StandardError.ReadAsync(buffer) > 0) { } });
        FileActionResult? result = null;
        Exception? protocolError = null;
        try
        {
            await child.StandardInput.WriteLineAsync(JsonSerializer.Serialize(plan, Json)).ConfigureAwait(false);
            child.StandardInput.Close();
            started?.Invoke(child);
            while (await child.StandardOutput.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                Message? message;
                try { message = JsonSerializer.Deserialize<Message>(line, Json); }
                catch (JsonException error) { protocolError ??= error; continue; }
                if (message?.Progress is { } frame) progress.AcceptFrame(frame);
                if (message?.Result is { } completed) result = completed;
            }
        }
        catch (IOException error) { protocolError = error; }
        // Do not release path claims while a worker may still be moving files, even after a pipe error.
        await child.WaitForExitAsync().ConfigureAwait(false);
        await drain.ConfigureAwait(false);
        if (protocolError is not null)
            throw new IOException("工作进程通信中断；请核对源和目标，不会自动重放任务。", protocolError);
        if (result is null || child.ExitCode != 0)
            throw new IOException($"移动工作进程异常退出（0x{child.ExitCode:X8}），主界面仍可用。已发布的副本保留；请刷新后重新选择未移动的项，合并会核对已有副本。");
        return result;
    }

    public static async Task<int> ServeAsync()
    {
        Console.InputEncoding = Encoding.UTF8;
        Console.OutputEncoding = new UTF8Encoding(false);
        var gate = new object();
        var disconnected = false;
        void Send(Message message)
        {
            lock (gate)
            {
                if (disconnected) return;
                try { Console.WriteLine(JsonSerializer.Serialize(message, Json)); Console.Out.Flush(); }
                catch (IOException) { disconnected = true; }
            }
        }
        try
        {
            var line = await Console.In.ReadLineAsync().ConfigureAwait(false);
            if (line is null || line.Length > 2 * 1024 * 1024) return 2;
            var plan = JsonSerializer.Deserialize<FileActionPlan>(line, Json) ?? throw new IOException("没有收到移动任务。");
            if (plan.Kind != FileActionKind.Move || plan.Items.Length is 0 or > FileActions.BatchLimit)
                throw new IOException("工作进程仅处理明确提交的移动任务。");
            var progress = new FileTaskProgress(plan, frame => Send(new(Progress: frame)));
            var result = await FileActions.RunAsync(plan, progress: progress).ConfigureAwait(false);
            Send(new(Result: result));
            return 0;
        }
        catch (Exception error) when (FileActions.IsActionError(error) || error is JsonException or ArgumentException or InvalidOperationException)
        {
            Send(new(Result: new(0, false, [FileActions.MoveError(error)], FileActions.IsAccessDenied(error))));
            return 0;
        }
    }
}
