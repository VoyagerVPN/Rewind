using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Rewind.Preview;

internal static class PlayerRiskCheck
{
    public static async Task RunAsync(string diagnostics)
    {
        string run = Path.Combine(diagnostics, "player-risk-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(run);
        using var telemetry = new UiTelemetry(Dispatcher.CurrentDispatcher, run, () => false);
        var player = new ReplayPlayerView(telemetry, run);
        var host = new Window { Content = player, Width = 1040, Height = 689, Left = -20000, Top = -20000, ShowActivated = false, ShowInTaskbar = false, WindowStyle = WindowStyle.None };
        host.Show();
        async Task WaitAsync(Func<bool> ready, string message, int seconds = 10)
        {
            DateTime deadline = DateTime.UtcNow.AddSeconds(seconds);
            while (!ready() && DateTime.UtcNow < deadline) await Task.Delay(25);
            if (!ready()) throw new InvalidOperationException(message);
        }
        void Render(string name)
        {
            player.Measure(new Size(1040, 689)); player.Arrange(new Rect(0, 0, 1040, 689)); player.UpdateLayout();
            var bitmap = new RenderTargetBitmap(1040, 689, 96, 96, PixelFormats.Pbgra32); bitmap.Render(player);
            var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
            using var output = File.Create(Path.Combine(run, name + ".png")); png.Save(output);
            var controls = new[] { "GoToStartButton", "PlayButton", "PreviousFrameButton", "NextFrameButton", "SoundButton", "ActionsButton" }
                .Select(key =>
                {
                    var button = (Button)player.FindName(key);
                    FrameworkElement? icon = button.Content as Wpf.Ui.Controls.SymbolIcon ??
                        (button.Content as Panel)?.Children.OfType<Wpf.Ui.Controls.SymbolIcon>().FirstOrDefault();
                    Point origin = button.TranslatePoint(new Point(), player);
                    return new { name = key, origin.X, origin.Y, button.ActualWidth, button.ActualHeight,
                        centerY = origin.Y + button.ActualHeight / 2, iconCenterY = icon?.TranslatePoint(new Point(icon.ActualWidth / 2, icon.ActualHeight / 2), player).Y };
                });
            File.WriteAllText(Path.Combine(run, name + "-layout.json"), JsonSerializer.Serialize(controls));
        }
        try
        {
            var clip = new Clip("Проверка предупреждения", "", Path.Combine(diagnostics, "player-timed-fixture.mp4"), "", [null, null, null], 24, 30);
            await player.LoadClipAsync(clip, autoPlay: false);
            player.ShowError("В начале просмотра произошёл сбой.");
            if (player.FindName("PlaybackHealthIcon") is not FrameworkElement { Visibility: Visibility.Visible })
                throw new InvalidOperationException("Сразу после обнаружения ошибки нужна метка слева от времени.");
            player.SeekTo(8);
            await WaitAsync(() => !player.IsLoading && !player.HasPlaybackError && player.VideoPositionSeconds >= 7.8, "Рабочая точка должна открыться.");
            player.PausePlayback(); await Task.Delay(100);
            if (player.FindName("RiskRibbon") is not FrameworkElement ribbon || ribbon.Visibility != Visibility.Visible)
                throw new InvalidOperationException("Плеер должен показывать ленту проблемных участков по сохранённым сведениям.");
            player.SeekTo(15);
            await WaitAsync(() => Math.Abs(player.VideoPositionSeconds - 15) < .3, "Нужно перейти в рабочую середину записи.");
            player.PausePlayback();
            var slider = (Slider)player.FindName("PositionSlider");
            if (!slider.CaptureMouse()) throw new InvalidOperationException("Не удалось начать жест перемотки в тестовом плеере.");
            player.SeekTo(2); player.SeekTo(1);
            if (((FrameworkElement)player.FindName("RiskConfirmation")).Visibility == Visibility.Visible || Math.Abs(player.PositionSeconds - 8) > .1 || Math.Abs(slider.Value - 8) > .1)
                throw new InvalidOperationException($"Один жест из рабочей части должен упираться в доступное начало без предупреждения и ухода бегунка в повреждение: position={player.PositionSeconds}, slider={slider.Value}, video={player.VideoPositionSeconds}, captured={slider.IsMouseCaptured}, warning={((FrameworkElement)player.FindName("RiskConfirmation")).Visibility}.");
            slider.ReleaseMouseCapture();
            await WaitAsync(() => Math.Abs(player.VideoPositionSeconds - 8) < .3, "Плеер должен остановить перемотку на доступной границе.");
            ((Button)player.FindName("PreviousFrameButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            if (((FrameworkElement)player.FindName("RiskConfirmation")).Visibility == Visibility.Visible || Math.Abs(player.PositionSeconds - 8) > .1)
                throw new InvalidOperationException("Шаг назад на доступном начале должен оставаться на границе без подтверждения.");
            slider.Value = 2;
            if (((FrameworkElement)player.FindName("RiskConfirmation")).Visibility == Visibility.Visible || Math.Abs(player.PositionSeconds - 8) > .1 || Math.Abs(slider.Value - 8) > .1)
                throw new InvalidOperationException("Изменение шкалы без нового жеста мыши должно упираться в доступное начало.");
            player.SeekTo(15);
            await WaitAsync(() => Math.Abs(player.VideoPositionSeconds - 15) < .3, "Нужно подготовить проверку кнопки начала.");
            ((Button)player.FindName("GoToStartButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitAsync(() => Math.Abs(player.VideoPositionSeconds - 8) < .3, "Кнопка начала должна возвращать к доступному фрагменту.");
            ((Button)player.FindName("SeekBackwardButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            if (((FrameworkElement)player.FindName("RiskConfirmation")).Visibility == Visibility.Visible || Math.Abs(player.PositionSeconds - 8) > .1)
                throw new InvalidOperationException("Перемотка назад в секундах должна упираться в доступное начало без открытия повреждения.");
            int? originalWorker = player.VideoProcessId;
            double before = player.PositionSeconds;
            player.SeekTo(2);
            if (player.FindName("RiskConfirmation") is not FrameworkElement confirmation || confirmation.Visibility != Visibility.Visible || player.VideoProcessId != originalWorker)
                throw new InvalidOperationException("Переход в проблемный участок должен ждать подтверждения.");
            Render("risk-confirmation");
            foreach (string name in new[] { "CancelRiskButton", "ConfirmRiskButton", "GoToSafePartButton", "CancelDeleteButton", "ConfirmDeleteButton" })
            {
                var button = (Button)player.FindName(name);
                if (button.Content is not Panel content || !content.Children.OfType<Wpf.Ui.Controls.SymbolIcon>().Any() ||
                    !content.Children.OfType<TextBlock>().Any(label => !string.IsNullOrWhiteSpace(label.Text)))
                    throw new InvalidOperationException("В подтверждении у каждого действия нужны иконка и подпись: " + name);
            }
            ((Button)player.FindName("CancelRiskButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            if (confirmation.Visibility != Visibility.Collapsed || Math.Abs(player.PositionSeconds - before) > .2)
                throw new InvalidOperationException("Отмена должна сохранять прежнюю позицию.");
            player.SeekTo(2);
            ((Button)player.FindName("ConfirmRiskButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitAsync(() => !player.IsLoading && !player.HasPlaybackError && player.VideoProcessId is int id && id != originalWorker && player.VideoPositionSeconds is >= 1.8 and < 5,
                "Подтверждение должно действительно открывать выбранное место в новом видеопроцессе.");
            int crashedWorker = player.VideoProcessId!.Value;
            using (Process worker = Process.GetProcessById(crashedWorker)) { worker.Kill(entireProcessTree: true); await worker.WaitForExitAsync(); }
            await WaitAsync(() => !player.IsLoading && !player.HasPlaybackError && player.VideoProcessId is int id && id != crashedWorker && player.VideoPositionSeconds >= 7.8,
                "После аварии плеер должен восстановиться на известной рабочей точке.");
            await WaitAsync(() => ((FrameworkElement)player.FindName("RecoveryNotice")).Visibility == Visibility.Visible,
                "Автоматическое восстановление должно быть видно в интерфейсе.");
            Render("recovered");
            if (player.IsPlaying) throw new InvalidOperationException("После восстановления пауза должна сохраняться.");
            int restoredWorker = player.VideoProcessId!.Value;
            using (Process worker = Process.GetProcessById(restoredWorker)) { worker.Kill(entireProcessTree: true); await worker.WaitForExitAsync(); }
            await WaitAsync(() => player.HasPlaybackError && ((Button)player.FindName("RestartPlayerButton")).IsEnabled,
                "Повторный сбой должен оставлять ручной перезапуск без бесконечных попыток.");
            await Task.Delay(600);
            if (!player.HasPlaybackError || ((FrameworkElement)player.FindName("RecoveryNotice")).Visibility == Visibility.Visible)
                throw new InvalidOperationException("При повторном сбое нельзя показывать успешное восстановление.");
            ((Button)player.FindName("RestartPlayerButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitAsync(() => !player.IsLoading && !player.HasPlaybackError && player.VideoProcessId is int id && id != restoredWorker && player.VideoPositionSeconds >= 7.8,
                "Ручной перезапуск должен сохранять ранее найденную рабочую точку.");
            string healthyFile = Path.Combine(run, "healthy-seek.mp4"); File.Copy(clip.Video, healthyFile);
            await player.LoadClipAsync(clip with { Video = healthyFile }, autoPlay: false);
            await WaitAsync(() => !player.IsLoading, "Исправная запись должна закончить загрузку до проверки таймаута перехода.");
            using (Process stalled = Process.GetProcessById(player.VideoProcessId!.Value))
            {
                int stalledId = stalled.Id;
                if (NtSuspendProcess(stalled.Handle) != 0) throw new InvalidOperationException("Не удалось приостановить собственный тестовый декодер.");
                try
                {
                    player.SeekTo(18);
                    await WaitAsync(() => !player.IsLoading && !player.HasPlaybackError && player.VideoProcessId is int id && id != stalledId,
                        "Таймаут перехода должен восстанавливать плеер.", seconds: 14);
                }
                finally { if (!stalled.HasExited) NtResumeProcess(stalled.Handle); }
            }
            if (((FrameworkElement)player.FindName("PlaybackHealthIcon")).Visibility == Visibility.Visible)
                throw new InvalidOperationException("Таймаут плеера не должен объявлять исправную запись повреждённой.");
            player.SeekTo(18);
            if (((FrameworkElement)player.FindName("RiskConfirmation")).Visibility != Visibility.Collapsed)
                throw new InvalidOperationException("После таймаута исправный участок не должен требовать подтверждения повреждения.");
            await WaitAsync(() => Math.Abs(player.VideoPositionSeconds - 18) < .3, "Исправный участок должен открыться после восстановления.");
            await File.WriteAllTextAsync(Path.Combine(diagnostics, "player-risk.json"), JsonSerializer.Serialize(new { run, ribbon = "PASS", confirmation = "PASS", buttonIconsAndLabels = "PASS", availableStartBoundary = "PASS", oneGestureNoWarning = "PASS", deliberateRetryWarning = "PASS", previousFrameAtStart = "PASS", goToAvailableStart = "PASS", cancel = "PASS", explicitOpen = "PASS", automaticRecovery = "PASS", boundedRetry = "PASS", manualRestart = "PASS" }));
        }
        finally { await player.ClosePlayerAsync(); host.Close(); }
    }
    [DllImport("ntdll.dll")] private static extern int NtSuspendProcess(nint process);
    [DllImport("ntdll.dll")] private static extern int NtResumeProcess(nint process);
}
