using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Rewind.Preview;

internal static class PlayerControlsCheck
{
    public static async Task RunAsync(string diagnostics)
    {
        string run = Path.Combine(diagnostics, "player-controls-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(run);
        string original = Path.Combine(diagnostics, "player-timed-fixture.mp4"), file = Path.Combine(run, "selected.mp4"), sibling = Path.Combine(run, "other-screen.mp4");
        File.Copy(original, file); File.Copy(original, sibling);
        string hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(original)));
        using var telemetry = new UiTelemetry(Dispatcher.CurrentDispatcher, run, () => false);
        var player = new ReplayPlayerView(telemetry, run);
        var host = new Window { Content = player, Width = 1040, Height = 689, Left = -20000, Top = -20000,
            ShowActivated = false, ShowInTaskbar = false, WindowStyle = WindowStyle.None };
        host.Show();
        void Render(string name, int width = 1040)
        {
            host.Width = width;
            player.Measure(new Size(width, 689)); player.Arrange(new Rect(0, 0, width, 689)); player.UpdateLayout();
            var bitmap = new RenderTargetBitmap(width, 689, 96, 96, PixelFormats.Pbgra32); bitmap.Render(player);
            var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
            using var output = File.Create(Path.Combine(run, name + ".png")); png.Save(output);
            File.WriteAllText(Path.Combine(run, name + "-layout.json"), JsonSerializer.Serialize(new[]
                { "GoToStartButton", "PreviousFrameButton", "PlayButton", "NextFrameButton", "InlineTracks", "SoundButton" }
                .Select(key =>
                {
                    var control = (FrameworkElement)player.FindName(key);
                    Point origin = control.TranslatePoint(new Point(), player);
                    return new { name = key, origin.X, origin.Y, control.ActualWidth, control.ActualHeight, playerWidth = player.ActualWidth };
                })));
        }
        try
        {
            if (player.FindName("DeleteClipButton") is not Button delete) throw new InvalidOperationException("В плеере нужна кнопка удаления с подтверждением.");
            if (player.FindName("ActionsButton") is not Button || player.FindName("ShowClipInExplorerButton") is not Button)
                throw new InvalidOperationException("Удаление и показ в проводнике должны находиться в меню действий рядом со звуком.");
            var clip = new Clip("Клип", "", file, Path.Combine(diagnostics, "media", "audio", "thumbnail.jpg"), [null, null, null], 24, 30, EmbeddedAudioSources: ["system", "discord", "microphone"]);
            player.ShowLoading(clip); Render("loading");
            if (player.FindName("LoadingSpinner") is not Wpf.Ui.Controls.ProgressRing { IsIndeterminate: true })
                throw new InvalidOperationException("При загрузке нужен вращающийся индикатор без фиктивного прогресса.");
            // Подготовка метаданных ещё не запускала декодер и не должна активировать его watchdog.
            await Task.Delay(180);
            player.PausePlayback();
            if (player.HasPlaybackError || player.VideoProcessId is not null)
                throw new InvalidOperationException("Предварительная загрузка не должна объявлять таймаут и самовольно запускать восстановление декодера.");
            var timer = Stopwatch.StartNew(); await player.LoadClipAsync(clip); double loadMs = timer.Elapsed.TotalMilliseconds;
            DateTime readyDeadline = DateTime.UtcNow.AddSeconds(2);
            while (player.IsLoading && DateTime.UtcNow < readyDeadline) await Task.Delay(40);
            if (player.IsLoading) throw new InvalidOperationException("Загрузка должна перейти в готовое состояние.");
            Task positionedLoad = player.LoadClipAsync(clip, 7000, autoPlay: false);
            if (Math.Abs(((Slider)player.FindName("PositionSlider")).Value - 7) > .001)
                throw new InvalidOperationException("При подготовке выбранного места бегунок должен оставаться на 7 с, а не сбрасываться к нулю.");
            await positionedLoad;
            DateTime positionedDeadline = DateTime.UtcNow.AddSeconds(3);
            while (player.IsLoading && DateTime.UtcNow < positionedDeadline) await Task.Delay(20);
            if (player.FindName("SeekBackwardButton") is not Button seekBackward || player.FindName("SeekForwardButton") is not Button seekForward ||
                player.FindName("SeekStepPicker") is not ComboBox seekStep)
                throw new InvalidOperationException("Нужны отдельные кнопки перемотки в секундах и настройка шага.");
            async Task ExpectSeekAsync(double seconds)
            {
                DateTime deadline = DateTime.UtcNow.AddSeconds(5);
                while ((Math.Abs(player.VideoPositionSeconds - seconds) > .35 || Math.Abs(player.AudioPositionSeconds - seconds) > .35) && DateTime.UtcNow < deadline)
                    await Task.Delay(20);
                if (player.HasPlaybackError || player.IsPlaying || Math.Abs(player.PositionSeconds - seconds) > .35 ||
                    Math.Abs(player.VideoPositionSeconds - seconds) > .35 || Math.Abs(player.AudioPositionSeconds - seconds) > .35)
                    throw new InvalidOperationException($"Перемотка должна сохранять паузу и переходить к {seconds} с: video={player.VideoPositionSeconds}, audio={player.AudioPositionSeconds}.");
            }
            if (!player.HandleShortcut(new HotkeyChord(0, 0x24))) throw new InvalidOperationException("Home должен переходить к началу клипа.");
            await ExpectSeekAsync(0);
            player.SetHotkeys(PlayerHotkeySettings.Default.With(PlayerHotkeyAction.GoToStart, new HotkeyChord(2, 0x24)));
            player.SeekTo(7); await ExpectSeekAsync(7);
            if (player.HandleShortcut(new HotkeyChord(0, 0x24)) || !player.HandleShortcut(new HotkeyChord(2, 0x24)))
                throw new InvalidOperationException("Переназначенный переход к началу должен освобождать Home.");
            await ExpectSeekAsync(0);
            player.SetHotkeys(PlayerHotkeySettings.Default);
            player.SeekTo(7); await ExpectSeekAsync(7);
            seekForward.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await ExpectSeekAsync(12);
            seekBackward.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await ExpectSeekAsync(7);
            seekStep.SelectedItem = seekStep.Items.OfType<ComboBoxItem>().Single(item => item.Tag?.ToString() == "10");
            if (player.FindName("SeekForwardSeconds") is not TextBlock { Text: "10" } || player.FindName("SeekBackwardSeconds") is not TextBlock { Text: "10" })
                throw new InvalidOperationException("Обе кнопки должны показывать выбранный шаг.");
            seekForward.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await ExpectSeekAsync(17);
            if (!player.HandleShortcut(new HotkeyChord(0, 0x25))) throw new InvalidOperationException("Клавиша назад должна работать.");
            await ExpectSeekAsync(7);
            seekBackward.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await ExpectSeekAsync(0);
            if (!player.HandleShortcut(new HotkeyChord(0, 0x27))) throw new InvalidOperationException("Клавиша вперёд должна работать.");
            await ExpectSeekAsync(10);
            seekStep.SelectedItem = seekStep.Items.OfType<ComboBoxItem>().Single(item => item.Tag?.ToString() == "30");
            seekForward.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await ExpectSeekAsync(24 - 1d / 30);
            seekBackward.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await ExpectSeekAsync(0);
            seekStep.SelectedItem = seekStep.Items.OfType<ComboBoxItem>().Single(item => item.Tag?.ToString() == "10");
            player.SeekTo(7); await ExpectSeekAsync(7);
            foreach (ComboBoxItem step in seekStep.Items.OfType<ComboBoxItem>())
            { seekStep.SelectedItem = step; Render("seek-step-" + step.Tag); }
            seekStep.SelectedItem = seekStep.Items.OfType<ComboBoxItem>().Single(item => item.Tag?.ToString() == "10");
            Render("transport");
            player.TogglePlayback();
            seekForward.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            DateTime playingSeekDeadline = DateTime.UtcNow.AddSeconds(5);
            while ((player.VideoPositionSeconds < 16.7 || player.AudioPositionSeconds < 16.7) && DateTime.UtcNow < playingSeekDeadline) await Task.Delay(20);
            if (!player.IsPlaying || player.HasPlaybackError || player.VideoPositionSeconds < 16.7 || player.AudioPositionSeconds < 16.7)
                throw new InvalidOperationException("Перемотка во время просмотра должна продолжать воспроизведение с новой позиции.");
            seekBackward.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            if (player.FindName("PanelTracksToggle") is not System.Windows.Controls.Primitives.ToggleButton trackToggle ||
                player.FindName("InlineTracks") is not FrameworkElement tracks)
                throw new InvalidOperationException("В настройках плеера нужен переключатель горизонтальных дорожек.");
            if (tracks.Visibility != Visibility.Collapsed) throw new InvalidOperationException("По умолчанию дорожки скрыты в меню звука.");
            trackToggle.IsChecked = true;
            player.UpdateLayout();
            var trackControls = new[] { "InlineSystemSlider", "InlineDiscordSlider", "InlineMicrophoneSlider" }
                .Select(name => (Slider)player.FindName(name)).ToArray();
            Point[] trackPositions = trackControls.Select(control => control.TranslatePoint(new Point(), player)).ToArray();
            if (tracks.Visibility != Visibility.Visible || trackControls.Any(control => control.ActualWidth <= 0) ||
                trackPositions.Max(point => point.Y) - trackPositions.Min(point => point.Y) > 1 ||
                trackPositions[0].X >= trackPositions[1].X || trackPositions[1].X >= trackPositions[2].X)
                throw new InvalidOperationException("Три дорожки должны появляться в одну горизонтальную строку.");
            ((Slider)player.FindName("InlineSystemSlider")).Value = 42;
            if (Math.Abs(((Slider)player.FindName("SystemSlider")).Value - 42) > .01)
                throw new InvalidOperationException("Регуляторы в панели и меню должны управлять одной дорожкой.");
            Render("inline-tracks");
            Render("inline-tracks-narrow", 800);
            Render("inline-tracks-wide", 1280);
            Render("inline-tracks");
            ((Button)player.FindName("PlayerSettingsButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Task.Delay(100);
            var settingsPopup = (System.Windows.Controls.Primitives.Popup)player.FindName("PlayerSettingsPopup");
            var settingsContent = (FrameworkElement)settingsPopup.Child;
            var settingsBitmap = new RenderTargetBitmap((int)Math.Ceiling(settingsContent.ActualWidth), (int)Math.Ceiling(settingsContent.ActualHeight), 96, 96, PixelFormats.Pbgra32);
            settingsBitmap.Render(settingsContent);
            var settingsEncoder = new PngBitmapEncoder(); settingsEncoder.Frames.Add(BitmapFrame.Create(settingsBitmap));
            using (var settingsFile = File.Create(Path.Combine(run, "player-settings.png"))) settingsEncoder.Save(settingsFile);
            settingsPopup.IsOpen = false;
            var reopened = new ReplayPlayerView(telemetry, run);
            try
            {
                if (((FrameworkElement)reopened.FindName("InlineTracks")).Visibility != Visibility.Visible) throw new InvalidOperationException("Выбор показа дорожек должен сохраняться после перезапуска.");
                if (((ComboBox)reopened.FindName("SeekStepPicker")).SelectedItem is not ComboBoxItem { Tag: "10" } ||
                    ((TextBlock)reopened.FindName("SeekForwardSeconds")).Text != "10")
                    throw new InvalidOperationException("Шаг перемотки должен сохраняться вместе с настройкой дорожек после перезапуска.");
            }
            finally { await reopened.ClosePlayerAsync(); }
            trackToggle.IsChecked = false;
            var mixer = (System.Windows.Controls.Primitives.Popup)player.FindName("MixerPopup");
            var sound = (Button)player.FindName("SoundButton");
            sound.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            if (!mixer.IsOpen) throw new InvalidOperationException("Первое нажатие должно открывать меню звука.");
            sound.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            if (mixer.IsOpen) throw new InvalidOperationException("Повторное нажатие должно закрывать меню звука.");
            var actions = (System.Windows.Controls.Primitives.Popup)player.FindName("ActionsPopup");
            UIElement menu = actions.Child; actions.Child = null;
            try
            {
                menu.Measure(new Size(246, 200)); menu.Arrange(new Rect(0, 0, 246, menu.DesiredSize.Height)); menu.UpdateLayout();
                var bitmap = new RenderTargetBitmap(246, (int)Math.Ceiling(menu.RenderSize.Height), 96, 96, PixelFormats.Pbgra32); bitmap.Render(menu);
                byte[] background = new byte[4]; bitmap.CopyPixels(new Int32Rect(8, 8, 1, 1), background, 4, 0);
                if (background.Take(3).Any(channel => channel > 60)) throw new InvalidOperationException("Меню не должно использовать системный белый фон.");
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using var output = File.Create(Path.Combine(run, "actions-menu.png")); encoder.Save(output);
            }
            finally { actions.Child = menu; }
            await Task.Delay(1500);
            if (loadMs > 2000 || Directory.EnumerateFiles(run, "*.m4a", SearchOption.AllDirectories).Any()) throw new InvalidOperationException("Открытие не должно ждать полной распаковки аудиодорожек.");
            player.SeekTo(23.5);
            DateTime endDeadline = DateTime.UtcNow.AddSeconds(4);
            while (!player.IsAtEnd && DateTime.UtcNow < endDeadline) await Task.Delay(40);
            if (!player.IsAtEnd || player.IsPlaying) throw new InvalidOperationException("Конец видео должен быть явным остановленным состоянием.");
            player.TogglePlayback();
            DateTime restartDeadline = DateTime.UtcNow.AddSeconds(3);
            while ((player.IsLoading || player.IsAtEnd) && DateTime.UtcNow < restartDeadline) await Task.Delay(40);
            await Task.Delay(200);
            if (!player.IsPlaying || player.IsAtEnd || player.PositionSeconds > 2) throw new InvalidOperationException("Играть после конца должно запускать клип сначала.");
            player.SetHotkeys(PlayerHotkeySettings.Default.With(PlayerHotkeyAction.Delete, new HotkeyChord(2, 0x2e)));
            if (player.HandleShortcut(new HotkeyChord(0, 0x2e)) || !player.HandleShortcut(new HotkeyChord(2, 0x2e)))
                throw new InvalidOperationException("Удаление должно учитывать переназначение и открывать подтверждение.");
            if (!File.Exists(file) || player.HandleShortcut(new HotkeyChord(2, 0x2e)))
                throw new InvalidOperationException("Повторная клавиша удаления не должна подтверждать удаление файла.");
            ((Button)player.FindName("CancelDeleteButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            player.SetHotkeys(PlayerHotkeySettings.Default);
            if (!player.HandleShortcut(new HotkeyChord(0, 0x2e))) throw new InvalidOperationException("Delete должен открывать подтверждение удаления.");
            if (((FrameworkElement)player.FindName("DeleteConfirmation")).Visibility != Visibility.Visible || !File.Exists(file)) throw new InvalidOperationException("Первое нажатие только запрашивает подтверждение.");
            if (((FrameworkElement)player.FindName("Video")).Visibility != Visibility.Visible || player.FindName("DeletePopup") is not System.Windows.Controls.Primitives.Popup { IsOpen: true, StaysOpen: false })
                throw new InvalidOperationException("Подтверждение удаления должно быть закрываемым снаружи попапом и сохранять видеоповерхность видимой.");
            Render("delete-confirmation");
            var deletePopup = (System.Windows.Controls.Primitives.Popup)player.FindName("DeletePopup");
            FrameworkElement popupContent = (FrameworkElement)deletePopup.Child;
            popupContent.Measure(new Size(400, double.PositiveInfinity));
            popupContent.Arrange(new Rect(new Point(), popupContent.DesiredSize)); popupContent.UpdateLayout();
            var popupBitmap = new RenderTargetBitmap(400, (int)Math.Ceiling(popupContent.ActualHeight), 96, 96, PixelFormats.Pbgra32); popupBitmap.Render(popupContent);
            var popupEncoder = new PngBitmapEncoder(); popupEncoder.Frames.Add(BitmapFrame.Create(popupBitmap));
            using (var popupFile = File.Create(Path.Combine(run, "delete-popup.png"))) popupEncoder.Save(popupFile);
            deletePopup.IsOpen = false;
            DateTime dismissedDeadline = DateTime.UtcNow.AddSeconds(1);
            while ((((FrameworkElement)player.FindName("DeleteConfirmation")).Visibility != Visibility.Collapsed || !player.IsPlaying) && DateTime.UtcNow < dismissedDeadline) await Task.Delay(20);
            if (((FrameworkElement)player.FindName("DeleteConfirmation")).Visibility != Visibility.Collapsed || !player.IsPlaying || !File.Exists(file))
                throw new InvalidOperationException("Закрытие попапа снаружи должно сохранять файл и возвращать воспроизведение.");
            delete.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            ((Button)player.FindName("CancelDeleteButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            if (!File.Exists(file) || ((FrameworkElement)player.FindName("DeleteConfirmation")).Visibility != Visibility.Collapsed) throw new InvalidOperationException("Отмена должна сохранять клип.");
            var deleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            player.ClipDeleted += (_, _) => deleted.TrySetResult();
            delete.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            ((Button)player.FindName("ConfirmDeleteButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await deleted.Task.WaitAsync(TimeSpan.FromSeconds(8));
            if (File.Exists(file) || !File.Exists(sibling) || hash != Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(original)))) throw new InvalidOperationException("Подтверждение удаляет только выбранный файл, оставляя остальные экраны и исходники.");
            File.WriteAllText(Path.Combine(diagnostics, "player-controls.json"), JsonSerializer.Serialize(new { run, loadMs, verified = new[] { "indeterminate-loading-ring", "horizontal-three-tracks", "shared-track-gain", "track-setting-reload", "sound-menu-toggle", "configurable-second-seek-buttons-and-hotkeys", "home-and-reassigned-start-seek", "delete-and-reassigned-delete-confirmation-without-repeat-confirmation", "seek-clamps-to-clip-bounds", "seek-preserves-paused-and-playing-state", "seek-step-setting-reload", "load-without-audio-extraction", "ended-state", "restart-after-end", "confirmation", "native-surface-stays-visible", "outside-close-keeps-file-resumes-playback", "cancel-keeps-file", "only-selected-file-deleted", "original-unchanged" } }));
        }
        finally { await player.ClosePlayerAsync(); host.Close(); }
    }
}
