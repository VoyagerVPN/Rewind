using System.IO;

namespace Rewind.Preview;

internal sealed record FfmpegTools(string Ffmpeg, string Ffprobe)
{
    internal static string LocalAppData => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    internal static FfmpegTools WinGetLinks(string localAppData) =>
        At(Path.Combine(localAppData, "Microsoft", "WinGet", "Links"));

    internal static FfmpegTools? Find(string applicationDirectory) =>
        Find(applicationDirectory, LocalAppData, Environment.GetEnvironmentVariable("PATH"));

    internal static FfmpegTools? Find(string applicationDirectory, string localAppData, string? searchPath)
    {
        var bundled = At(Path.Combine(applicationDirectory, "tools"));
        if (bundled.Exists) return bundled;

        var winget = WinGetLinks(localAppData);
        if (winget.Exists) return winget;

        foreach (string entry in (searchPath ?? "").Split(Path.PathSeparator))
        {
            string directory = entry.Trim().Trim('"');
            if (!Path.IsPathFullyQualified(directory)) continue;
            var tools = At(directory);
            if (tools.Exists) return tools;
        }
        return null;
    }

    internal static async Task<bool> EnsureAsync(Func<FfmpegTools?> find, Func<Task> install)
    {
        if (find() is not null) return true;
        await install();
        return find() is not null;
    }

    private bool Exists => File.Exists(Ffmpeg) && File.Exists(Ffprobe);

    private static FfmpegTools At(string directory) =>
        new(Path.Combine(directory, "ffmpeg.exe"), Path.Combine(directory, "ffprobe.exe"));
}
