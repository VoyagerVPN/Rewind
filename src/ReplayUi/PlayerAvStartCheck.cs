using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace Rewind.Preview;

internal static class PlayerAvStartCheck
{
    private sealed record Start(string Phase, double PictureMs, double SoundMs, double Video, double Audio);
    public static async Task RunAsync(string diagnostics)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 20348)) throw new PlatformNotSupportedException();
        string run = Path.Combine(diagnostics, "player-av-start-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(run);
        Environment.SetEnvironmentVariable("REWIND_VIDEO_TRACE_FOLDER", run);
        string file = Path.Combine(run, "red-green.mp4");
        await CaptureProcesses.RunAsync(CaptureProcesses.Ffmpeg,
            ["-v", "error", "-nostdin", "-y", "-f", "lavfi", "-i", "color=red:s=1280x720:r=60:d=8", "-f", "lavfi", "-i", "color=lime:s=1280x720:r=60:d=8",
             "-f", "lavfi", "-i", "sine=frequency=880:sample_rate=48000:duration=16", "-filter_complex", "[0:v][1:v]concat=n=2:v=1:a=0[v]",
             "-map", "[v]", "-map", "2:a", "-c:v", "libx264", "-preset", "ultrafast", "-g", "240", "-c:a", "aac", file], TimeSpan.FromSeconds(30));
        using var telemetry = new UiTelemetry(Dispatcher.CurrentDispatcher, run, () => true);
        var player = new ReplayPlayerView(telemetry, run);
        var host = new Window { Content = player, Width = 1040, Height = 689, Left = 560, Top = 80,
            ShowActivated = false, ShowInTaskbar = false, Topmost = true, WindowStyle = WindowStyle.None };
        host.Show();
        await using var recorder = await new WasapiRecorderBuilder()
            .WithProcessLoopback((uint)Environment.ProcessId, ProcessLoopbackMode.IncludeTargetProcessTree)
            .WithFormat(WaveFormat.CreateIeeeFloatWaveFormat(48000, 2)).BuildAsync();
        long origin = 0, sound = 0;
        int timestampFallbacks = 0;
        recorder.DataAvailable += (buffer, flags, _, qpcPosition) =>
        {
            long began = Interlocked.Read(ref origin);
            if (began == 0 || ((int)flags & 2) != 0) return;
            ReadOnlySpan<float> samples = MemoryMarshal.Cast<byte, float>(buffer);
            for (int index = 0; index < samples.Length; index++)
                if (Math.Abs(samples[index]) > .001f)
                {
                    bool fallback = qpcPosition == 0 || ((int)flags & 4) != 0;
                    long captured = fallback ? Stopwatch.GetTimestamp() : (long)(qpcPosition * (double)Stopwatch.Frequency / 10000000d + index / 2d / 48000 * Stopwatch.Frequency);
                    if (captured < began) continue;
                    if (fallback) Interlocked.Increment(ref timestampFallbacks);
                    Interlocked.CompareExchange(ref sound, captured, 0); break;
                }
        };
        var starts = new List<Start>();
        async Task ObserveAsync(string phase, bool green, Action begin)
        {
            Interlocked.Exchange(ref sound, 0); Interlocked.Exchange(ref origin, Stopwatch.GetTimestamp());
            long picture = 0;
            Point point = player.PointToScreen(new Point(player.ActualWidth / 2, player.ActualHeight / 2));
            begin();
            DateTime deadline = DateTime.UtcNow.AddSeconds(5);
            while ((picture == 0 || Interlocked.Read(ref sound) == 0) && DateTime.UtcNow < deadline)
            {
                nint dc = GetDC(0); uint pixel;
                try { pixel = GetPixel(dc, (int)point.X, (int)point.Y); } finally { ReleaseDC(0, dc); }
                int red = (int)(pixel & 255), channelGreen = (int)((pixel >> 8) & 255);
                if (picture == 0 && (green ? channelGreen > 160 && red < 80 : red > 160 && channelGreen < 80)) picture = Stopwatch.GetTimestamp();
                await Task.Delay(4);
            }
            long firstSound = Interlocked.Read(ref sound), began = Interlocked.Exchange(ref origin, 0);
            double pictureMs = picture == 0 ? -1 : Stopwatch.GetElapsedTime(began, picture).TotalMilliseconds;
            double soundMs = firstSound == 0 ? -1 : Stopwatch.GetElapsedTime(began, firstSound).TotalMilliseconds;
            starts.Add(new(phase, pictureMs, soundMs, player.VideoPositionSeconds, player.AudioPositionSeconds));
            if (pictureMs < 0 || soundMs < 0 || soundMs + 40 < pictureMs)
                throw new InvalidOperationException("Звук должен начинаться после появления нужного видеокадра: " + JsonSerializer.Serialize(starts[^1]));
            // Сравниваем часы после новой метки проигрывателя, чтобы не учитывать старое значение.
            double previousVideo = player.VideoPositionSeconds;
            deadline = DateTime.UtcNow.AddSeconds(2);
            while (player.VideoPositionSeconds <= previousVideo && DateTime.UtcNow < deadline) await Task.Delay(4);
            if (Math.Abs(player.VideoPositionSeconds - player.AudioPositionSeconds) > .35)
                throw new InvalidOperationException($"После запуска звук и видео должны сохранять общую позицию: video={player.VideoPositionSeconds:F3}, audio={player.AudioPositionSeconds:F3}.");
            player.PausePlayback(); await Task.Delay(150);
        }
        try
        {
            recorder.StartRecording();
            Task? loading = null;
            await ObserveAsync("open", false, () => loading = player.LoadClipAsync(new Clip("AV start", "", file, "", [null, null, null], 16, 60, EmbeddedSystemAudio: true)));
            await loading!;
            await ObserveAsync("seek-forward", true, () => { player.SeekTo(9); player.TogglePlayback(); });
            await ObserveAsync("seek-backward", false, () => { player.SeekTo(3); player.TogglePlayback(); });
            await ObserveAsync("prepare-green-for-reopen", true, () => { player.SeekTo(9); player.TogglePlayback(); });
            await ObserveAsync("cached-reopen", false, () => loading = player.LoadClipAsync(new Clip("AV start", "", file, "", [null, null, null], 16, 60, EmbeddedSystemAudio: true)));
            await loading!;
        }
        finally
        {
            await File.WriteAllTextAsync(Path.Combine(diagnostics, "player-av-start.json"), JsonSerializer.Serialize(new { run, starts, timestampFallbacks,
                mode = "visible native HWND red/green desktop pixels; WASAPI packet QPC plus nonzero sample offset; 40ms tolerance" }, new JsonSerializerOptions { WriteIndented = true }));
            await player.ClosePlayerAsync(); host.Close();
        }
    }
    [DllImport("user32.dll")] private static extern nint GetDC(nint window);
    [DllImport("user32.dll")] private static extern int ReleaseDC(nint window, nint dc);
    [DllImport("gdi32.dll")] private static extern uint GetPixel(nint dc, int x, int y);
}
