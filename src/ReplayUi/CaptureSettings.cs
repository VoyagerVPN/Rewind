using System.IO;
using System.Text.Json;

namespace Rewind.Preview;

public sealed record MonitorRecordingProfile(string DeviceName, bool Enabled, int Fps = 30, int OutputHeight = 0, string? Identity = null)
{
    public (int Width, int Height) OutputSize(int nativeWidth, int nativeHeight)
    {
        int height = Math.Min(1440, OutputHeight == 0 ? nativeHeight : Math.Min(OutputHeight, nativeHeight));
        return ((int)Math.Round(nativeWidth * (double)height / nativeHeight / 2) * 2, height / 2 * 2);
    }
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(DeviceName) || Fps is not (30 or 60 or 120) || OutputHeight is not (0 or 360 or 480 or 720 or 1080 or 1440))
            throw new ArgumentException(Localization.Text("Message.InvalidMonitorProfile"));
    }
}

public sealed record CaptureSettings(string MonitorDeviceName, int Fps, int HistorySeconds, int ReplaySeconds, string StorageRoot,
    string? MonitorIdentity = null, int[]? ReplayMarkers = null, bool CaptureSystemAudio = true,
    bool CaptureMicrophone = true, string? MicrophoneDeviceId = null, bool SeparateDiscord = true, bool CaptureVideo = true,
    int? PreferredReplaySeconds = null, MonitorRecordingProfile[]? MonitorProfiles = null, string FileNameFormat = ReplayFileName.DefaultFormat)
{
    public string? ShadowPlayDirectory { get; init; }

    internal string EffectiveFileNameFormat => ShadowPlayDirectory is null ? FileNameFormat : ReplayFileName.DefaultFormat;

    internal string ResolveReplayFolder(string application)
    {
        string name = ReplayFileName.ApplicationFolder(application);
        if (ShadowPlayDirectory is not string archive)
            return Path.Combine(StorageRoot, "Clips", name);

        string applicationFolder = Path.Combine(archive, name);
        string desktopFolder = Path.Combine(archive, "Desktop");
        if (Directory.Exists(applicationFolder) || !Directory.Exists(desktopFolder))
            return applicationFolder;

        string prefix = name + " ";
        var options = new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
        foreach (string file in Directory.EnumerateFiles(desktopFolder, "*.DVR.mp4", options))
        {
            string filename = Path.GetFileName(file);
            if (filename.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
                DateTime.TryParseExact(filename[prefix.Length..], "yyyy.MM.dd - HH.mm.ss.ff'.DVR.mp4'",
                    System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out _))
                return desktopFolder;
        }
        return applicationFolder;
    }

    public IReadOnlyList<MonitorRecordingProfile> ResolveMonitorProfiles() => MonitorProfiles ??
        [new MonitorRecordingProfile(MonitorDeviceName, CaptureVideo, Fps, 0, MonitorIdentity)];
    public bool HasSameValues(CaptureSettings? other) => other is not null &&
        (this with { ReplayMarkers = null, MonitorProfiles = null }) == (other with { ReplayMarkers = null, MonitorProfiles = null }) &&
        (ReplayMarkers ?? []).Order().SequenceEqual((other.ReplayMarkers ?? []).Order()) &&
        ResolveMonitorProfiles().OrderBy(profile => profile.DeviceName).SequenceEqual(other.ResolveMonitorProfiles().OrderBy(profile => profile.DeviceName));

    public void Validate()
    {
        ReplayFileName.Validate(FileNameFormat);
        if (string.IsNullOrWhiteSpace(MonitorDeviceName)) throw new ArgumentException(Localization.Text("Message.NoScreenIsSelectedForRecording"));
        if (Fps is not (30 or 60 or 120)) throw new ArgumentException(Localization.Text("Message.FrameRateMustBe3060Or120Fps"));
        if (HistorySeconds is < 4 or > 900) throw new ArgumentException(Localization.Text("Message.HistoryMustBeBetween4And900Seconds"));
        if (ReplaySeconds < 1 || ReplaySeconds > HistorySeconds) throw new ArgumentException(Localization.Text("Message.TheReplayMustFitWithinTheHistory"));
        if (PreferredReplaySeconds is < 1 or > 900) throw new ArgumentException(Localization.Text("Message.TheDefaultMarkerMustBeBetween001And1500"));
        if (ReplayMarkers is not null) _ = new ReplayIntervals(HistorySeconds, ReplayMarkers, PreferredReplaySeconds ?? ReplaySeconds);
        if (!Path.IsPathFullyQualified(StorageRoot)) throw new ArgumentException(Localization.Text("Message.TheStorageFolderPathIsIncomplete"));
        foreach (MonitorRecordingProfile profile in ResolveMonitorProfiles()) profile.Validate();
        if (ResolveMonitorProfiles().Select(profile => profile.Identity ?? profile.DeviceName).Distinct().Count() != ResolveMonitorProfiles().Count)
            throw new ArgumentException(Localization.Text("Message.TheMonitorIsListedTwice"));
        if (MonitorProfiles is not null && MonitorProfiles.Any(profile => profile.Enabled) != CaptureVideo)
            throw new ArgumentException(Localization.Text("Message.TheRecordingStateDoesNotMatchTheSelectedScreens"));
    }
}

public sealed class CaptureSettingsFile(string path)
{
    public CaptureSettings? Load()
    {
        if (!File.Exists(path)) return null;
        CaptureSettings value = JsonSerializer.Deserialize<CaptureSettings>(File.ReadAllText(path))
            ?? throw new InvalidDataException(Localization.Text("Message.CouldNotReadSettings"));
        value.Validate();
        return value;
    }

    public void Save(CaptureSettings value)
    {
        value.Validate();
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        string temporary = path + ".new";
        File.WriteAllText(temporary, JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temporary, path, overwrite: true);
    }
}
