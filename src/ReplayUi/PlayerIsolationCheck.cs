using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;

namespace Rewind.Preview;

internal static class PlayerIsolationCheck
{
    public static async Task RunAsync(string diagnostics, string? damagedFile = null)
    {
        string run = Path.Combine(diagnostics, "player-isolation-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(run);
        async Task<string?> SourceHashAsync()
        {
            if (damagedFile is null) return null;
            await using FileStream input = new(damagedFile, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            return Convert.ToHexString(await SHA256.HashDataAsync(input));
        }
        string? sourceHash = await SourceHashAsync();
        using var telemetry = new UiTelemetry(Dispatcher.CurrentDispatcher, run, () => false);
        var player = new ReplayPlayerView(telemetry, run);
        var host = new Window { Content = player, Width = 900, Height = 650, Left = -20000, Top = -20000,
            ShowActivated = false, ShowInTaskbar = false, WindowStyle = WindowStyle.None };
        host.Show();
        CaptureMonitor monitor = MonitorCatalog.Read().First(item => item.Primary);
        var settings = new CaptureSettings(monitor.DeviceName, 30, 20, 2, Path.Combine(run, "capture"), monitor.Identity,
            CaptureSystemAudio: false, CaptureMicrophone: false, SeparateDiscord: false,
            MonitorProfiles: [new(monitor.DeviceName, true, 30, 360, monitor.Identity)]);
        await using var capture = DesktopReplaySession.Start(settings, [monitor]);
        try
        {
            var clip = new Clip("Проверка защиты", "", Path.Combine(diagnostics, "player-timed-fixture.mp4"), "", [null, null, null], 24, 30,
                EmbeddedAudioSources: ["system", "discord", "microphone"]);
            await player.LoadClipAsync(clip);
            DateTime deadline = DateTime.UtcNow.AddSeconds(5);
            while (player.IsLoading && DateTime.UtcNow < deadline) await Task.Delay(25);
            if (player.IsLoading) throw new InvalidOperationException("Исправный повтор должен открываться.");
            using Process current = Process.GetCurrentProcess();
            if (current.Modules.Cast<ProcessModule>().Any(module => module.ModuleName.StartsWith("libvlc", StringComparison.OrdinalIgnoreCase) || module.ModuleName.StartsWith("libmpv", StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("Нативный декодер не должен загружаться в процесс панели и записи.");
            deadline = DateTime.UtcNow.AddSeconds(20);
            while (capture.Status.AvailableSeconds < 3 && DateTime.UtcNow < deadline) await Task.Delay(100);
            if (capture.Status.AvailableSeconds < 3) throw new InvalidOperationException("Захват не подготовил историю: " + capture.Status.Error);
            double before = capture.Status.AvailableSeconds;
            int workerId = player.VideoProcessId ?? throw new InvalidOperationException("Видеопроцесс не запущен.");
            if (workerId == Environment.ProcessId) throw new InvalidOperationException("Видеопроцесс должен быть отдельным.");
            var surface = (NativeVideoView)player.FindName("Video");
            bool embedded = false;
            EnumChildWindows(surface.SurfaceHandle, (child, _) => { GetWindowThreadProcessId(child, out uint owner); if (owner == workerId) embedded = true; return true; }, 0);
            if (surface.SurfaceHandle == 0 || !embedded) throw new InvalidOperationException("Дочерний видеопроцесс должен рисовать внутри поверхности панели.");
            using (Process worker = Process.GetProcessById(workerId)) { worker.Kill(entireProcessTree: true); await worker.WaitForExitAsync(); }
            deadline = DateTime.UtcNow.AddSeconds(12);
            while ((player.IsLoading || player.HasPlaybackError || player.VideoProcessId is not int resumed || resumed == workerId) && DateTime.UtcNow < deadline) await Task.Delay(25);
            if (player.HasPlaybackError || player.IsLoading || !player.IsPlaying || player.VideoProcessId == workerId)
                throw new InvalidOperationException("Авария декодера должна автоматически восстанавливать просмотр в новом процессе.");
            double recoveredAudio = player.AudioPositionSeconds;
            await Task.Delay(500);
            if (player.AudioPositionSeconds <= recoveredAudio + .2) throw new InvalidOperationException("После восстановления воспроизведения звук должен продолжаться.");
            deadline = DateTime.UtcNow.AddSeconds(10);
            while (capture.Status.AvailableSeconds <= before + 1 && DateTime.UtcNow < deadline) await Task.Delay(100);
            if (capture.Status.Error is not null || capture.Status.AvailableSeconds <= before + 1)
                throw new InvalidOperationException("Авария плеера не должна прерывать запись.");
            SavedReplay saved = (await capture.SaveScreensAsync(2, [monitor.DeviceName], "Isolation QA"))[0];
            await CaptureProcesses.RunAsync(CaptureProcesses.Ffmpeg, ["-v", "error", "-nostdin", "-i", saved.File, "-map", "0", "-f", "null", "-"], TimeSpan.FromSeconds(20));
            await player.LoadClipAsync(clip, autoPlay: false);
            deadline = DateTime.UtcNow.AddSeconds(5);
            while (player.IsLoading && DateTime.UtcNow < deadline) await Task.Delay(25);
            if (player.HasPlaybackError || player.IsLoading || player.VideoProcessId == workerId)
                throw new InvalidOperationException("После аварии должен запускаться новый плеер и открываться исправный повтор.");
            if (damagedFile is not null)
            {
                bool rejected = false;
                try { await player.LoadClipAsync(clip with { Video = damagedFile, Duration = 90 }); }
                catch (InvalidOperationException) { rejected = true; }
                if (!rejected || !player.HasPlaybackError || player.IsPlaying)
                    throw new InvalidOperationException("Повреждённое начало должно быть отклонено до воспроизведения, с понятной ошибкой.");
                await player.LoadClipAsync(clip, autoPlay: false);
                deadline = DateTime.UtcNow.AddSeconds(5);
                while (player.IsLoading && DateTime.UtcNow < deadline) await Task.Delay(25);
                if (player.HasPlaybackError || player.IsLoading) throw new InvalidOperationException("После повреждённого файла должен открываться исправный повтор.");
            }
            int stalledId = player.VideoProcessId ?? throw new InvalidOperationException("Нет видеопроцесса для проверки зависания.");
            using (Process stalled = Process.GetProcessById(stalledId))
                if (NtSuspendProcess(stalled.Handle) != 0) throw new InvalidOperationException("Не удалось приостановить тестовый видеопроцесс.");
            var stallTimer = Stopwatch.StartNew();
            deadline = DateTime.UtcNow.AddSeconds(15);
            while ((player.IsLoading || player.HasPlaybackError || player.VideoProcessId is not int restored || restored == stalledId) && DateTime.UtcNow < deadline) await Task.Delay(25);
            if (player.HasPlaybackError || player.IsLoading || player.IsPlaying || player.VideoProcessId == stalledId)
                throw new InvalidOperationException("Зависание должно восстанавливать видеопроцесс с сохранением паузы.");
            double stallRecoveryMs = stallTimer.Elapsed.TotalMilliseconds;
            if (sourceHash != await SourceHashAsync()) throw new InvalidOperationException("Исходный MP4 должен оставаться неизменным.");
            int? finalWorker = player.VideoProcessId;
            await player.ClosePlayerAsync();
            if (player.VideoProcessId is not null) throw new InvalidOperationException("Закрытие должно освобождать видеопроцесс.");
            await File.WriteAllTextAsync(Path.Combine(diagnostics, "player-isolation.json"), JsonSerializer.Serialize(new
            { run, isolated = true, embedded, workerId, finalWorker, saved.File, captureContinued = true, savedDecode = "PASS", damagedFile, sourceHash, damagedRejected = damagedFile is not null, recovery = "PASS", stallRecoveryMs, close = "PASS" }));
        }
        finally { await player.ClosePlayerAsync(); host.Close(); }
    }
    private delegate bool WindowVisitor(nint window, nint parameter);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool EnumChildWindows(nint parent, WindowVisitor visitor, nint parameter);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint process);
    [DllImport("ntdll.dll")] private static extern int NtSuspendProcess(nint process);
}
