using System.Diagnostics;
using System.IO;
using System.Text.Json;
using NAudio.Wave;
using System.Windows;

namespace Rewind.Preview;

internal static class AudioMeterCheck
{
    public static async Task RunAsync(string folder)
    {
        Directory.CreateDirectory(folder);
        string ready = Path.Combine(folder, "meter-tone-" + Guid.NewGuid().ToString("N"));
        using var helper = CaptureProcesses.Start(Environment.ProcessPath!, ["--audio-tone", ready]);
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            while (!File.Exists(ready)) { if (helper.HasExited) throw new IOException("Не запустился источник проверки уровней."); await Task.Delay(20, deadline.Token); }
            var settings = new CaptureSettings("meter-only", 30, 4, 1, folder, CaptureSystemAudio: false, CaptureMicrophone: true, SeparateDiscord: true);
            AudioSourceStatus[] statuses;
            await using (var monitor = new AudioMonitorSession(settings, (uint)helper.Id))
            {
                do
                {
                    await Task.Delay(33, deadline.Token);
                    statuses = monitor.Status;
                } while (!statuses.Any(source => source.Source == "discord" && source.Peak > 0.01) ||
                    !statuses.Any(source => source.Source == "microphone" && source.Connected && source.Packets > 0));
                if (statuses.Any(source => source.Source == "system") || statuses.Length != 2 || statuses.Any(source => source.Warning is not null))
                    throw new InvalidOperationException("Discord и микрофон должны измеряться независимо от выключенной системы: " + JsonSerializer.Serialize(statuses));
            }
            AudioSourceStatus[] allSources;
            await using (var monitor = new AudioMonitorSession(settings with { CaptureSystemAudio = true }, (uint)helper.Id))
            {
                using var player = new WasapiPlayerBuilder().WithLatency(30).Build();
                player.Init(new AudioRoutingCheck.Tone(880)); player.Play();
                do
                {
                    await Task.Delay(33, deadline.Token);
                    allSources = monitor.Status;
                } while (!allSources.Any(source => source.Source == "system" && source.Peak > .01) ||
                    !allSources.Any(source => source.Source == "discord" && source.Peak > .01) ||
                    !allSources.Any(source => source.Source == "microphone" && source.Connected && source.Packets > 0));
                if (allSources.Length != 3 || allSources.Any(source => source.Warning is not null))
                    throw new InvalidOperationException("Система, Discord и микрофон должны измеряться одновременно: " + JsonSerializer.Serialize(allSources));
                await CheckPanelAsync(folder, deadline.Token);
                player.Stop();
            }
            CaptureMonitor screen = MonitorCatalog.Read().First(source => source.Primary);
            string recordingFolder = Path.Combine(@"N:\Rewind-QA", "discord-only-" + Guid.NewGuid().ToString("N"));
            var soloSettings = new CaptureSettings(screen.DeviceName, 30, 6, 2, recordingFolder, screen.Identity,
                CaptureSystemAudio: false, CaptureMicrophone: false, SeparateDiscord: true,
                MonitorProfiles: [new MonitorRecordingProfile(screen.DeviceName, true, 30, 360, screen.Identity)]);
            SavedReplay discordOnly;
            await using (var capture = DesktopReplaySession.Start(soloSettings, screen, (uint)helper.Id))
            {
                while (capture.Status.LatestSeconds < 4)
                {
                    if (capture.Status.Error is string error) throw new InvalidOperationException(error);
                    await Task.Delay(50, deadline.Token);
                }
                discordOnly = await capture.SaveAsync(2);
            }
            var soloSpectrum = await AudioRoutingCheck.SpectrumAsync(discordOnly.File, 0);
            if (discordOnly.AudioSources is null || !discordOnly.AudioSources.SequenceEqual(new[] { "discord" }) || soloSpectrum.Tone660 < .005)
                throw new InvalidOperationException("Discord должен сохраняться даже при выключенных системе и микрофоне.");
            await File.WriteAllTextAsync(Path.Combine(folder, "audio-meters.json"), JsonSerializer.Serialize(new
            { completedUtc = DateTime.UtcNow, statuses, allSources, discordOnly, soloSpectrum, verified = "Live monitoring without video or encoder, simultaneous system/Discord/microphone levels, independent Discord with system off, physical microphone connected and producing packets; invisible WPF panel starts/stops/resumes preview on audio view show/hide; actual Discord-only MP4 when system and microphone are off",
                limitations = "Controlled process stands in for Discord. Microphone packets and levels are measured; intelligible speech, game performance and endpoint switching are not verified." }, new JsonSerializerOptions { WriteIndented = true }));
        }
        finally
        {
            await File.WriteAllTextAsync(ready + ".stop", "stop");
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            try { await helper.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException) { helper.Kill(entireProcessTree: true); await helper.WaitForExitAsync(); }
            File.Delete(ready); File.Delete(ready + ".stop");
        }
    }

    private static async Task CheckPanelAsync(string diagnostics, CancellationToken cancellation)
    {
        string folder = Path.Combine(diagnostics, "audio-ui-" + Guid.NewGuid().ToString("N"));
        CaptureMonitor monitor = MonitorCatalog.Read().First(source => source.Primary);
        new CaptureSettingsFile(Path.Combine(folder, "capture-settings.json")).Save(new CaptureSettings(monitor.DeviceName, 30, 4, 1, folder));
        new HotkeySettingsFile(Path.Combine(folder, "hotkeys.json")).Save(new HotkeySettings(null, null, null));
        var panel = new OverlayWindow([], folder) { Opacity = 0, ShowActivated = false, Topmost = false, IsHitTestVisible = false };
        var system = (AudioLevelBar)panel.FindName("SystemLevel");
        var microphone = (AudioLevelBar)panel.FindName("MicrophoneLevel");
        bool closed = false;
        panel.Closed += (_, _) => closed = true;
        async Task WaitUntil(Func<bool> predicate)
        {
            while (!predicate()) await Task.Delay(20, cancellation);
        }
        try
        {
            panel.ShowSettingsSection("audio"); panel.Show();
            string microphoneName = AudioDeviceCatalog.ResolveMicrophone(null).Name;
            await WaitUntil(() => system.Peak > .01 && (microphone.ToolTip as string)?.Contains(microphoneName, StringComparison.Ordinal) == true);
            panel.Hide();
            await WaitUntil(() => system.Peak == 0 && (system.ToolTip as string)?.Contains("Источник выключен", StringComparison.Ordinal) == true);
            panel.Show();
            await WaitUntil(() => system.Peak > .01 && (microphone.ToolTip as string)?.Contains(microphoneName, StringComparison.Ordinal) == true);
        }
        finally
        {
            panel.Close();
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (!closed) await Task.Delay(20, cleanup.Token);
        }
    }
}
