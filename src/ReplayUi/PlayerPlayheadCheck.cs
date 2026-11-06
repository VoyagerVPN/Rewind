using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace Rewind.Preview;

internal static class PlayerPlayheadCheck
{
    private sealed record Tick(double Wall, double Position);
    private sealed record Motion(string Name, double UpdatesPerSecond, double MaxGapMs, double MaxSpeed, double BackwardSeconds, Tick[] Ticks);
    public static async Task RunAsync(string diagnostics)
    {
        string run = Path.Combine(diagnostics, "player-playhead-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(run);
        string file = Path.Combine(run, "short.mp4");
        await CaptureProcesses.RunAsync(CaptureProcesses.Ffmpeg, ["-hide_banner", "-v", "error", "-nostdin", "-y", "-i", Path.Combine(diagnostics, "player-timed-fixture.mp4"), "-t", "4", "-map", "0", "-c", "copy", file], TimeSpan.FromSeconds(15), CancellationToken.None);
        using var telemetry = new UiTelemetry(Dispatcher.CurrentDispatcher, run, () => true);
        var player = new ReplayPlayerView(telemetry, run);
        var host = new Window { Content = player, Width = 1040, Height = 689, Left = 560, Top = 80,
            ShowActivated = false, ShowInTaskbar = false, Topmost = true, WindowStyle = WindowStyle.None };
        host.Show();
        var motions = new List<Motion>();
        var slider = (Slider)player.FindName("PositionSlider");
        async Task ObserveAsync(string name, double duration)
        {
            player.TogglePlayback(); await Task.Delay(250);
            var watch = Stopwatch.StartNew(); var ticks = new List<Tick>();
            void Changed(object sender, RoutedPropertyChangedEventArgs<double> args) => ticks.Add(new(watch.Elapsed.TotalSeconds, args.NewValue));
            slider.ValueChanged += Changed;
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(duration));
                DateTime deadline = DateTime.UtcNow.AddMilliseconds(300);
                // Пауза за несколько миллисекунд до EOF не должна обрывать проверку конца.
                // Положение UI уже может достичь края, пока последний кадр ещё показывается.
                if (name == "short-through-end")
                    while (!player.IsAtEnd && DateTime.UtcNow < deadline) await Task.Delay(10);
                while (player.IsAtEnd && slider.Value < slider.Maximum && DateTime.UtcNow < deadline) await Task.Delay(10);
            }
            finally { slider.ValueChanged -= Changed; }
            double elapsed = watch.Elapsed.TotalSeconds;
            player.PausePlayback();
            var steps = ticks.Zip(ticks.Skip(1), (before, after) => new { Gap = after.Wall - before.Wall, Delta = after.Position - before.Position }).ToArray();
            if (steps.Length == 0) throw new InvalidOperationException("Playhead не двигался во время воспроизведения.");
            motions.Add(new(name, ticks.Count / elapsed, steps.Max(step => step.Gap) * 1000,
                steps.Where(step => step.Gap > .001).Max(step => step.Delta / step.Gap), Math.Min(0, steps.Min(step => step.Delta)), ticks.ToArray()));
            double paused = slider.Value; await Task.Delay(200);
            if (Math.Abs(slider.Value - paused) > .002) throw new InvalidOperationException("На паузе playhead должен оставаться на месте.");
        }
        try
        {
            await player.LoadClipAsync(new Clip("Короткий повтор", "", file, "", [null, null, null], 4, 30,
                EmbeddedAudioSources: ["system", "discord", "microphone"]), autoPlay: false);
            DateTime deadline = DateTime.UtcNow.AddSeconds(4);
            while (player.IsLoading && DateTime.UtcNow < deadline) await Task.Delay(20);
            if (player.IsLoading) throw new InvalidOperationException("Короткий повтор не загрузился.");
            await ObserveAsync("short-start", 2);
            player.SeekTo(.5); deadline = DateTime.UtcNow.AddSeconds(4);
            while ((Math.Abs(player.VideoPositionSeconds - .5) > .15 || ((TextBlock)player.FindName("PlayerStateText")).Text != "Пауза") && DateTime.UtcNow < deadline) await Task.Delay(20);
            await ObserveAsync("short-after-seek", 1.5);
            player.SeekTo(.1); deadline = DateTime.UtcNow.AddSeconds(4);
            while ((Math.Abs(player.VideoPositionSeconds - .1) > .15 || ((TextBlock)player.FindName("PlayerStateText")).Text != "Пауза") && DateTime.UtcNow < deadline) await Task.Delay(20);
            await ObserveAsync("short-through-end", 3.7);
            if (!player.IsAtEnd) throw new InvalidOperationException($"Короткий повтор должен дойти до конца: UI={player.PositionSeconds:F3}, video={player.VideoPositionSeconds:F3}, audio={player.AudioPositionSeconds:F3}.");
            if (motions.Any(motion => motion.UpdatesPerSecond < 60 || motion.MaxGapMs > 40 || motion.MaxSpeed > 1.5 || motion.BackwardSeconds < -.002))
                throw new InvalidOperationException("Playhead короткого видео должен обновляться минимум 60 раз/с без скачков и остановок: " + JsonSerializer.Serialize(motions.Select(motion => motion with { Ticks = [] })));
        }
        finally
        {
            File.WriteAllText(Path.Combine(diagnostics, "player-playhead.json"), JsonSerializer.Serialize(new { run, motions, mode = "real visible WPF slider ValueChanged; observer does not advance player clock" }, new JsonSerializerOptions { WriteIndented = true }));
            await player.ClosePlayerAsync(); host.Close();
        }
    }
}
