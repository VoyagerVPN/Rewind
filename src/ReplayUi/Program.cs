using System.IO;
using System.Text.Json;
using System.Windows;

namespace Rewind.Preview;

internal static class Program
{
    private static void CheckCaptureIndicator(OverlayWindow panel)
    {
        if (panel.HeaderCaptureErrorDot.Visibility != Visibility.Collapsed)
            throw new InvalidOperationException("Индикатор обрыва должен быть скрыт при запуске панели.");
        var color = System.Windows.Media.Colors.Gray;
        var recording = new ReplayStatus(true, 8, 8, 1, 240, 0, 0, null);
        var recovering = recording with { Recovering = true };
        panel.RenderCaptureIndicator(recovering, true, color);
        if (panel.HeaderCaptureErrorDot.Visibility != Visibility.Visible || panel.HeaderCaptureErrorDot.Opacity != 1 ||
            panel.CaptureStatusText.Text != Localization.Text("Message.RecordingInterruptedRecovering"))
            throw new InvalidOperationException("При восстановлении панель должна показать обрыв записи вместо предупреждения о звуке.");
        panel.RenderCaptureIndicator(recovering, true, color, advanceBlink: true);
        if (panel.HeaderCaptureErrorDot.Opacity != 0)
            throw new InvalidOperationException("На следующем такте точка должна погаснуть.");
        panel.RenderCaptureIndicator(recording with { Running = false, Error = "Сбой видеозахвата" }, false, color);
        panel.RenderCaptureIndicator(null, false, color, advanceBlink: true);
        if (panel.HeaderCaptureErrorDot.Opacity != 1 || panel.CaptureStatusText.Text != Localization.Text("Message.RecordingInterrupted") ||
            panel.CaptureFailureText.Text != "Сбой видеозахвата")
            throw new InvalidOperationException("После завершения оборванного сеанса должны остаться индикатор и причина сбоя.");
        panel.RenderCaptureIndicator(recording, false, color);
        if (panel.HeaderCaptureErrorDot.Visibility != Visibility.Collapsed || panel.CaptureFailureText.Visibility != Visibility.Collapsed ||
            panel.CaptureStatusText.Text != Localization.Text("Message.Recording9d0a624f"))
            throw new InvalidOperationException("Возобновившаяся запись должна очистить индикатор и сообщение о сбое.");
    }

