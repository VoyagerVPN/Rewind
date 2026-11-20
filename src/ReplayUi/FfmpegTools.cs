using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;

namespace Rewind.Preview;

internal sealed record FfmpegTools(string Ffmpeg, string Ffprobe)
{
    internal static readonly Version MinimumVersion = new(9, 1);
    internal static string LocalAppData => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    private static FfmpegTools WinGetLinks(string localAppData) =>
        At(Path.Combine(localAppData, "Microsoft", "WinGet", "Links"));

    internal static FfmpegTools? Find(string applicationDirectory) =>
        Find(applicationDirectory, LocalAppData, Environment.GetEnvironmentVariable("PATH"));

    internal static FfmpegTools? Find(string applicationDirectory, string localAppData, string? searchPath,
        Func<string, string?>? versionOutput = null)
    {
        versionOutput ??= executable => ReadVersionOutputAsync(executable).GetAwaiter().GetResult();
        var bundled = At(Path.Combine(applicationDirectory, "tools"));
        if (bundled.IsSupported(versionOutput)) return bundled;

        var winget = WinGetLinks(localAppData);
        if (winget.IsSupported(versionOutput)) return winget;

        foreach (string entry in (searchPath ?? "").Split(Path.PathSeparator))
        {
            string directory = entry.Trim().Trim('"');
            if (!Path.IsPathFullyQualified(directory)) continue;
            var tools = At(directory);
            if (tools.IsSupported(versionOutput)) return tools;
        }
        return null;
    }

    internal static async Task<bool> EnsureAsync(Func<FfmpegTools?> find, Func<Task> install)
    {
        if (await Task.Run(find) is not null) return true;
        await install();
        return await Task.Run(find) is not null;
    }

    private bool IsSupported(Func<string, string?> versionOutput) =>
        File.Exists(Ffmpeg) && File.Exists(Ffprobe) &&
        MeetsMinimum(versionOutput(Ffmpeg), "ffmpeg") && MeetsMinimum(versionOutput(Ffprobe), "ffprobe");

    private static bool MeetsMinimum(string? banner, string tool)
    {
        string prefix = tool + " version ";
        if (banner is null || !banner.StartsWith(prefix, StringComparison.Ordinal)) return false;
        var version = Regex.Match(banner[prefix.Length..], @"^n?([0-9]+\.[0-9]+(?:\.[0-9]+)?)(?=[\s+-]|$)");
        return version.Success && Version.TryParse(version.Groups[1].Value, out Version? parsed) && parsed >= MinimumVersion;
    }

    private static async Task<string?> ReadVersionOutputAsync(string executable)
    {
        var start = new ProcessStartInfo(executable, "-version")
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
        };
        try
        {
            using Process? process = Process.Start(start);
            if (process is null) return null;
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(4));
            try
            {
                Task<string> output = process.StandardOutput.ReadToEndAsync(deadline.Token);
                Task errors = process.StandardError.BaseStream.CopyToAsync(Stream.Null, deadline.Token);
                await Task.WhenAll(output, errors, process.WaitForExitAsync(deadline.Token)).ConfigureAwait(false);
                return process.ExitCode == 0 ? output.Result : null;
            }
            catch (OperationCanceledException)
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                return null;
            }
        }
        catch (Exception error) when (error is Win32Exception or IOException or InvalidOperationException)
        {
            return null;
        }
    }

    private static FfmpegTools At(string directory) =>
        new(Path.Combine(directory, "ffmpeg.exe"), Path.Combine(directory, "ffprobe.exe"));
}
