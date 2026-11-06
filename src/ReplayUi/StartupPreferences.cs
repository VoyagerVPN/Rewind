using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Rewind.Preview;

public sealed record StartupPreferences(bool AlwaysStartRecording = false, bool LastRecordingEnabled = false)
{
    [JsonIgnore] public bool ShouldStartRecording => AlwaysStartRecording || LastRecordingEnabled;
}

public sealed class StartupPreferencesFile(string path)
{
    public StartupPreferences Load() => !File.Exists(path) ? new() :
        JsonSerializer.Deserialize<StartupPreferences>(File.ReadAllText(path)) ?? throw new InvalidDataException("Не удалось прочитать параметры запуска.");

    public void Save(StartupPreferences preferences)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path + ".new", JsonSerializer.Serialize(preferences, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(path + ".new", path, overwrite: true);
    }
}

public sealed record AppLaunchOptions(string Manifest, string DataDirectory, bool Background, string? Section)
{
    public string? ReplayDirectory { get; init; }

    public static string InstanceId(string directory) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
        System.Text.Encoding.UTF8.GetBytes(Path.GetFullPath(directory).ToUpperInvariant())))[..16];

    public static AppLaunchOptions Parse(IReadOnlyList<string> arguments, string applicationDirectory)
    {
        string[] positional = arguments.Where(argument => argument != "--background").ToArray();
        if (positional.Length != 0)
        {
            if (positional.Length is not (2 or 3)) throw new ArgumentException("Некорректные параметры запуска Rewind.");
            return new(Path.GetFullPath(positional[0]), Path.GetFullPath(positional[1]), arguments.Contains("--background"), positional.ElementAtOrDefault(2));
        }

        string data = Path.Combine(applicationDirectory, "Data");
        string? replays = null;
        string pathsFile = Path.Combine(applicationDirectory, "install-paths.txt");
        if (File.Exists(pathsFile) && File.ReadAllLines(pathsFile) is [string dataPath, string replayPath] &&
            Path.IsPathFullyQualified(dataPath) && Path.IsPathFullyQualified(replayPath))
        {
            data = dataPath;
            replays = replayPath;
        }
        return new(Path.Combine(data, "live-clips.json"), data, arguments.Contains("--background"), null) { ReplayDirectory = replays };
    }
}
