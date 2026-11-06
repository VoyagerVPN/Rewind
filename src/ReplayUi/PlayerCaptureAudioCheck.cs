using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace Rewind.Preview;

// Реальный системный захват и отдельный непрерывный источник: декодирование без ошибок не обнаруживает короткие провалы PCM.
internal static class PlayerCaptureAudioCheck
{
    private sealed record Gap(double StartSeconds, double DurationMilliseconds);
    private sealed record Signal(double Seconds, double MedianRms, Gap[] Gaps);
    public static async Task RunAsync(string diagnostics)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 20348)) throw new PlatformNotSupportedException();
        string run = Path.Combine(diagnostics, "player-capture-audio-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(run);
        string ready = Path.Combine(run, "tone-ready");
        using var helper = CaptureProcesses.Start(Environment.ProcessPath!, ["--audio-tone", ready]);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(55));
        async Task WaitAsync(Func<bool> condition, string message)
        {
            while (!condition())
            {
                if (deadline.IsCancellationRequested) throw new TimeoutException(message);
                await Task.Delay(20);
            }
        }
        await WaitAsync(() => File.Exists(ready) || helper.HasExited, "Не запустился контрольный звук.");
        if (helper.HasExited) throw new IOException("Завершился контрольный аудиопроцесс.");
        await using var control = await new WasapiRecorderBuilder()
            .WithProcessLoopback((uint)helper.Id, ProcessLoopbackMode.IncludeTargetProcessTree)
            .WithFormat(WaveFormat.CreateIeeeFloatWaveFormat(48000, 2)).BuildAsync();
        using var packets = new MemoryStream(); object packetGate = new();
        control.DataAvailable += (buffer, flags, _, _) =>
        {
            lock (packetGate) { if (((int)flags & 2) != 0) packets.Write(new byte[buffer.Length]); else packets.Write(buffer); }
        };
        var monitor = MonitorCatalog.Read().Single(item => item.Primary);
        new CaptureSettingsFile(Path.Combine(run, "capture-settings.json")).Save(new CaptureSettings(monitor.DeviceName, 30, 30, 30, run,
            CaptureSystemAudio: true, CaptureMicrophone: false, SeparateDiscord: false,
            MonitorProfiles: [new(monitor.DeviceName, true, 30, 360, monitor.Identity)]));
        new HotkeySettingsFile(Path.Combine(run, "hotkeys.json")).Save(new(null, null, null));
        var clip = new Clip("Контроль", "", Path.Combine(diagnostics, "player-timed-fixture.mp4"),
            Path.Combine(diagnostics, "media", "audio", "thumbnail.jpg"), [null, null, null], 24, 30,
            EmbeddedAudioSources: ["system", "discord", "microphone"]);
        var window = new OverlayWindow([clip], run);
        var root = (FrameworkElement)window.Content; window.Content = null;
        root.Resources.MergedDictionaries.Add(window.Resources);
        var host = new Window { Content = root, Width = 1600, Height = 1000, Left = -20000, Top = -20000,
            ShowActivated = false, ShowInTaskbar = false, WindowStyle = WindowStyle.None };
        T Find<T>(string name) where T : class => (T)window.FindName(name);
        void Click(string name) => Find<Button>(name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var phases = new List<object>();
        bool started = false;
        try
        {
            host.Show(); control.StartRecording();
            Click("CaptureToggle"); started = true;
            await WaitAsync(() => Find<Button>("SaveReplayButton").IsEnabled, "Не подготовился повтор.");
            await Task.Delay(1500);
            for (int iteration = 0; iteration < 3; iteration++)
            {
                phases.Add(new { phase = "open", iteration, utc = DateTimeOffset.UtcNow });
                Task opening = window.OpenClipAsync(clip);
                var player = (ReplayPlayerView)Find<ContentControl>("PlayerContent").Content;
                for (int sourceIndex = 0; sourceIndex < 3; sourceIndex++) player.SetAudioVolume(sourceIndex, 0);
                await opening;
                if (player.HasPlaybackError) throw new InvalidOperationException("Не открылся контрольный клип.");
                await Task.Delay(300);
                phases.Add(new { phase = "reload", iteration, utc = DateTimeOffset.UtcNow });
                await window.OpenClipAsync(clip);
                if (player.HasPlaybackError) throw new InvalidOperationException("Не перезагрузился контрольный клип.");
                await Task.Delay(300);
                phases.Add(new { phase = "close", iteration, utc = DateTimeOffset.UtcNow });
                await window.ClosePlayerExtensionAsync(); await Task.Delay(600);
            }
            await Task.Delay(1500);
            Click("SaveAllScreensButton");
            await WaitAsync(() => Find<TextBlock>("CaptureMessageText").Text.StartsWith("Сохранено", StringComparison.Ordinal), "Не сохранился повтор.");
            string saved = Directory.GetFiles(Path.Combine(run, "Clips"), "*.mp4", SearchOption.AllDirectories).Single();
            Click("CaptureToggle");
            await WaitAsync(() => Find<Button>("CaptureToggle").IsEnabled && Find<TextBlock>("CaptureStatusText").Text == "Запись выключена", "Не остановилась запись."); started = false;
            control.StopRecording();
            using var decoder = CaptureProcesses.Start(CaptureProcesses.Ffmpeg,
                ["-v", "error", "-nostdin", "-xerror", "-i", saved, "-map", "0:a:0", "-ac", "1", "-ar", "48000", "-f", "f32le", "pipe:1"]);
            Task<string> errors = decoder.StandardError.ReadToEndAsync();
            using var bytes = new MemoryStream(); await decoder.StandardOutput.BaseStream.CopyToAsync(bytes);
            await decoder.WaitForExitAsync();
            if (decoder.ExitCode != 0 || !string.IsNullOrWhiteSpace(await errors)) throw new IOException("Не декодируется системная дорожка.");
            Signal replay = Analyze(MemoryMarshal.Cast<byte, float>(bytes.ToArray()).ToArray());
            float[] stereo; lock (packetGate) stereo = MemoryMarshal.Cast<byte, float>(packets.ToArray()).ToArray();
            Signal reference = Analyze(stereo.Where((_, index) => index % 2 == 0).ToArray());
            await File.WriteAllTextAsync(Path.Combine(diagnostics, "player-capture-audio.json"), JsonSerializer.Serialize(new
            { run, saved, replay, reference, phases, measurement = "3 actual overlay player open/reload/close cycles during system capture; external continuous 660 Hz; muted clip tracks; PCM RMS 5 ms bins, gaps >=10 ms below 25% median, trim 1 s edges" }, new JsonSerializerOptions { WriteIndented = true }));
            if (reference.MedianRms < .003 || reference.Gaps.Length != 0) throw new InvalidOperationException("Контрольный источник не был непрерывным.");
            if (replay.MedianRms < .003 || replay.Gaps.Length != 0) throw new InvalidOperationException($"Системная дорожка потеряла контрольный звук: {replay.Gaps.Length} провалов.");
        }
        finally
        {
            control.StopRecording();
            await window.ClosePlayerExtensionAsync();
            if (started)
            {
                await WaitAsync(() => Find<Button>("CaptureToggle").IsEnabled, "Зависло переключение записи."); Click("CaptureToggle");
                await WaitAsync(() => Find<Button>("CaptureToggle").IsEnabled && Find<TextBlock>("CaptureStatusText").Text == "Запись выключена", "Не остановилась запись.");
            }
            host.Content = null; host.Close(); window.Close();
            await File.WriteAllTextAsync(ready + ".stop", "stop");
            try { await helper.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (TimeoutException) { helper.Kill(entireProcessTree: true); await helper.WaitForExitAsync(); }
        }
    }
    private static Signal Analyze(float[] samples)
    {
        const int bin = 240;
        var levels = new List<double>();
        for (int start = 48000; start + bin < samples.Length - 48000; start += bin)
        {
            double sum = 0; for (int index = 0; index < bin; index++) sum += samples[start + index] * samples[start + index];
            levels.Add(Math.Sqrt(sum / bin));
        }
        double median = levels.Order().ElementAtOrDefault(levels.Count / 2);
        var gaps = new List<Gap>(); int? first = null;
        for (int index = 0; index <= levels.Count; index++)
        {
            bool low = index < levels.Count && levels[index] < median * .25;
            if (low) first ??= index;
            else if (first is int begin) { if (index - begin >= 2) gaps.Add(new(1 + begin * .005, (index - begin) * 5)); first = null; }
        }
        return new(samples.Length / 48000d, median, gaps.ToArray());
    }
}
