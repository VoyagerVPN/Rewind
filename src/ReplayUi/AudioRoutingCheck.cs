using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using NAudio.Wave;

namespace Rewind.Preview;

internal static class AudioRoutingCheck
{
    public static async Task ToneAsync(string ready)
    {
        using var player = new WasapiPlayerBuilder().WithLatency(30).Build();
        player.Init(new Tone(660)); player.Play();
        await File.WriteAllTextAsync(ready, "ready");
        var timer = Stopwatch.StartNew();
        while (timer.Elapsed.TotalSeconds < 60 && !File.Exists(ready + ".stop")) await Task.Delay(100);
        player.Stop();
    }

    public static async Task RunAsync(string folder)
    {
        Directory.CreateDirectory(folder);
        string ready = Path.Combine(folder, "tone-" + Guid.NewGuid().ToString("N"));
        using var helper = CaptureProcesses.Start(Environment.ProcessPath!, ["--audio-tone", ready]);
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(50));
            while (!File.Exists(ready)) { if (helper.HasExited) throw new IOException("Не запустился контрольный аудиопроцесс."); await Task.Delay(50, deadline.Token); }
            CaptureMonitor monitor = MonitorCatalog.Read().Single(value => value.Primary);
            var microphone = AudioDeviceCatalog.ResolveMicrophone(null);
            string root = Path.Combine(@"N:\Rewind-QA", "audio-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss"));
            var settings = new CaptureSettings(monitor.DeviceName, 60, 8, 6, root, monitor.Identity,
                CaptureMicrophone: true, MicrophoneDeviceId: microphone.Id);
            SavedReplay saved;
            AudioSourceStatus[] statuses;
            await using (var capture = DesktopReplaySession.Start(settings, monitor, (uint)helper.Id))
            {
                while (capture.Status.LatestSeconds < 3) { Check(capture); await Task.Delay(100, deadline.Token); }
                using var player = new WasapiPlayerBuilder().WithLatency(30).Build();
                player.Init(new Tone(880)); player.Play();
                await Task.Delay(2200, deadline.Token); player.Stop();
                await Task.Delay(600, deadline.Token);
                Check(capture); statuses = capture.AudioStatus;
                if (statuses.Length != 3 || statuses.Any(source => source.Warning is not null) || statuses[2].Device != microphone.Name)
                    throw new InvalidOperationException("Не подключены все три реальных источника: " + JsonSerializer.Serialize(statuses));
                saved = await capture.SaveAsync();
                await capture.StopAsync(); Check(capture);
            }
            if (saved.AudioSources is null || !saved.AudioSources.SequenceEqual(new[] { "system", "discord", "microphone" }))
                throw new InvalidOperationException("В повторе не сохранены три источника.");
            var system = await SpectrumAsync(saved.File, 0);
            var application = await SpectrumAsync(saved.File, 1);
            var mic = await SpectrumAsync(saved.File, 2);
            if (system.Tone880 < .005 || application.Tone660 < .005 || system.Tone660 > application.Tone660 * .03 || application.Tone880 > system.Tone880 * .03)
                throw new InvalidOperationException("Звук приложения не отделён от системы: " + JsonSerializer.Serialize(new { system, application }));

            // Без Discord нужен тот же непрерывный системный захват; пропавший закреплённый микрофон не заменяется.
            SavedReplay withoutDiscord;
            AudioSourceStatus[] missingStatuses;
            await using (var capture = DesktopReplaySession.Start(settings with { SeparateDiscord = false, MicrophoneDeviceId = "rewind-missing-device" }, monitor))
            {
                using var player = new WasapiPlayerBuilder().WithLatency(30).Build();
                player.Init(new Tone(880)); player.Play();
                while (capture.Status.LatestSeconds < 4) { Check(capture); await Task.Delay(100, deadline.Token); }
                missingStatuses = capture.AudioStatus;
                if (missingStatuses.Single(source => source.Source == "microphone").Warning is null)
                    throw new InvalidOperationException("Пропавший микрофон должен показывать ошибку, а не заменяться другим.");
                withoutDiscord = await capture.SaveAsync(3); player.Stop();
                await capture.StopAsync(); Check(capture);
            }
            var allOutput = await SpectrumAsync(withoutDiscord.File, 0);
            if (allOutput.Tone880 < .005 || allOutput.Tone660 < .005)
                throw new InvalidOperationException("Общий захват без разделения должен содержать оба процесса: " + JsonSerializer.Serialize(new { allOutput, missingStatuses }));
            SavedReplay discordOnly;
            await using (var capture = DesktopReplaySession.Start(settings with { CaptureSystemAudio = false, CaptureMicrophone = false, SeparateDiscord = true }, monitor, (uint)helper.Id))
            {
                while (capture.Status.LatestSeconds < 4) { Check(capture); await Task.Delay(100, deadline.Token); }
                discordOnly = await capture.SaveAsync(3);
                await capture.StopAsync(); Check(capture);
            }
            var solo = await SpectrumAsync(discordOnly.File, 0);
            if (discordOnly.AudioSources is null || !discordOnly.AudioSources.SequenceEqual(new[] { "discord" }) || solo.Tone660 < .005)
                throw new InvalidOperationException("Отдельный Discord должен записываться при выключенных системе и микрофоне.");
            await File.WriteAllTextAsync(Path.Combine(folder, "audio-routing.json"), JsonSerializer.Serialize(new
            {
                completedUtc = DateTime.UtcNow, saved, statuses, system, application, microphone = mic,
                withoutDiscord, allOutput, missingStatuses, discordOnly, solo,
                verified = "Real process include/exclude, three AAC tracks, selected microphone connected, missing microphone is not replaced, all-output capture without Discord separation, Discord-only recording with system and microphone off.",
                limitations = "Controlled audio process represents Discord; no remote call, physical unplug, default-device switch, long-session drift, or game performance test."
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
        finally
        {
            await File.WriteAllTextAsync(ready + ".stop", "stop");
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try { await helper.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException) { helper.Kill(entireProcessTree: true); await helper.WaitForExitAsync(); }
            File.Delete(ready); File.Delete(ready + ".stop");
        }
    }

    private static void Check(DesktopReplaySession capture)
    { if (capture.Status.Error is string error) throw new InvalidOperationException(error); }

    public sealed record Spectrum(double Peak, double Tone660, double Tone880);
    public static async Task<Spectrum> SpectrumAsync(string file, int track)
    {
        using var process = CaptureProcesses.Start(CaptureProcesses.Ffmpeg,
            ["-hide_banner", "-v", "error", "-nostdin", "-i", file, "-map", $"0:a:{track}", "-ac", "1", "-ar", "48000", "-f", "f32le", "pipe:1"]);
        Task<string> errors = process.StandardError.ReadToEndAsync();
        using var bytes = new MemoryStream();
        await process.StandardOutput.BaseStream.CopyToAsync(bytes); await process.WaitForExitAsync();
        if (process.ExitCode != 0 || !string.IsNullOrWhiteSpace(await errors)) throw new IOException("Не декодируется аудиодорожка.");
        return Analyze(MemoryMarshal.Cast<byte, float>(bytes.ToArray()).ToArray());
    }

    public static Spectrum Analyze(float[] samples, int discordFrequency = 660, int microphoneFrequency = 880)
    {
        double ToneLevel(int frequency)
        {
            double maximum = 0;
            const int window = 4800;
            for (int start = 0; start + window <= samples.Length; start += window)
            {
                double real = 0, imaginary = 0;
                for (int index = 0; index < window; index++)
                { double angle = index * 2 * Math.PI * frequency / 48000; real += samples[start + index] * Math.Cos(angle); imaginary += samples[start + index] * Math.Sin(angle); }
                maximum = Math.Max(maximum, 2 * Math.Sqrt(real * real + imaginary * imaginary) / window);
            }
            return maximum;
        }
        return new Spectrum(samples.Select(sample => Math.Abs((double)sample)).DefaultIfEmpty().Max(), ToneLevel(discordFrequency), ToneLevel(microphoneFrequency));
    }

    internal sealed class Tone(int frequency) : IWaveProvider
    {
        private long _frame;
        public WaveFormat WaveFormat { get; } = new(48000, 16, 2);
        public int Read(Span<byte> buffer)
        {
            for (int offset = 0; offset + 3 < buffer.Length; offset += 4)
            {
                short sample = (short)(Math.Sin(_frame++ * Math.PI * 2 * frequency / 48000) * .02 * short.MaxValue);
                buffer[offset] = buffer[offset + 2] = (byte)sample;
                buffer[offset + 1] = buffer[offset + 3] = (byte)(sample >> 8);
            }
            return buffer.Length;
        }
    }
}
