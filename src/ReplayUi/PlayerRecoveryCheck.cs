using System.IO;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Rewind.Preview;

internal static class PlayerRecoveryCheck
{
    public static async Task RunAsync(string file, string diagnostics)
    {
        string run = Path.Combine(diagnostics, "player-recovery-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(run);
        async Task<string> HashAsync()
        {
            await using FileStream input = new(file, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            return Convert.ToHexString(await SHA256.HashDataAsync(input));
        }
        string sourceHash = await HashAsync();
        using var telemetry = new UiTelemetry(Dispatcher.CurrentDispatcher, run, () => false);
        using var index = new ReplayLibraryIndex(Path.Combine(run, "catalog"), telemetry);
        var player = new ReplayPlayerView(telemetry, run);
        var host = new Window { Content = player, Width = 1040, Height = 689, Left = -20000, Top = -20000,
            ShowActivated = false, ShowInTaskbar = false, WindowStyle = WindowStyle.None };
        host.Show();
        try
        {
            Clip clip = await index.PreparePlaybackAsync(new Clip(Path.GetFileName(file), "", file, "", [null, null, null], 0, 30), CancellationToken.None);
            try { await player.LoadClipAsync(clip, autoPlay: false, preferKnownStart: false); } catch (InvalidOperationException) { }
            if (!player.HasPlaybackError || clip.Duration < 65) throw new InvalidOperationException("Проверка требует клип с повреждённым началом и известной длительностью.");
            player.SeekTo(65);
            DateTime deadline = DateTime.UtcNow.AddSeconds(12);
            while ((player.HasPlaybackError || player.IsLoading) && DateTime.UtcNow < deadline) await Task.Delay(50);
            if (player.HasPlaybackError || player.IsLoading || player.PositionSeconds < 64.8)
                throw new InvalidOperationException("Повреждённое начало не должно блокировать переход к рабочей части на 65 с.");
            await Task.Delay(600);
            double manualVideo = player.VideoPositionSeconds, manualAudio = player.AudioPositionSeconds;
            if (manualVideo < 64.8 || manualVideo > 68 || Math.Abs(manualVideo - manualAudio) > .5)
                throw new InvalidOperationException($"Декодер и звук должны действительно перейти на 65 с: video={manualVideo}, audio={manualAudio}.");
            try { await player.LoadClipAsync(clip, autoPlay: false, preferKnownStart: false); } catch (InvalidOperationException) { }
            if (player.FindName("FindAvailablePartButton") is not Button find)
                throw new InvalidOperationException("В сообщении нужна кнопка поиска доступного фрагмента.");
            player.Measure(new Size(1040, 689)); player.Arrange(new Rect(0, 0, 1040, 689)); player.UpdateLayout();
            if (player.FindName("PlaybackErrorPanel") is not FrameworkElement panel || panel.ActualWidth > 450 || player.FindName("PlaybackErrorIcon") is not FrameworkElement)
                throw new InvalidOperationException("Ошибка должна быть компактным блоком с иконкой по центру видео.");
            var surface = (FrameworkElement)player.FindName("VideoSurface"); Point origin = panel.TranslatePoint(new Point(), surface);
            if (Math.Abs(origin.X + panel.ActualWidth / 2 - surface.ActualWidth / 2) > 1 || Math.Abs(origin.Y + panel.ActualHeight / 2 - surface.ActualHeight / 2) > 1)
                throw new InvalidOperationException("Блок ошибки должен быть по центру видеозоны.");
            var bitmap = new RenderTargetBitmap(1040, 689, 96, 96, PixelFormats.Pbgra32); bitmap.Render(player);
            var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
            using (var output = File.Create(Path.Combine(run, "unavailable-part.png"))) png.Save(output);
            find.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            deadline = DateTime.UtcNow.AddSeconds(35);
            while ((player.HasPlaybackError || player.IsLoading) && DateTime.UtcNow < deadline) await Task.Delay(50);
            await Task.Delay(600);
            if (player.HasPlaybackError || player.IsLoading || player.VideoPositionSeconds < 1 || player.VideoPositionSeconds >= clip.Duration)
                throw new InvalidOperationException("Поиск должен открыть доступный участок этого же повтора.");
            double foundVideo = player.VideoPositionSeconds;
            try { await player.LoadClipAsync(clip, autoPlay: false, preferKnownStart: false); } catch (InvalidOperationException) { }
            find.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Task.Delay(60);
            ((Button)player.FindName("CancelRecoveryButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            deadline = DateTime.UtcNow.AddSeconds(3);
            while (!find.IsEnabled && DateTime.UtcNow < deadline) await Task.Delay(25);
            if (!player.HasPlaybackError || !find.IsEnabled || player.IsPlaying) throw new InvalidOperationException("Отмена поиска должна оставлять ручной выбор и повторный поиск доступными.");
            find.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Task.Delay(60);
            var clean = new Clip("Исправный", "", Path.Combine(diagnostics, "player-timed-fixture.mp4"), "", [null, null, null], 24, 30);
            await player.LoadClipAsync(clean, autoPlay: false); await Task.Delay(1500);
            if (player.HasPlaybackError || player.CurrentVideo != clean.Video || player.IsPlaying)
                throw new InvalidOperationException("Смена файла должна отменять старый поиск и сохранять последний выбранный клип.");
            try { await player.LoadClipAsync(clip, autoPlay: false, preferKnownStart: false); } catch (InvalidOperationException) { }
            find.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Task.Delay(60);
            var closeTimer = Stopwatch.StartNew(); await player.ClosePlayerAsync();
            closeTimer.Stop();
            if (closeTimer.Elapsed.TotalSeconds > 3 || player.VideoProcessId is not null)
                throw new InvalidOperationException("Закрытие должно отменять поиск и освобождать видеопроцесс.");
            var reopened = new ReplayPlayerView(telemetry, run); host.Content = reopened; host.UpdateLayout();
            double restoredVideo;
            try
            {
                await reopened.LoadClipAsync(clip, autoPlay: false);
                deadline = DateTime.UtcNow.AddSeconds(8);
                while (reopened.IsLoading && DateTime.UtcNow < deadline) await Task.Delay(50);
                restoredVideo = reopened.VideoPositionSeconds;
                if (reopened.HasPlaybackError || reopened.IsLoading || restoredVideo < 1 || Math.Abs(restoredVideo - foundVideo) > 1.5)
                    throw new InvalidOperationException("После создания нового плеера сохранённая рабочая точка должна открываться без повторного поиска.");
            }
            finally { await reopened.ClosePlayerAsync(); }
            string versioned = Path.Combine(run, "versioned.mp4"); File.Copy(clean.Video, versioned);
            var versionedClip = clean with { Video = versioned };
            var beforeChange = new ReplayPlayerView(telemetry, run); host.Content = beforeChange; host.UpdateLayout();
            try
            {
                await beforeChange.LoadClipAsync(versionedClip, autoPlay: false);
                beforeChange.ShowError("Проверка восстановления после сбоя."); beforeChange.SeekTo(8);
                deadline = DateTime.UtcNow.AddSeconds(8);
                while ((beforeChange.HasPlaybackError || beforeChange.IsLoading) && DateTime.UtcNow < deadline) await Task.Delay(50);
                await Task.Delay(250);
                if (beforeChange.VideoPositionSeconds < 7.8) throw new InvalidOperationException("Проверка смены версии требует сохранённой точки восстановления.");
            }
            finally { await beforeChange.ClosePlayerAsync(); }
            File.SetLastWriteTimeUtc(versioned, File.GetLastWriteTimeUtc(versioned).AddMinutes(1));
            var afterChange = new ReplayPlayerView(telemetry, run); host.Content = afterChange; host.UpdateLayout();
            try
            {
                await afterChange.LoadClipAsync(versionedClip, autoPlay: false);
                if (afterChange.HasPlaybackError || afterChange.VideoPositionSeconds > 1)
                    throw new InvalidOperationException("Изменённый файл не должен использовать старую точку восстановления.");
            }
            finally { await afterChange.ClosePlayerAsync(); }
            if (sourceHash != await HashAsync()) throw new InvalidOperationException("Исходный файл не должен изменяться.");
            await File.WriteAllTextAsync(Path.Combine(diagnostics, "player-recovery.json"), JsonSerializer.Serialize(new { run, file, clip.Duration, sourceHash, manualVideo, manualAudio, manualSeek = "PASS", foundVideo, restoredVideo, persistedRecovery = "PASS", changedFileInvalidatesMetadata = "PASS", search = "PASS", cancel = "PASS", latestSelection = "PASS", centeredMessage = "PASS", closeDuringSearchMs = closeTimer.Elapsed.TotalMilliseconds }));
        }
        finally { await player.ClosePlayerAsync(); host.Close(); }
    }
}
