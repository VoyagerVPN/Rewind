using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using LibVLCSharp.Shared;
using LibVLCSharp.WPF;

namespace Rewind.Preview;

internal static class ColorPlaybackCheck
{
    public static async Task RunAsync(string directory)
    {
        int[][] colors = [[0,0,0], [28,28,28], [64,64,64], [128,128,128], [255,255,255], [200,40,40], [40,200,40], [40,40,200]];
        var results = new List<object>();
        bool nativeColorsCorrect = true;
        foreach (string variant in new[] { "current-frame", "corrected-frame" })
        {
            var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var video = new MediaElement { LoadedBehavior = MediaState.Manual, UnloadedBehavior = MediaState.Manual,
                Stretch = Stretch.Fill, Volume = 0, Source = new Uri(Path.Combine(directory, "nvenc-" + variant + ".mp4")) };
            var window = new Window { WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize,
                ShowInTaskbar = false, ShowActivated = false, Topmost = true, Left = 650, Top = 180,
                Width = 512, Height = 64, Background = Brushes.Black, Content = video };
            video.MediaOpened += (_, _) => ready.TrySetResult();
            video.MediaFailed += (_, args) => ready.TrySetException(args.ErrorException);
            try
            {
                window.Show(); video.Play();
                await ready.Task.WaitAsync(TimeSpan.FromSeconds(10));
                video.Pause(); video.Position = TimeSpan.Zero;
                await Task.Delay(600);
                nint handle = new WindowInteropHelper(window).Handle;
                Native.GetWindowRect(handle, out Native.Rect rect);
                nint dc = GetDC(0);
                try
                {
                    var samples = new List<object>();
                    for (int index = 0; index < colors.Length; index++)
                    {
                        int x = rect.Left + (rect.Right - rect.Left) * (index * 64 + 32) / 512;
                        int y = rect.Top + (rect.Bottom - rect.Top) / 2;
                        uint pixel = GetPixel(dc, x, y);
                        int[] actual = [(int)(pixel & 255), (int)((pixel >> 8) & 255), (int)((pixel >> 16) & 255)];
                        samples.Add(new { expected = colors[index], actual });
                    }
                    results.Add(new { renderer = "WPF", variant, samples });
                }
                finally { ReleaseDC(0, dc); }
            }
            finally { video.Close(); window.Close(); }
        }
        Core.Initialize();
        using var vlc = new LibVLC("--ignore-config", "--no-audio", "--no-video-title-show", "--no-osd", "--no-sub-autodetect-file");
        foreach (string variant in new[] { "current-frame", "corrected-frame" })
        {
            using var player = new LibVLCSharp.Shared.MediaPlayer(vlc);
            using var view = new VideoView { MediaPlayer = player };
            var window = new Window { WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize,
                ShowInTaskbar = false, ShowActivated = false, Topmost = true, Left = 650, Top = 180,
                Width = 512, Height = 64, Background = Brushes.Black, Content = view };
            try
            {
                window.Show();
                using var media = new Media(vlc, new Uri(Path.Combine(directory, "nvenc-" + variant + ".mp4")));
                if (!player.Play(media)) throw new InvalidOperationException("VLC did not start.");
                await Task.Delay(800);
                player.SetPause(true);
                Native.GetWindowRect(new WindowInteropHelper(window).Handle, out Native.Rect rect);
                nint dc = GetDC(0);
                try
                {
                    var samples = new List<object>();
                    for (int index = 0; index < colors.Length; index++)
                    {
                        uint pixel = GetPixel(dc, rect.Left + (rect.Right - rect.Left) * (index * 64 + 32) / 512, rect.Top + (rect.Bottom - rect.Top) / 2);
                        int[] actual = [(int)(pixel & 255), (int)((pixel >> 8) & 255), (int)((pixel >> 16) & 255)];
                        if (Enumerable.Range(0, 3).Any(channel => Math.Abs(actual[channel] - colors[index][channel]) > 3)) nativeColorsCorrect = false;
                        samples.Add(new { expected = colors[index], actual });
                    }
                    results.Add(new { renderer = "LibVLC", variant, samples });
                }
                finally { ReleaseDC(0, dc); }
            }
            finally { await Task.Run(player.Stop); window.Close(); }
        }
        using (var telemetry = new UiTelemetry(System.Windows.Threading.Dispatcher.CurrentDispatcher, directory, () => true))
        {
            var player = new ReplayPlayerView(telemetry, directory);
            var host = new Window { Content = player, Width = 720, Height = 540, WindowStyle = WindowStyle.None, Topmost = true, ShowActivated = false };
            host.Show();
            await player.LoadClipAsync(new Clip("Проверка цвета", "", Path.Combine(directory, "nvenc-corrected-frame.mp4"), "", [null, null, null], 2, 30));
            await Task.Delay(800);
            var surface = (FrameworkElement)player.FindName("VideoSurface");
            nint dc = GetDC(0);
            try
            {
                var samples = new List<object>();
                for (int index = 0; index < colors.Length; index++)
                {
                    Point point = surface.PointToScreen(new Point(surface.ActualWidth * (index * 64 + 32) / 512, surface.ActualHeight / 2));
                    uint pixel = GetPixel(dc, (int)point.X, (int)point.Y);
                    int[] actual = [(int)(pixel & 255), (int)((pixel >> 8) & 255), (int)((pixel >> 16) & 255)];
                    if (Enumerable.Range(0, 3).Any(channel => Math.Abs(actual[channel] - colors[index][channel]) > 3)) nativeColorsCorrect = false;
                    samples.Add(new { expected = colors[index], actual });
                }
                results.Add(new { renderer = "ReplayPlayerView", variant = "corrected-frame", samples });
            }
            finally { ReleaseDC(0, dc); }
            player.PausePlayback();
            int seekSamples = 0, whiteSamples = 0;
            foreach (double position in new[] { .2, 1.2, .4, 1.5 })
            {
                player.SeekTo(position);
                for (int sample = 0; sample < 15; sample++)
                {
                    await Task.Delay(10);
                    Point point = surface.PointToScreen(new Point(surface.ActualWidth * 96 / 512, surface.ActualHeight / 2));
                    nint sampleDc = GetDC(0);
                    uint pixel;
                    try { pixel = GetPixel(sampleDc, (int)point.X, (int)point.Y); }
                    finally { ReleaseDC(0, sampleDc); }
                    seekSamples++;
                    if ((pixel & 255) > 220 && ((pixel >> 8) & 255) > 220 && ((pixel >> 16) & 255) > 220) whiteSamples++;
                }
            }
            results.Add(new { renderer = "ReplayPlayerView", variant = "seek-white-flash-probe", seekSamples, whiteSamples });
            if (whiteSamples != 0) throw new InvalidOperationException("При перемотке тёмный участок видео вспыхивает белым.");
            await player.ClosePlayerAsync().WaitAsync(TimeSpan.FromSeconds(10));
            host.Close();
        }
        await File.WriteAllTextAsync(Path.Combine(directory, "wpf-report.json"), JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));
        if (!nativeColorsCorrect) throw new InvalidOperationException("Native player colors differ from the reference patches.");
    }

    [DllImport("user32.dll")] private static extern nint GetDC(nint window);
    [DllImport("user32.dll")] private static extern int ReleaseDC(nint window, nint dc);
    [DllImport("gdi32.dll")] private static extern uint GetPixel(nint dc, int x, int y);
}