    [STAThread]
    private static void Main(string[] args)
    {
        if (args is ["--setup", string installer, string defaultsFile])
        {
            Environment.ExitCode = SetupWindow.Run(installer, defaultsFile);
            return;
        }
        if (args.Length == 2 && args[0] == "--version")
        {
            File.WriteAllText(args[1], JsonSerializer.Serialize(AppVersion.RuntimeInfo(), new JsonSerializerOptions { WriteIndented = true }));
            return;
        }
        if (args.Length == 2 && args[0] == "--video-overlay-check")
        {
            try { VideoOverlayCompatibilityCheck.RunAsync(args[1]).GetAwaiter().GetResult(); File.Delete(Path.Combine(args[1], "video-overlay-check-error.txt")); }
            catch (Exception error) { File.WriteAllText(Path.Combine(args[1], "video-overlay-check-error.txt"), error.ToString()); Environment.ExitCode = 1; }
            return;
        }
        if (args.Length == 2 && args[0] == "--ui-telemetry-check")
        {
            var checkApp = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            checkApp.Startup += async (_, _) =>
            {
                try { await UiTelemetryCheck.RunAsync(args[1]); File.Delete(Path.Combine(args[1], "ui-telemetry-check-error.txt")); }
                catch (Exception error) { File.WriteAllText(Path.Combine(args[1], "ui-telemetry-check-error.txt"), error.ToString()); Environment.ExitCode = 1; }
                finally { checkApp.Shutdown(); }
            };
            checkApp.Run(); return;
        }
        if (args.Length == 2 && args[0] == "--gallery-preview-check")
        {
            var galleryApp = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            galleryApp.Startup += async (_, _) =>
            {
                try { await GalleryPreviewCheck.RunAsync(args[1]); File.Delete(Path.Combine(args[1], "gallery-preview-check-error.txt")); }
                catch (Exception error) { File.WriteAllText(Path.Combine(args[1], "gallery-preview-check-error.txt"), error.ToString()); Environment.ExitCode = 1; }
                finally { galleryApp.Shutdown(); }
            };
            galleryApp.Run(); return;
        }
        if (args.Length == 2 && args[0] == "--motion-check")
        {
            var motionApp = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            motionApp.Startup += async (_, _) =>
            {
                try { await OverlayMotionCheck.RunAsync(args[1]); File.Delete(Path.Combine(args[1], "motion-check-error.txt")); }
                catch (Exception error) { File.WriteAllText(Path.Combine(args[1], "motion-check-error.txt"), error.ToString()); Environment.ExitCode = 1; }
                finally { motionApp.Shutdown(); }
            };
            motionApp.Run(); return;
        }
        if (args.Length == 2 && args[0] == "--capture-recovery-check")
        {
            try { CaptureCadenceCheck.CheckWorkerRecoveryAsync(args[1]).GetAwaiter().GetResult(); File.Delete(Path.Combine(args[1], "capture-recovery-check-error.txt")); }
            catch (Exception error) { File.WriteAllText(Path.Combine(args[1], "capture-recovery-check-error.txt"), error.ToString()); Environment.ExitCode = 1; }
            return;
        }
        if (args.Length == 2 && args[0] == "--capture-log-recovery-check")
        {
            try { CaptureCadenceCheck.CheckLogContentionAsync(args[1], true).GetAwaiter().GetResult(); File.Delete(Path.Combine(args[1], "capture-log-recovery-check-error.txt")); }
            catch (Exception error) { File.WriteAllText(Path.Combine(args[1], "capture-log-recovery-check-error.txt"), error.ToString()); Environment.ExitCode = 1; }
            return;
        }
        if (args.Length == 2 && args[0] == "--capture-log-check")
        {
            try { CaptureCadenceCheck.CheckLogContentionAsync(args[1]).GetAwaiter().GetResult(); File.Delete(Path.Combine(args[1], "capture-log-check-error.txt")); }
            catch (Exception error) { File.WriteAllText(Path.Combine(args[1], "capture-log-check-error.txt"), error.ToString()); Environment.ExitCode = 1; }
            return;
        }
        if (args.Length == 2 && args[0] == "--capture-stop-check")
        {
            try { CaptureCadenceCheck.CheckUnconnectedStopAsync(args[1]).GetAwaiter().GetResult(); File.Delete(Path.Combine(args[1], "capture-stop-check-error.txt")); }
            catch (Exception error) { File.WriteAllText(Path.Combine(args[1], "capture-stop-check-error.txt"), error.ToString()); Environment.ExitCode = 1; }
            return;
        }
        if (args.Length == 2 && args[0] == "--capture-cadence-check")
        {
            try { CaptureCadenceCheck.RunAsync(args[1]).GetAwaiter().GetResult(); File.Delete(Path.Combine(args[1], "capture-cadence-error.txt")); }
            catch (Exception error) { File.WriteAllText(Path.Combine(args[1], "capture-cadence-error.txt"), error.ToString()); Environment.ExitCode = 1; }
            return;
        }
        if (args.Length == 4 && args[0] == "--application-check")
        {
            try
            {
                using var process = System.Diagnostics.Process.GetProcessById(int.Parse(args[1]));
                var watch = System.Diagnostics.Stopwatch.StartNew();
                ApplicationCaptureContext context = CaptureApplication.Read(process.MainWindowHandle);
                File.WriteAllText(Path.Combine(args[3], "application-check.json"), JsonSerializer.Serialize(new { context, milliseconds = watch.Elapsed.TotalMilliseconds }));
                if (context.Name != args[2] || context.SessionId is null) throw new InvalidOperationException("Имя приложения должно соответствовать продукту игры, с сохранением сессии: " + context.Name);
                File.Delete(Path.Combine(args[3], "application-check-error.txt"));
            }
            catch (Exception error) { File.WriteAllText(Path.Combine(args[3], "application-check-error.txt"), error.ToString()); Environment.ExitCode = 1; }
            return;
        }
        if (args.Length == 2 && args[0] == "--audio-health-check")
        {
            try { AudioCaptureHealthCheck.RunAsync(args[1]).GetAwaiter().GetResult(); File.Delete(Path.Combine(args[1], "audio-health-error.txt")); }
            catch (Exception error) { File.WriteAllText(Path.Combine(args[1], "audio-health-error.txt"), error.ToString()); Environment.ExitCode = 1; }
            return;
        }
        if (args.Length == 2 && args[0] == "--audio-buffer-check")
        {
            try { AudioBufferCheck.RunAsync(args[1]).GetAwaiter().GetResult(); File.Delete(Path.Combine(args[1], "audio-buffer-error.txt")); }
            catch (Exception error) { File.WriteAllText(Path.Combine(args[1], "audio-buffer-error.txt"), error.ToString()); Environment.ExitCode = 1; }
            return;
        }
        if (args.Length == 2 && args[0] == "--player-playhead-check")
        {
            var playheadApp = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            playheadApp.Startup += async (_, _) => { try { await PlayerPlayheadCheck.RunAsync(args[1]); File.Delete(Path.Combine(args[1], "player-playhead-error.txt")); }
                catch (Exception error) { File.WriteAllText(Path.Combine(args[1], "player-playhead-error.txt"), error.ToString()); Environment.ExitCode = 1; } finally { playheadApp.Shutdown(); } };
            playheadApp.Run(); return;
        }
        if (args.Length == 2 && args[0] == "--player-risk-check")
        {
            var riskApp = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            riskApp.Startup += async (_, _) => { try { await PlayerRiskCheck.RunAsync(args[1]); File.Delete(Path.Combine(args[1], "player-risk-error.txt")); }
                catch (Exception error) { File.WriteAllText(Path.Combine(args[1], "player-risk-error.txt"), error.ToString()); Environment.ExitCode = 1; } finally { riskApp.Shutdown(); } };
            riskApp.Run(); return;
        }
        if (args.Length == 3 && args[0] == "--player-recovery-check")
        {
            var recoveryApp = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            recoveryApp.Startup += async (_, _) => { try { await PlayerRecoveryCheck.RunAsync(args[1], args[2]); File.Delete(Path.Combine(args[2], "player-recovery-error.txt")); }
                catch (Exception error) { File.WriteAllText(Path.Combine(args[2], "player-recovery-error.txt"), error.ToString()); Environment.ExitCode = 1; } finally { recoveryApp.Shutdown(); } };
            recoveryApp.Run(); return;
        }
        if (args.Length == 3 && args[0] == "--video-worker")
        {
            string? workerLanguage = Environment.GetEnvironmentVariable("REWIND_UI_LANGUAGE");
            if (workerLanguage is "en" or "ru") Localization.SetLanguage(workerLanguage);
            try { VideoWorker.RunAsync(args[1], int.Parse(args[2], System.Globalization.CultureInfo.InvariantCulture)).GetAwaiter().GetResult(); }
            catch { Environment.ExitCode = 1; }
            return;
        }
        if (args.Length == 3 && args[0] == "--video-probe-cache-check")
        {
            try { VideoProbeCacheCheck.RunAsync(args[1], args[2]).GetAwaiter().GetResult(); File.Delete(Path.Combine(args[1], "video-probe-cache-error.txt")); }
            catch (Exception error) { File.WriteAllText(Path.Combine(args[1], "video-probe-cache-error.txt"), error.ToString()); Environment.ExitCode = 1; }
            return;
        }
        if (args.Length is 2 or 3 && args[0] == "--player-isolation-check")
        {
            var isolationApp = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            isolationApp.Startup += async (_, _) => { try { await PlayerIsolationCheck.RunAsync(args[1], args.ElementAtOrDefault(2)); File.Delete(Path.Combine(args[1], "player-isolation-error.txt")); }
                catch (Exception error) { File.WriteAllText(Path.Combine(args[1], "player-isolation-error.txt"), error.ToString()); Environment.ExitCode = 1; } finally { isolationApp.Shutdown(); } };
            isolationApp.Run(); return;
        }
        if (args.Length == 2 && args[0] == "--notification-placement")
        {
            var placementApp = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            placementApp.Startup += (_, _) =>
            {
                string errorFile = Path.Combine(args[1], "notification-placement-error.txt");
                try { NotificationClickCheck.CheckPlacement(args[1]); File.Delete(errorFile); }
                catch (Exception error) { File.WriteAllText(errorFile, error.ToString()); Environment.ExitCode = 1; }
                finally { placementApp.Shutdown(); }
            };
            placementApp.Run(); return;
        }
        if (args.Length == 2 && args[0] == "--notification-check")
        {
            var notificationApp = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            notificationApp.Startup += async (_, _) => { try { await NotificationClickCheck.RunAsync(args[1]); File.Delete(Path.Combine(args[1], "notification-error.txt")); }
                catch (Exception error) { File.WriteAllText(Path.Combine(args[1], "notification-error.txt"), error.ToString()); Environment.ExitCode = 1; } finally { notificationApp.Shutdown(); } };
            notificationApp.Run(); return;
        }
        if (args.Length == 2 && args[0] == "--session-capture-check")
        {
            try { SessionCaptureCheck.RunAsync(args[1]).GetAwaiter().GetResult(); File.Delete(Path.Combine(args[1], "session-capture-error.txt")); }
            catch (Exception error) { File.WriteAllText(Path.Combine(args[1], "session-capture-error.txt"), error.ToString()); Environment.ExitCode = 1; }
            return;
        }
        if (args.Length == 3 && args[0] == "--player-performance-check")
        {
            var performanceApp = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            performanceApp.Startup += async (_, _) =>
            {
                try { await PlayerPerformanceCheck.RunAsync(args[1], args[2]); File.Delete(Path.Combine(args[2], "player-performance-error.txt")); }
                catch (Exception error) { File.WriteAllText(Path.Combine(args[2], "player-performance-error.txt"), error.ToString()); Environment.ExitCode = 1; }
                finally { performanceApp.Shutdown(); }
            };
            performanceApp.Run(); return;
        }
        if (args.Length == 2 && args[0] == "--replay-export-check")
        {
            try { ReplayExportCheck.RunAsync(args[1]).GetAwaiter().GetResult(); File.Delete(Path.Combine(args[1], "replay-export-error.txt")); }
            catch (Exception error) { File.WriteAllText(Path.Combine(args[1], "replay-export-error.txt"), error.ToString()); Environment.ExitCode = 1; }
            return;
        }
        if (args.Length == 2 && args[0] == "--player-controls-check")
        {
            var checkApp = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            checkApp.Startup += async (_, _) => { try { await PlayerControlsCheck.RunAsync(args[1]); File.Delete(Path.Combine(args[1], "player-controls-error.txt")); }
                catch (Exception error) { File.WriteAllText(Path.Combine(args[1], "player-controls-error.txt"), error.ToString()); Environment.ExitCode = 1; } finally { checkApp.Shutdown(); } };
            checkApp.Run(); return;
        }
        if (args.Length == 2 && args[0] == "--catalog-measurement")
        {
            var catalogApp = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            catalogApp.Startup += async (_, _) => { try { await CatalogMeasurement.RunAsync(args[1]); File.Delete(Path.Combine(args[1], "catalog-measurement-error.txt")); }
                catch (Exception error) { File.WriteAllText(Path.Combine(args[1], "catalog-measurement-error.txt"), error.ToString()); Environment.ExitCode = 1; } finally { catalogApp.Shutdown(); } };
            catalogApp.Run(); return;
        }
        if (args.Length == 2 && args[0] == "--storage-check")
        {
            var storageApp = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            storageApp.Startup += async (_, _) => { try { await StorageUiCheck.RunAsync(args[1]); File.Delete(Path.Combine(args[1], "storage-check-error.txt")); }
                catch (Exception error) { File.WriteAllText(Path.Combine(args[1], "storage-check-error.txt"), error.ToString()); Environment.ExitCode = 1; } finally { storageApp.Shutdown(); } };
            storageApp.Run(); return;
        }
        if (args.Length == 2 && args[0] == "--library-check")
        {
            var checkApp = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            checkApp.Startup += async (_, _) =>
            {
                try { await LibraryUiCheck.RunAsync(args[1]); File.Delete(Path.Combine(args[1], "library-check-error.txt")); }
                catch (Exception error) { File.WriteAllText(Path.Combine(args[1], "library-check-error.txt"), error.ToString()); Environment.ExitCode = 1; }
                finally { checkApp.Shutdown(); }
            };
            checkApp.Run(); return;
        }
        if (args.Length == 2 && args[0] == "--overlay-check")
        {
            var checkApp = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            checkApp.Startup += async (_, _) =>
            {
                try { await OverlayInteractionCheck.RunAsync(args[1]); File.Delete(Path.Combine(args[1], "overlay-check-error.txt")); }
                catch (Exception error) { File.WriteAllText(Path.Combine(args[1], "overlay-check-error.txt"), error.ToString()); Environment.ExitCode = 1; }
                finally { checkApp.Shutdown(); }
            };
            checkApp.Run(); return;
        }
        if (args.Length == 2 && args[0] == "--audio-isolation-probe")
        {
            try { AudioIsolationProbe.RunAsync(args[1]).GetAwaiter().GetResult(); }
            catch (Exception error) { File.WriteAllText(Path.Combine(args[1], "audio-isolation-probe-error.txt"), error.ToString()); Environment.ExitCode = 1; }
            return;
        }
        if (args.Length == 2 && args[0] == "--audio-meter-check")
        {
            var meterApp = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            meterApp.Startup += async (_, _) =>
            {
                try { await AudioMeterCheck.RunAsync(args[1]); File.Delete(Path.Combine(args[1], "audio-meters-error.txt")); }
                catch (Exception error) { File.WriteAllText(Path.Combine(args[1], "audio-meters-error.txt"), error.ToString()); Environment.ExitCode = 1; }
                finally { meterApp.Shutdown(); }
            };
            meterApp.Run();
            return;
        }
        if (args.Length == 2 && args[0] == "--hotkey-check")
        {
            _ = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            try { HotkeyRegistrationCheck.Run(args[1]); File.Delete(Path.Combine(args[1], "hotkeys-error.txt")); }
            catch (Exception error) { File.WriteAllText(Path.Combine(args[1], "hotkeys-error.txt"), error.ToString()); Environment.ExitCode = 1; }
            return;
        }
        if (args.Length == 2 && args[0] == "--multi-monitor-check")
        {
            try { MultiMonitorSmoke.RunAsync(args[1]).GetAwaiter().GetResult(); File.Delete(Path.Combine(args[1], "multi-monitor-error.txt")); }
            catch (Exception error) { File.WriteAllText(Path.Combine(args[1], "multi-monitor-error.txt"), error.ToString()); Environment.ExitCode = 1; }
            return;
        }
        if (args.Length is 3 or 4 && args[0] == "--ui-interaction")
        {
            Clip[] qaClips = JsonSerializer.Deserialize<Clip[]>(File.ReadAllText(args[1]), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
            var qaApp = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
            var qaWindow = new OverlayWindow(qaClips, args[2]) { Title = "Rewind · UI QA", ShowInTaskbar = true };
            if (args.Length == 3 || args[3] != "home") qaWindow.ShowSettingsSection("screens");
            if (args.Length == 4 && args[3] == "player")
                qaApp.Startup += async (_, _) => await qaWindow.OpenClipAsync(qaClips[0]);
            qaApp.Run(qaWindow); return;
        }
        if (args.Length == 2 && args[0] == "--catalog-cache-check")
        {
            var checkApp = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            checkApp.Startup += async (_, _) =>
            {
                try { await CatalogCacheCheck.RunAsync(args[1]); File.Delete(Path.Combine(args[1], "catalog-cache-error.txt")); }
                catch (Exception error) { File.WriteAllText(Path.Combine(args[1], "catalog-cache-error.txt"), error.ToString()); Environment.ExitCode = 1; }
                finally { checkApp.Shutdown(); }
            };
            checkApp.Run(); return;
        }
        if (args.Length == 2 && args[0] == "--catalog-concurrency-check")
        {
            var checkApp = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            checkApp.Startup += async (_, _) =>
            {
                try { await CatalogConcurrencyCheck.RunAsync(args[1]); File.Delete(Path.Combine(args[1], "catalog-concurrency-error.txt")); }
                catch (Exception error) { File.WriteAllText(Path.Combine(args[1], "catalog-concurrency-error.txt"), error.ToString()); Environment.ExitCode = 1; }
                finally { checkApp.Shutdown(); }
            };
            checkApp.Run(); return;
        }
        if (args.Length == 2 && args[0] == "--player-health-read-check")
        {
            var responseApp = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            responseApp.Startup += async (_, _) =>
            {
                try { await PlayerHealthReadCheck.RunAsync(args[1]); File.Delete(Path.Combine(args[1], "player-health-read-error.txt")); }
                catch (Exception error) { File.WriteAllText(Path.Combine(args[1], "player-health-read-error.txt"), error.ToString()); Environment.ExitCode = 1; }
                finally { responseApp.Shutdown(); }
            };
            responseApp.Run(); return;
        }
        if (args.Length == 2 && args[0] == "--player-capture-audio-check")
        {
            var responseApp = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            responseApp.Startup += async (_, _) =>
            {
                try { await PlayerCaptureAudioCheck.RunAsync(args[1]); File.Delete(Path.Combine(args[1], "player-capture-audio-error.txt")); }
                catch (Exception error) { File.WriteAllText(Path.Combine(args[1], "player-capture-audio-error.txt"), error.ToString()); Environment.ExitCode = 1; }
                finally { responseApp.Shutdown(); }
            };
            responseApp.Run(); return;
        }
        if (args.Length == 2 && args[0] == "--ui-responsiveness-check")
        {
            var responseApp = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            responseApp.Startup += async (_, _) =>
            {
                try { await UiResponsivenessCheck.RunAsync(args[1]); File.Delete(Path.Combine(args[1], "ui-responsiveness-error.txt")); }
                catch (Exception error) { File.WriteAllText(Path.Combine(args[1], "ui-responsiveness-error.txt"), error.ToString()); Environment.ExitCode = 1; }
                finally { responseApp.Shutdown(); }
            };
            responseApp.Run(); return;
        }
        if (args.Length == 3 && args[0] == "--ui-appearance")
        {
            var appearanceApp = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            appearanceApp.Startup += async (_, _) =>
            {
                try { await UiAppearanceCheck.RunAsync(args[1], args[2]); File.Delete(Path.Combine(args[2], "error.txt")); }
                catch (Exception error) { Directory.CreateDirectory(args[2]); File.WriteAllText(Path.Combine(args[2], "error.txt"), error.ToString()); Environment.ExitCode = 1; }
                finally { appearanceApp.Shutdown(); }
            };
            appearanceApp.Run(); return;
        }
        if (args.Length == 1 && args[0] == "--audio-anchor")
        { Console.In.ReadLine(); return; }
        if (args.Length == 2 && args[0] == "--player-av-start-check")
        {
            var checkApp = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            checkApp.Startup += async (_, _) =>
            {
                try { await PlayerAvStartCheck.RunAsync(args[1]); File.Delete(Path.Combine(args[1], "player-av-start-error.txt")); }
                catch (Exception error) { File.WriteAllText(Path.Combine(args[1], "player-av-start-error.txt"), error.ToString()); Environment.ExitCode = 1; }
                finally { checkApp.Shutdown(); }
            };
            checkApp.Run(); return;
        }
        if (args.Length == 2 && args[0] == "--player-audio-check")
        {
            var checkApp = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            checkApp.Startup += async (_, _) =>
            {
                try { await PlayerAudioCheck.RunAsync(args[1]); File.Delete(Path.Combine(args[1], "player-audio-error.txt")); }
                catch (Exception error) { File.WriteAllText(Path.Combine(args[1], "player-audio-error.txt"), error.ToString()); Environment.ExitCode = 1; }
                finally { checkApp.Shutdown(); }
            };
            checkApp.Run(); return;
        }
        if (args.Length == 2 && args[0] == "--audio-tone")
        { AudioRoutingCheck.ToneAsync(args[1]).GetAwaiter().GetResult(); return; }
        if (args.Length == 2 && args[0] == "--audio-routing-check")
        {
            try { AudioRoutingCheck.RunAsync(args[1]).GetAwaiter().GetResult(); File.Delete(Path.Combine(args[1], "audio-routing-error.txt")); }
            catch (Exception error) { File.WriteAllText(Path.Combine(args[1], "audio-routing-error.txt"), error.ToString()); Environment.ExitCode = 1; }
            return;
        }
        if (args.Length == 3 && args[0] == "--close-prototype")
        {
            try { PrototypeControl.Close(int.Parse(args[1], System.Globalization.CultureInfo.InvariantCulture), args[2]); }
            catch (Exception error) { Console.Error.WriteLine(error.Message); Environment.ExitCode = 1; }
            return;
        }
        if (args.Length == 2 && args[0] == "--color-playback")
        {
            var checkApp = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            checkApp.Startup += async (_, _) =>
            {
                try { await ColorPlaybackCheck.RunAsync(args[1]); }
                catch (Exception error) { File.WriteAllText(Path.Combine(args[1], "wpf-error.txt"), error.ToString()); Environment.ExitCode = 1; }
                finally { checkApp.Shutdown(); }
            };
            checkApp.Run();
            return;
        }
        if (args.Length == 2 && args[0] == "--capture-smoke")
        {
            try
            {
                CaptureSmoke.RunAsync(args[1]).GetAwaiter().GetResult();
                File.Delete(Path.Combine(args[1], "capture-smoke-error.txt"));
            }
            catch (Exception error)
            {
                Directory.CreateDirectory(args[1]);
                File.WriteAllText(Path.Combine(args[1], "capture-smoke-error.txt"), error.ToString());
                Environment.ExitCode = 1;
            }
            return;
        }
        if (args.Length == 2 && args[0] == "--monitors")
        {
            File.WriteAllText(args[1], JsonSerializer.Serialize(MonitorCatalog.Read(), new JsonSerializerOptions { WriteIndented = true }));
            return;
        }
        if (args.Length == 2 && args[0] == "--validate")
        {
            var validationApp = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            validationApp.Startup += async (_, _) =>
            {
                try
                {
                    using var telemetry = new UiTelemetry(validationApp.Dispatcher, args[1], () => false);
                    var player = new ReplayPlayerView(telemetry, args[1]);
                    await player.ClosePlayerAsync();
                    var panel = new OverlayWindow([], args[1]);
                    try { CheckCaptureIndicator(panel); }
                    finally { panel.Close(); }
                    OverlayRegionCheck.Run(args[1]);
                    File.Delete(Path.Combine(args[1], "error.txt"));
                }
                catch (Exception error) { File.WriteAllText(Path.Combine(args[1], "error.txt"), error.ToString()); Environment.ExitCode = 1; }
                finally { validationApp.Shutdown(); }
            };
            validationApp.Run();
            return;
        }
        AppLaunchOptions launch = AppLaunchOptions.Parse(args, AppContext.BaseDirectory);
        string manifest = launch.Manifest;
        string diagnostics = launch.DataDirectory;
        Directory.CreateDirectory(diagnostics);
        if (!File.Exists(manifest)) File.WriteAllText(manifest, "[]");
        string instanceId = AppLaunchOptions.InstanceId(diagnostics);
        string instanceName = "Local\\Rewind-" + instanceId;
        using var instance = new Mutex(true, instanceName, out bool firstInstance);
        if (!firstInstance)
        {
            if (!launch.Background) Native.PostMessage(0xffff, Native.RegisterWindowMessage("Rewind.Show." + instanceId), 0, 0);
            return;
        }
        Clip[] clips = JsonSerializer.Deserialize<Clip[]>(File.ReadAllText(manifest),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? throw new InvalidDataException("No clips.");
        var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
        var mainWindow = new OverlayWindow(clips, diagnostics, launch.ReplayDirectory);
        app.MainWindow = mainWindow;
        if (launch.Section is string section) mainWindow.ShowSettingsSection(section);
        app.Startup += async (_, _) =>
        {
            _ = new System.Windows.Interop.WindowInteropHelper(mainWindow).EnsureHandle();
            if (!launch.Background) mainWindow.ShowOverlay();
            await mainWindow.RestoreStartupRecordingAsync();
        };
        app.Run();
    }
}

public sealed record Clip(string Title, string Subtitle, string Video, string Thumbnail,
    string?[] Audio, double Duration, double Fps, bool EmbeddedSystemAudio = false, string[]? EmbeddedAudioSources = null,
    string? MonitorName = null, string? EventId = null, Clip[]? Screens = null,
    string? ApplicationName = null, string? SessionId = null, DateTime? SessionStartedUtc = null, DateTime? SavedAtUtc = null);
