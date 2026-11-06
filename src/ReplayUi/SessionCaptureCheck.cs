using System.IO;
using System.Text.Json;

namespace Rewind.Preview;

internal static class SessionCaptureCheck
{
    public static async Task RunAsync(string diagnostics)
    {
        CaptureMonitor primary = MonitorCatalog.Read().First(monitor => monitor.Primary);
        string folder = Path.Combine(diagnostics, "session-capture-" + Guid.NewGuid().ToString("N"));
        var settings = new CaptureSettings(primary.DeviceName, 30, 5, 2, folder, primary.Identity,
            CaptureSystemAudio: false, CaptureMicrophone: false, SeparateDiscord: false,
            MonitorProfiles: [new(primary.DeviceName, true, 30, 360, primary.Identity)], FileNameFormat: "{app} {size} {screen}");
        await using var capture = DesktopReplaySession.Start(settings, [primary]);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while (capture.Status.AvailableSeconds < 3)
        {
            if (capture.Status.Error is string error) throw new InvalidOperationException(error);
            await Task.Delay(100, deadline.Token);
        }
        DateTime started = DateTime.UtcNow.AddHours(-3);
        SavedReplay saved = (await capture.SaveScreensAsync(2, [primary.DeviceName], "Session QA", sessionId: "session-roundtrip", sessionStartedUtc: started))[0];
        await capture.StopAsync();
        string expectedName = ReplayFileName.Format(settings.FileNameFormat, "Session QA", saved.SavedAtUtc!.Value.ToLocalTime(),
            saved.RequestedSeconds, 1, string.Concat(primary.DeviceName.Where(char.IsLetterOrDigit)), new FileInfo(saved.File).Length);
        if (Path.GetFileName(saved.File) != expectedName)
            throw new InvalidOperationException("Размер в имени должен соответствовать готовому MP4 без повторной упаковки видео.");
        SavedReplay? restored = ReplayMetadata.Read(saved.File);
        if (restored?.ApplicationName != "Session QA" || restored.SessionId != "session-roundtrip" || restored.SessionStartedUtc != started || restored.SavedAtUtc is null)
            throw new InvalidOperationException("Реальное сохранение должно переносить приложение и сессию внутрь MP4.");
        string moved = Path.Combine(folder, "Переименованный повтор.mp4"); File.Copy(saved.File, moved);
        SavedReplay? portable = ReplayMetadata.Read(moved);
        if (portable?.ApplicationName != restored.ApplicationName || portable.SessionId != restored.SessionId || portable.SavedAtUtc != restored.SavedAtUtc)
            throw new InvalidOperationException("Перенос и переименование MP4 не должны менять приложение, сессию и время сохранения.");
        await CaptureProcesses.RunAsync(CaptureProcesses.Ffmpeg, ["-v", "error", "-nostdin", "-i", saved.File, "-map", "0", "-f", "null", "-"], TimeSpan.FromSeconds(20));
        await File.WriteAllTextAsync(Path.Combine(diagnostics, "session-capture-check.json"), JsonSerializer.Serialize(new { file = saved.File, metadata = "PASS application/session/start/save time round-trip", decode = "PASS full video decode", limitations = "Short desktop capture without audio; not game performance." }));
    }
}
