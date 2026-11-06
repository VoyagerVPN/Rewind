using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace Rewind.Preview;

internal static class PlayerPerformanceCheck
{
    private sealed record Sample(double WallSeconds, double VideoSeconds, double AudioSeconds, double UiSeconds, double UiIntervalMs, VideoPlaybackStatistics? Statistics);
    private sealed record Segment(string Name, double Seconds, double? DisplayedFps, int? LostFrames, double WorkerCpuCores, double UiIntervalP95Ms, double UiIntervalMaxMs, double UiClockFreezeMs, double SyncP95Seconds, Sample[] Samples);
    private sealed record Seek(double Target, double Milliseconds, double Video, double Audio, bool WhilePlaying, bool Ready);
    public static async Task RunAsync(string file, string folder)
    {
        string run = Path.Combine(folder, "player-performance-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(run);
        using var telemetry = new UiTelemetry(Dispatcher.CurrentDispatcher, run, () => false);
        using var index = new ReplayLibraryIndex(Path.Combine(run, "catalog"), telemetry);
        var player = new ReplayPlayerView(telemetry, run);
        bool visible = Environment.GetEnvironmentVariable("REWIND_PERFORMANCE_VISIBLE") == "1";
        var host = new Window { Content = player, Width = 1280, Height = 830, Left = visible ? 560 : -20000, Top = visible ? 80 : -20000,
            ShowActivated = false, ShowInTaskbar = false, Topmost = visible, WindowStyle = WindowStyle.None };
        host.Show();
        if (Environment.GetEnvironmentVariable("REWIND_PERFORMANCE_TRACE") == "1") Environment.SetEnvironmentVariable("REWIND_VIDEO_TRACE_FOLDER", run);
        var segments = new List<Segment>(); var seeks = new List<Seek>();
        async Task<string> HashAsync()
        {
            await using FileStream input = new(file, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            return Convert.ToHexString(await SHA256.HashDataAsync(input));
        }
        bool verifySourceHash = Environment.GetEnvironmentVariable("REWIND_PERFORMANCE_HASH") != "0";
        string? sourceHash = verifySourceHash ? await HashAsync() : null;
        double preparationMs = 0, loadMs = 0;
        async Task ObserveAsync(string name, double seconds)
        {
            if (!player.IsPlaying) player.TogglePlayback(); await Task.Delay(500);
            using Process worker = Process.GetProcessById(player.VideoProcessId ?? throw new InvalidOperationException("Нет видеопроцесса."));
            double cpuBefore = worker.TotalProcessorTime.TotalSeconds;
            VideoPlaybackStatistics? before = player.PlaybackStatistics;
            var watch = Stopwatch.StartNew(); var samples = new List<Sample>(); double previous = 0;
            while (watch.Elapsed.TotalSeconds < seconds)
            {
                await Task.Delay(16);
                double wall = watch.Elapsed.TotalSeconds;
                samples.Add(new(wall, player.VideoPositionSeconds, player.AudioPositionSeconds, player.PositionSeconds, (wall - previous) * 1000, player.PlaybackStatistics));
                previous = wall;
                if (player.HasPlaybackError) break;
            }
            VideoPlaybackStatistics? after = player.PlaybackStatistics;
            double elapsed = watch.Elapsed.TotalSeconds;
            worker.Refresh();
            double lastChange = 0, lastPosition = -1, freezeMs = 0;
            foreach (Sample sample in samples)
            {
                if (Math.Abs(sample.UiSeconds - lastPosition) > .001) { lastChange = sample.WallSeconds; lastPosition = sample.UiSeconds; }
                freezeMs = Math.Max(freezeMs, (sample.WallSeconds - lastChange) * 1000);
            }
            segments.Add(new(name, elapsed, after?.DisplayedFrames is int displayedAfter && before?.DisplayedFrames is int displayedBefore ? (displayedAfter - displayedBefore) / elapsed : null,
                after?.LostFrames is int lostAfter && before?.LostFrames is int lostBefore ? lostAfter - lostBefore : null, (worker.TotalProcessorTime.TotalSeconds - cpuBefore) / elapsed,
                Percentile(samples.Select(sample => sample.UiIntervalMs), .95), samples.Select(sample => sample.UiIntervalMs).DefaultIfEmpty().Max(),
                freezeMs, Percentile(samples.Select(sample => Math.Abs(sample.VideoSeconds - sample.AudioSeconds)), .95), samples.ToArray()));
            player.PausePlayback();
        }
        async Task SeekAsync(double target, bool rapid = false, bool whilePlaying = false)
        {
            if (whilePlaying && !player.IsPlaying) player.TogglePlayback();
            var watch = Stopwatch.StartNew();
            if (rapid)
                foreach (double point in new[] { .25, .7, .4, .8 }) { player.SeekTo(Math.Max(target / 3, point * target)); await Task.Delay(35); }
            player.SeekTo(target);
            bool Ready() => !player.IsLoading && !player.HasPlaybackError &&
                (whilePlaying ? player.VideoPositionSeconds >= target - .35 && player.VideoPositionSeconds < target + 2 && Math.Abs(player.AudioPositionSeconds - player.VideoPositionSeconds) <= .6 :
                    Math.Abs(player.VideoPositionSeconds - target) <= .35 && Math.Abs(player.AudioPositionSeconds - target) <= .35) &&
                ((TextBlock)player.FindName("PlayerStateText")).Text == (whilePlaying ? "Воспроизведение" : "Пауза");
            while (!Ready() && watch.Elapsed.TotalSeconds < 5) await Task.Delay(20);
            seeks.Add(new(target, watch.Elapsed.TotalMilliseconds, player.VideoPositionSeconds, player.AudioPositionSeconds, whilePlaying, Ready()));
        }
        try
        {
            PlaybackHealth health = await new PlaybackHealthStore(Path.Combine(folder, "library-cache", "playback-health")).ReadAsync(file, CancellationToken.None);
            string healthFolder = Path.Combine(run, "library-cache", "playback-health"); Directory.CreateDirectory(healthFolder);
            await File.WriteAllTextAsync(Path.Combine(healthFolder, health.Key + ".json"), JsonSerializer.Serialize(health));
            var timer = Stopwatch.StartNew();
            Clip clip = await index.PreparePlaybackAsync(new Clip(Path.GetFileName(file), "", file, "", [null, null, null], 0, 30), CancellationToken.None);
            preparationMs = timer.Elapsed.TotalMilliseconds;
            timer.Restart(); await player.LoadClipAsync(clip, autoPlay: false);
            loadMs = timer.Elapsed.TotalMilliseconds;
            if (health.Issues.Length > 0 && player.PlaybackStatistics is not { SoftwareForced: false })
                throw new InvalidOperationException("Сохранённые отметки повреждения не должны принудительно включать программное декодирование доступной части записи.");
            DateTime deadline = DateTime.UtcNow.AddSeconds(4);
            while (player.IsLoading && DateTime.UtcNow < deadline) await Task.Delay(25);
            if (player.IsLoading) throw new InvalidOperationException("Большой клип не перешёл в готовое состояние.");
            await Task.Delay(500);
            await ObserveAsync("available-start", 5);
            foreach (double target in new[] { clip.Duration * .5, clip.Duration * .72, clip.Duration * .35 })
            { await SeekAsync(target, whilePlaying: target > clip.Duration * .7); await ObserveAsync("after-seek-" + target.ToString("F1", System.Globalization.CultureInfo.InvariantCulture), 5); }
            await SeekAsync(clip.Duration * .77, rapid: true, whilePlaying: true);
            await ObserveAsync("after-rapid-playing-seeks", 5);
            if (verifySourceHash && sourceHash != await HashAsync()) throw new InvalidOperationException("Исходный MP4 изменился.");
            if (player.HasPlaybackError || seeks.Any(seek => !seek.Ready)) throw new InvalidOperationException("Плеер не завершил воспроизведение или перемотку: " + JsonSerializer.Serialize(seeks));
            // Без displayed-счётчика ограничиваем reported drops относительно номинального
            // числа кадров источника; это не измерение физической частоты показа.
            if (segments.Any(segment => segment.LostFrames is int lost && lost / Math.Max(1,
                segment.DisplayedFps is double fps ? fps * segment.Seconds + lost : clip.Fps * segment.Seconds) > .1))
                throw new InvalidOperationException("На доступной части видеовыход не должен терять более 10% кадров.");
            if (segments.Any(segment => segment.DisplayedFps is double fps && fps < clip.Fps * .8 || segment.UiClockFreezeMs > 120))
                throw new InvalidOperationException("Воспроизведение и бегунок не должны останавливаться на доступном участке.");
            if (segments.Any(segment => segment.SyncP95Seconds > .15))
                throw new InvalidOperationException("Часы видео и аудио не должны расходиться более чем на 150 мс на доступном участке: " + JsonSerializer.Serialize(segments.Select(segment => new { segment.Name, segment.SyncP95Seconds })));
        }
        finally
        {
            await File.WriteAllTextAsync(Path.Combine(folder, "player-performance.json"), JsonSerializer.Serialize(new
            { run, file, sourceHash, verifySourceHash, preparationMs, loadMs, segments, seeks,
                visible, mode = "real embedded native video and audio; null native FPS is unavailable, not zero; use external coded-image PlaybackLab for presentation evidence" }, new JsonSerializerOptions { WriteIndented = true }));
            await player.ClosePlayerAsync(); host.Close();
        }
    }
    private static double Percentile(IEnumerable<double> values, double percentile)
    { double[] ordered = values.Order().ToArray(); return ordered.Length == 0 ? 0 : ordered[(int)Math.Ceiling((ordered.Length - 1) * percentile)]; }
}
