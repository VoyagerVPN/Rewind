using System.IO;
using System.Text.Json;
using NAudio.Wave;

namespace Rewind.Preview;

internal static class CaptureSmoke
{
    public static async Task RunAsync(string directory)
    {
        Directory.CreateDirectory(directory);
        CaptureMonitor monitor = MonitorCatalog.Read().Single(value => value.Primary);
        string storage = Path.Combine(@"N:\Rewind-QA", "desktop-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..6]);
        var settings = new CaptureSettings(monitor.DeviceName, 120, 8, 6, storage, monitor.Identity, CaptureMicrophone: true);
        await using var capture = DesktopReplaySession.Start(settings, monitor);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(55));
        while (capture.Status.LatestSeconds < 24)
        {
            if (capture.Status.Error is string error) throw new InvalidOperationException(error);
            await Task.Delay(100, timeout.Token);
        }
        using (var sound = new WasapiPlayerBuilder().WithLatency(30).Build())
        {
            sound.Init(new TestTone());
            sound.Play();
            await Task.Delay(2000, timeout.Token);
            sound.Stop();
        }
        SavedReplay first = await capture.SaveAsync();
        if (first.AudioSources is null || !first.AudioSources.SequenceEqual(new[] { "system", "discord", "microphone" }))
            throw new InvalidOperationException("A replay must retain separate system, Discord, and selected microphone tracks.");
        await Task.Delay(1000, timeout.Token);
        SavedReplay second = await capture.SaveAsync(5);
        if (first.Width != monitor.Width || first.Height != monitor.Height || second.Width != monitor.Width || second.Height != monitor.Height ||
            Math.Abs(first.Fps - 120) > 0.1 || Math.Abs(second.Fps - 120) > 0.1)
            throw new InvalidOperationException("Saved video must keep native resolution and the configured file frame rate.");
        if (second.RequestedEndSeconds <= first.RequestedEndSeconds ||
            second.RequestedEndSeconds - second.RequestedSeconds >= first.RequestedEndSeconds)
            throw new InvalidOperationException("Second replay must include overlapping past; saving must not reset history.");
        ReplayStatus beforeStop = capture.Status;
        int bufferedFiles = Directory.EnumerateFiles(Path.Combine(storage, "Buffer"), "part*." + ReplayExport.Extension, SearchOption.AllDirectories).Count();
        if (bufferedFiles > 11) throw new InvalidOperationException("Old desktop fragments must be evicted while capture continues.");
        await capture.StopAsync();
        if (capture.Status.Error is not null) throw new InvalidOperationException(capture.Status.Error);
        if (capture.Status.BufferBytes != 0 || capture.Status.AvailableSeconds != 0 ||
            Directory.EnumerateFiles(Path.Combine(storage, "Buffer"), "part*." + ReplayExport.Extension, SearchOption.AllDirectories).Any())
            throw new InvalidOperationException("Stopped capture must release temporary video storage and report no available history.");
        foreach (SavedReplay replay in new[] { first, second })
        {
            await CaptureProcesses.RunAsync(CaptureProcesses.Ffmpeg,
                ["-hide_banner", "-v", "error", "-nostdin", "-i", replay.File, "-map", "0", "-fps_mode", "passthrough", "-enc_time_base", "demux", "-f", "null", "-"], TimeSpan.FromSeconds(30));
            if (!File.Exists(replay.File) || new FileInfo(replay.File).Length == 0)
                throw new InvalidOperationException("Stopping capture must preserve saved files.");
        }
        if (!first.HasSystemAudio || !second.HasSystemAudio || second.RequestedSeconds != 5)
            throw new InvalidOperationException("Both replays need audio; the second must use the selected non-default interval.");
        double peak = await AudioPeakAsync(first.File);
        if (peak < 0.002) throw new InvalidOperationException("No audible signal was found in the real loopback recording.");
        await File.WriteAllTextAsync(Path.Combine(directory, "capture-smoke.json"), JsonSerializer.Serialize(new
        {
            completedUtc = DateTime.UtcNow, settings, monitor, first, second, beforeStop, bufferedFiles, capture.Status, systemAudioPeak = peak,
            verified = "Two independent overlapping desktop replays, different durations, real WASAPI system audio, NVENC, full video/audio decode, cleanup preserves saved files.",
            limitations = "Short desktop test, not game performance, true 120 unique frames, or precise long-session A/V synchronization proof. Source separation and actual player mixer are checked separately by audio-routing and player-audio."
        }, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static async Task<double> AudioPeakAsync(string file)
    {
        using var process = CaptureProcesses.Start(CaptureProcesses.Ffmpeg,
            ["-hide_banner", "-v", "error", "-nostdin", "-i", file, "-map", "0:a:0", "-ac", "1", "-ar", "48000", "-f", "s16le", "pipe:1"]);
        Task<string> errors = process.StandardError.ReadToEndAsync();
        using var bytes = new MemoryStream();
        await process.StandardOutput.BaseStream.CopyToAsync(bytes);
        await process.WaitForExitAsync();
        if (process.ExitCode != 0 || !string.IsNullOrWhiteSpace(await errors)) throw new InvalidDataException("Audio decode failed.");
        byte[] data = bytes.ToArray();
        double peak = 0;
        for (int index = 0; index + 1 < data.Length; index += 2) peak = Math.Max(peak, Math.Abs(BitConverter.ToInt16(data, index) / 32768.0));
        return peak;
    }

    private sealed class TestTone : IWaveProvider
    {
        private long _frame;
        public WaveFormat WaveFormat { get; } = new(48000, 16, 2);
        public int Read(Span<byte> buffer)
        {
            for (int offset = 0; offset + 3 < buffer.Length; offset += 4)
            {
                short sample = (short)(Math.Sin(_frame++ * Math.PI * 2 * 660 / 48000) * 0.02 * short.MaxValue);
                buffer[offset] = buffer[offset + 2] = (byte)sample;
                buffer[offset + 1] = buffer[offset + 3] = (byte)(sample >> 8);
            }
            return buffer.Length;
        }
    }
}
