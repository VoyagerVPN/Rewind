using System.Diagnostics;
using System.Text;

namespace Rewind.Preview;

internal static class CaptureProcesses
{
    private static readonly FfmpegTools Tools = FfmpegTools.Find(AppContext.BaseDirectory) ?? FfmpegTools.WinGetLinks(FfmpegTools.LocalAppData);
    public static string Ffmpeg => Tools.Ffmpeg;
    public static string Ffprobe => Tools.Ffprobe;

    public static Process Start(string executable, IEnumerable<string> arguments)
    {
        var info = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true
        };
        foreach (string argument in arguments) info.ArgumentList.Add(argument);
        return Process.Start(info) ?? throw new InvalidOperationException(Localization.Text("Message.CouldNotStartFfmpeg"));
    }

    public static async Task<string> RunAsync(string executable, IEnumerable<string> arguments, TimeSpan timeout, CancellationToken cancellation = default, Action<string>? nonFatalDiagnostics = null,
        IReadOnlyList<string>? inputFiles = null)
    {
        using Process process = Start(executable, arguments);
        Task<string> output = ReadBoundedAsync(process.StandardOutput, 2 * 1024 * 1024);
        Task<string> error = ReadBoundedAsync(process.StandardError, 16000);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(timeout);
        Task input = inputFiles is null ? Task.CompletedTask : WriteInputFilesAsync(process, inputFiles, deadline.Token);
        try { await Task.WhenAll(process.WaitForExitAsync(deadline.Token), input); }
        catch
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            await Task.WhenAll(output, error);
            throw;
        }
        string stderr = await error;
        string stdout = await output;
        if (process.ExitCode != 0 || !string.IsNullOrWhiteSpace(stderr) && nonFatalDiagnostics is null)
            throw new InvalidOperationException($"{System.IO.Path.GetFileName(executable)} ({process.ExitCode}): {stderr}");
        if (!string.IsNullOrWhiteSpace(stderr)) nonFatalDiagnostics?.Invoke(stderr);
        return stdout;
    }

    private static async Task WriteInputFilesAsync(Process process, IReadOnlyList<string> files, CancellationToken cancellation)
    {
        byte[] block = new byte[256 * 1024];
        try
        {
            foreach (string file in files)
            {
                await using var source = new System.IO.FileStream(file, System.IO.FileMode.Open, System.IO.FileAccess.Read,
                    System.IO.FileShare.Read | System.IO.FileShare.Delete, block.Length, System.IO.FileOptions.Asynchronous | System.IO.FileOptions.SequentialScan);
                int count;
                while ((count = await source.ReadAsync(block, cancellation)) > 0)
                {
                    try { await process.StandardInput.BaseStream.WriteAsync(block.AsMemory(0, count), cancellation); }
                    catch (System.IO.IOException)
                    {
                        // Ограничение -t может закончить экспорт раньше хвоста последнего зарезервированного файла.
                        await process.WaitForExitAsync(cancellation);
                        // При отказе процесса RunAsync вернёт код выхода и stderr вместо ошибки закрытого stdin.
                        return;
                    }
                }
            }
        }
        finally
        {
            try { process.StandardInput.Close(); }
            catch (Exception error) when (error is System.IO.IOException or ObjectDisposedException) { }
        }
    }

    private static async Task<string> ReadBoundedAsync(System.IO.StreamReader reader, int capacity)
    {
        var text = new StringBuilder();
        char[] block = new char[4096];
        int count;
        while ((count = await reader.ReadAsync(block)) > 0)
        {
            text.Append(block, 0, count);
            if (text.Length > capacity) text.Remove(0, text.Length - capacity);
        }
        return text.ToString();
    }
}
