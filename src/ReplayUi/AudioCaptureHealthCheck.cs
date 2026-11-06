using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

namespace Rewind.Preview;

internal static class AudioCaptureHealthCheck
{
    public static async Task RunAsync(string folder)
    {
        CaptureSettings original = new CaptureSettingsFile(Path.Combine(folder, "capture-settings.json")).Load()
            ?? throw new InvalidOperationException("Нет настроек для проверки текущих профилей экранов.");
        MonitorRecordingProfile[] profiles = original.ResolveMonitorProfiles().Where(profile => profile.Enabled).ToArray();
        if (profiles.Length != 2) throw new InvalidOperationException("Проверка требует два включённых профиля экранов.");
        var settings = original with { StorageRoot = Path.Combine(folder, "audio-health-" + Guid.NewGuid().ToString("N")),
            HistorySeconds = 12, ReplaySeconds = 8, ReplayMarkers = [8, 12], PreferredReplaySeconds = 8 };
        // Собственный процесс — управляемая цель process-loopback, даже если настоящий Discord закрыт.
        await using var capture = DesktopReplaySession.Start(settings, MonitorCatalog.Read(), (uint)Environment.ProcessId);
        var clock = Stopwatch.StartNew();
        var samples = new List<object>();
        bool rotationExercised = false;
        while (clock.Elapsed.TotalSeconds < 45)
        {
            if (capture.Status.Error is string error) throw new InvalidOperationException(error);
            if (!rotationExercised && clock.Elapsed.TotalSeconds >= 4)
            {
                // Только собственный QA-журнал: проверяем ротацию на границе размера без многочасового прогона.
                await File.AppendAllTextAsync(capture.AudioDiagnosticsFile, JsonSerializer.Serialize(new { rotationFixture = new string('x', 4 * 1024 * 1024) }) + "\n");
                rotationExercised = true;
            }
            samples.Add(new { seconds = clock.Elapsed.TotalSeconds, capture.Status, audio = capture.AudioDiagnostics });
            await Task.Delay(250);
        }
        AudioCaptureDiagnostics final = capture.AudioDiagnostics ?? throw new InvalidOperationException("Нет аудиометрик.");
        if (final.Sources.Length != 3 || final.Sources.Any(source => !source.Connected || source.Reconnects != 0 || source.Buffer?.DroppedFrames != 0))
            throw new InvalidOperationException("Обычная запись двух экранов не должна терять звук: " + JsonSerializer.Serialize(final));
        SavedReplay[] replays = await capture.SaveScreensAsync();
        await capture.StopAsync();
        var files = new List<object>();
        foreach (SavedReplay replay in replays)
        {
            if (replay.AudioSources?.Length != 3) throw new InvalidOperationException("В повторе нужны все три дорожки.");
            await CaptureProcesses.RunAsync(CaptureProcesses.Ffmpeg,
                ["-hide_banner", "-v", "error", "-nostdin", "-i", replay.File, "-map", "0", "-fps_mode", "passthrough", "-enc_time_base", "demux", "-f", "null", "-"], TimeSpan.FromSeconds(45));
            await using var stream = File.OpenRead(replay.File);
            string sha256 = Convert.ToHexString(await SHA256.HashDataAsync(stream));
            files.Add(new { replay, sha256 });
        }
        string[] log = await File.ReadAllLinesAsync(capture.AudioDiagnosticsFile);
        using JsonDocument finalLog = JsonDocument.Parse(log[^1]);
        string summary = await File.ReadAllTextAsync(capture.AudioSummaryFile);
        if (log.Length < 8 || !finalLog.RootElement.GetProperty("final").GetBoolean() ||
            !File.Exists(Path.Combine(Path.GetDirectoryName(capture.AudioDiagnosticsFile)!, "audio-metrics.previous.jsonl")) ||
            !summary.Contains("за текущий сеанс", StringComparison.Ordinal) || capture.AudioDiagnosticsError is not null ||
            finalLog.RootElement.GetProperty("metrics").GetProperty("Transport").GetProperty("PendingWriteMilliseconds").GetDouble() != 0)
            throw new InvalidOperationException("Диагностика должна записываться во время записи и при её завершении.");
        await File.WriteAllTextAsync(Path.Combine(folder, "audio-health-check.json"), JsonSerializer.Serialize(new
        {
            completedUtc = DateTime.UtcNow, settings, final, files, samples, diagnosticFile = capture.AudioDiagnosticsFile, diagnosticLines = log.Length,
            verified = "45 seconds with current two monitor profiles, no dropped audio or source reconnect, all tracks fully decoded, complete file SHA256, diagnostic rotation and readable summary, final pending write reset.",
            limitations = "Desktop workload, controlled process-loopback target in place of Discord; not a real call, game benchmark or physical device unplug test."
        }, new JsonSerializerOptions { WriteIndented = true }));
    }
}
