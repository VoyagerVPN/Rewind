using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace Rewind.Preview;

internal static class MultiMonitorSmoke
{
    public static async Task RunAsync(string directory)
    {
        CaptureMonitor[] monitors = MonitorCatalog.Read().OrderByDescending(monitor => monitor.Primary).ToArray();
        if (monitors.Length < 2) throw new InvalidOperationException("Для проверки нужны два монитора.");
        string storage = Path.Combine(@"N:\Rewind-QA", "multi-" + Guid.NewGuid().ToString("N"));
        MonitorRecordingProfile[] profiles = monitors.Select((monitor, index) => new MonitorRecordingProfile(monitor.DeviceName, true,
            index == 0 ? 120 : index == 1 ? 30 : 60, index == 0 ? 0 : index == 1 ? 720 : 360, monitor.Identity)).ToArray();
        var settings = new CaptureSettings(monitors[0].DeviceName, 120, 8, 3, storage, monitors[0].Identity, MonitorProfiles: profiles);
        await using var capture = DesktopReplaySession.Start(settings, monitors);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(50));
        while (capture.Status.AvailableSeconds < 8)
        {
            if (capture.Status.Error is string error) throw new InvalidOperationException(error);
            await Task.Delay(100, deadline.Token);
        }
        long endTimestamp = Stopwatch.GetTimestamp();
        IDisposable[] holds = capture.HoldReplayHistory(endTimestamp, 8, null);
        SavedReplay[] saved;
        try
        {
            await Task.Delay(1200, deadline.Token);
            saved = await capture.SaveScreensAsync(3, endTimestamp: endTimestamp);
        }
        finally { foreach (IDisposable hold in holds) hold.Dispose(); }
        if (Directory.EnumerateFiles(Path.Combine(storage, "Clips"), "*", SearchOption.AllDirectories).Any(file => Path.GetExtension(file) != ".mp4") ||
            saved.Any(replay => Path.GetFileName(Path.GetDirectoryName(replay.File)) != "Desktop"))
            throw new InvalidOperationException("В папке клипов должны оставаться только видео, без JSON и JPEG.");
        if (saved.Length != profiles.Length || saved.Select(replay => replay.EventId).Distinct().Count() != 1 ||
            saved.Select(replay => replay.RequestedEndSeconds).Distinct().Count() != 1)
            throw new InvalidOperationException("Сохранение всех экранов должно иметь один момент конца и одно событие.");
        foreach (SavedReplay replay in saved)
        {
            MonitorRecordingProfile profile = profiles.Single(profile => profile.DeviceName == replay.MonitorDeviceName);
            CaptureMonitor monitor = monitors.Single(monitor => monitor.DeviceName == profile.DeviceName);
            if ((replay.Width, replay.Height) != profile.OutputSize(monitor.Width, monitor.Height) || Math.Abs(replay.Fps - profile.Fps) > .1 ||
                replay.AudioSources?.Length != 3 || replay.RelatedFiles?.Length != profiles.Length)
                throw new InvalidOperationException("Каждый экран должен сохранить свой профиль и три звуковые дорожки.");
            await CaptureProcesses.RunAsync(CaptureProcesses.Ffmpeg,
                ["-hide_banner", "-v", "error", "-nostdin", "-i", replay.File, "-map", "0", "-fps_mode", "passthrough", "-enc_time_base", "demux", "-f", "null", "-"], TimeSpan.FromSeconds(30));
            string metadata = await CaptureProcesses.RunAsync(CaptureProcesses.Ffprobe,
                ["-v", "error", "-show_entries", "format_tags=comment", "-of", "json", replay.File], TimeSpan.FromSeconds(15));
            using JsonDocument probe = JsonDocument.Parse(metadata);
            string comment = probe.RootElement.GetProperty("format").GetProperty("tags").GetProperty("comment").GetString()!;
            if (!comment.StartsWith("Rewind/1:", StringComparison.Ordinal)) throw new InvalidOperationException("FFprobe должен видеть сведения Rewind в контейнере.");
            using JsonDocument embedded = JsonDocument.Parse(comment[9..]);
            if (embedded.RootElement.GetProperty("EventId").GetString() != replay.EventId ||
                embedded.RootElement.GetProperty("AudioSources").GetArrayLength() != 3 || comment.Contains(storage, StringComparison.Ordinal))
                throw new InvalidOperationException("Встроенная метадата должна содержать событие и три источника без привязки к папке.");
        }
        SavedReplay[] selected = await capture.SaveScreensAsync(2, [profiles[1].DeviceName]);
        if (selected.Length != 1 || selected[0].MonitorDeviceName != profiles[1].DeviceName)
            throw new InvalidOperationException("Сохранение выбранного экрана не должно сохранять остальные.");
        if (selected[0].RequestedEndSeconds - saved[0].RequestedEndSeconds < 1 || saved.Any(replay => replay.RequestedSeconds != 3))
            throw new InvalidOperationException("Клипы должны использовать выбранный интервал и конец, зафиксированный до ожидания.");
        await capture.StopAsync();
        if (capture.Status.Error is not null || capture.Status.BufferBytes != 0 || saved.Any(replay => !File.Exists(replay.File)))
            throw new InvalidOperationException(capture.Status.Error ?? "Остановка должна освободить историю и оставить клипы.");
        await File.WriteAllTextAsync(Path.Combine(directory, "multi-monitor.json"), JsonSerializer.Serialize(new { completedUtc = DateTime.UtcNow,
            settings, saved, selected, verified = "Separate monitor profiles, common endpoint from before the duration choice, all/subset export, 3 audio tracks, full decode and cleanup.",
            limitations = "Short desktop check; not game FPS, sustained 120 unique frames, or measured sub-frame cross-screen synchronization." }, new JsonSerializerOptions { WriteIndented = true }));
    }
}
