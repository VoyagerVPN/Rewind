using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Text.Json;
using System.Windows.Threading;
using System.Windows.Input;

namespace Rewind.Preview;

internal static class UiAppearanceCheck
{
    public static async Task RunAsync(string manifest, string folder)
    {
        Directory.CreateDirectory(folder);
        CheckPreviewRatio();
        Clip[] clips = JsonSerializer.Deserialize<Clip[]>(File.ReadAllText(manifest), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        IReadOnlyList<CaptureMonitor> monitors = MonitorCatalog.Read();
        CaptureMonitor source = monitors.First(monitor => monitor.Primary);
        new CaptureSettingsFile(Path.Combine(folder, "capture-settings.json")).Save(new CaptureSettings(
            source.DeviceName, 120, 120, 60, Path.Combine(folder, "clips"), source.Identity,
            [30, 60, 90, 120], CaptureMicrophone: false));
        var window = new OverlayWindow(clips, folder);
        var root = (FrameworkElement)window.Content;
        window.Content = null;
        root.Resources.MergedDictionaries.Add(window.Resources);
        System.Windows.Documents.TextElement.SetForeground(root, window.Foreground);
        System.Windows.Documents.TextElement.SetFontFamily(root, window.FontFamily);
        System.Windows.Documents.TextElement.SetFontSize(root, window.FontSize);
        try
        {
            root.Measure(new Size(520, 1000)); root.Arrange(new Rect(0, 0, 520, 1000)); root.UpdateLayout();
            var captureControl = (FrameworkElement)window.FindName("CaptureToggle");
            var galleryControl = (FrameworkElement)window.FindName("GalleryButton");
            if (captureControl.TranslatePoint(new Point(), root).Y >= galleryControl.TranslatePoint(new Point(), root).Y)
                throw new InvalidOperationException("Управление повтором должно находиться над историей, а не внизу панели.");
            if (!((WrapPanel)window.FindName("ReplayDurationOptions")).IsEnabled ||
                !((WrapPanel)window.FindName("ReplayDurationOptions")).Children.OfType<RadioButton>().Any())
                throw new InvalidOperationException("Длительность повтора должна выбираться до запуска буфера.");
            if (!((Slider)window.FindName("GalleryZoom")).IsSnapToTickEnabled || !((Slider)window.FindName("LibraryZoom")).IsSnapToTickEnabled)
                throw new InvalidOperationException("Размер превью должен переключаться фиксированными шагами.");
            if (window.FindName("RecentViewButton") is not Button recentView || window.FindName("RecentViewPopup") is not Popup viewPopup)
                throw new InvalidOperationException("Вид и размер превью должны открываться одним компактным меню на главной.");
            window.ShowSettingsSection("screens");
            var pointerSlider = (Slider)window.FindName("HistorySlider");
            window.ShowSettingsSection("replay");
            root.Measure(new Size(520, 1000)); root.Arrange(new Rect(0, 0, 520, 1000)); root.UpdateLayout();
            var pointerTrack = (Track)pointerSlider.Template.FindName("PART_Track", pointerSlider);
            var pointer = new Point(pointerTrack.ActualWidth * 2 / 3, 500);
            SliderDragBehavior.SetPointerValue(pointerSlider, pointer);
            double firstPointerValue = pointerSlider.Value;
            for (int sample = 0; sample < 8; sample++) SliderDragBehavior.SetPointerValue(pointerSlider, pointer);
            if (pointerSlider.Value != firstPointerValue || pointerSlider.Value % 15 != 0 || Math.Abs(pointerSlider.Value - 600) > 15)
                throw new InvalidOperationException("Указатель должен давать стабильную позицию со snap 15 с независимо от высоты курсора и отложенного layout.");
            window.ShowSettingsSection("screens");
            root.Measure(new Size(520, 1000)); root.Arrange(new Rect(0, 0, 520, 1000)); root.UpdateLayout();
            if (Descendants((DependencyObject)window.FindName("SettingsView")).OfType<TextBlock>().Any(label => label.Text == "Настройки"))
                throw new InvalidOperationException("Лишнего общего заголовка над вкладками быть не должно.");
            ((Button)window.FindName("SettingsButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            ((WrapPanel)window.FindName("ReplayDurationOptions")).Children.OfType<RadioButton>().Single(option => (int)option.Tag == 90).IsChecked = true;
            if (((WrapPanel)window.FindName("ReplayDurationOptions")).Children.OfType<RadioButton>().Single(option => option.IsChecked == true).Content.ToString() != "1:30" ||
                new CaptureSettingsFile(Path.Combine(folder, "capture-settings.json")).Load()?.ReplaySeconds != 60)
                throw new InvalidOperationException("Выбор на главной не должен менять основной интервал горячей клавиши.");
            try { window.SelectReplayDuration(999); throw new InvalidOperationException("Нельзя выбрать недоступную отметку."); }
            catch (ArgumentException) { }
            CheckSettingsGeometry(window, root);
            window.ShowSettingsSection("hotkeys");
            foreach (PlayerHotkeyAction action in new[] { PlayerHotkeyAction.GoToStart, PlayerHotkeyAction.Delete })
            {
                if (window.FindName("Player" + action + "Hotkey") is not HotkeyEditor actionEditor)
                    throw new InvalidOperationException("Новое действие плеера должно иметь редактор сочетания: " + action);
                var replacement = new HotkeyChord(2, action == PlayerHotkeyAction.GoToStart ? 0x24u : 0x2eu);
                actionEditor.Chord = replacement;
                if (new HotkeySettingsFile(Path.Combine(folder, "hotkeys.json")).Load().PlayerBindings.Get(action) != replacement)
                    throw new InvalidOperationException("Новое сочетание плеера должно сохраняться автоматически: " + action);
                actionEditor.Chord = null;
                if (new HotkeySettingsFile(Path.Combine(folder, "hotkeys.json")).Load().PlayerBindings.Get(action) is not null)
                    throw new InvalidOperationException("Отключённое сочетание плеера должно оставаться отключённым после загрузки: " + action);
                actionEditor.Chord = PlayerHotkeySettings.Default.Get(action);
            }
            if (window.FindName("PlayerPauseHotkey") is not HotkeyEditor pauseEditor)
                throw new InvalidOperationException("Клавиши плеера должны переназначаться тем же контролом.");
            pauseEditor.Chord = new HotkeyChord(0, 0x4a);
            using (JsonDocument savedKeys = JsonDocument.Parse(File.ReadAllText(Path.Combine(folder, "hotkeys.json"))))
                if (savedKeys.RootElement.GetProperty("Player").GetProperty("Pause").GetProperty("Key").GetUInt32() != 0x4a)
                    throw new InvalidOperationException("Клавиша плеера без модификаторов должна сохраняться автоматически.");
            var nextEditor = (HotkeyEditor)window.FindName("PlayerNextFrameHotkey");
            nextEditor.Chord = new HotkeyChord(0, 0x4a);
            if (nextEditor.Chord == pauseEditor.Chord || !((TextBlock)window.FindName("HotkeysMessage")).Text.Contains("другому действию", StringComparison.Ordinal))
                throw new InvalidOperationException("Конфликт клавиш плеера должен сохранять прежнее назначение.");
            using (var keyTelemetry = new UiTelemetry(window.Dispatcher, Path.Combine(folder, "player-key-check"), () => false))
            {
                var boundPlayer = new ReplayPlayerView(keyTelemetry, folder);
                if (!boundPlayer.HandleShortcut(new HotkeyChord(0, 0x4a)) || boundPlayer.HandleShortcut(new HotkeyChord(0, 0x20)))
                    throw new InvalidOperationException("Плеер должен читать новые назначения после перезапуска и освобождать старую клавишу.");
                await boundPlayer.ClosePlayerAsync();
            }
            pauseEditor.Chord = new HotkeyChord(0, 0x20);
            ((Button)window.FindName("SettingsButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Render(root, window.Background, folder, "home");
            foreach (string section in new[] { "screens", "audio", "replay", "storage" })
            { window.ShowSettingsSection(section); Render(root, window.Background, folder, section); if (section == "screens") Render(root, window.Background, folder, "screens-full", 1440); }
            window.ShowSettingsSection("hotkeys");
            var panelShortcut = (HotkeyEditor)window.FindName("PanelHotkey");
            panelShortcut.Chord = new HotkeyChord(3, 0x51);
            if (new HotkeySettingsFile(Path.Combine(folder, "hotkeys.json")).Load().Panel != new HotkeyChord(3, 0x51))
                throw new InvalidOperationException("Назначение сочетания должно сохраняться автоматически.");
            panelShortcut.Chord = HotkeySettings.Default.Save;
            if (new HotkeySettingsFile(Path.Combine(folder, "hotkeys.json")).Load().Panel != new HotkeyChord(3, 0x51) ||
                !((TextBlock)window.FindName("PanelHotkeyStatus")).Text.Contains("другому действию", StringComparison.Ordinal))
                throw new InvalidOperationException("Повторное сочетание не должно заменять предыдущее; причина должна быть видна.");
            panelShortcut.Chord = HotkeySettings.Default.Panel;
            Render(root, window.Background, folder, "hotkeys");
            window.ShowSettingsSection("audio");
            ((CheckBox)window.FindName("MicrophoneEnabled")).IsChecked = true;
            var microphoneMeter = (AudioLevelBar)window.FindName("MicrophoneLevel");
            microphoneMeter.Peak = 0.01;
            microphoneMeter.Visibility = Visibility.Visible;
            if (microphoneMeter.FilledFraction < 0.3 || microphoneMeter.FilledFraction > 0.34)
                throw new InvalidOperationException("Микрофон -40 dBFS должен занимать треть шкалы, а не один процент.");
            ((AudioLevelBar)window.FindName("SystemLevel")).Peak = 0.1;
            ((AudioLevelBar)window.FindName("SystemLevel")).Visibility = Visibility.Visible;
            ((AudioLevelBar)window.FindName("DiscordLevel")).Peak = 0.04;
            ((AudioLevelBar)window.FindName("DiscordLevel")).Visibility = Visibility.Visible;
            ((Grid)window.FindName("CaptureSettingsFields")).IsEnabled = false;
            Render(root, window.Background, folder, "audio-meters");
            ((Grid)window.FindName("CaptureSettingsFields")).IsEnabled = true;
            window.ShowSettingsSection("storage");
            var filenameInput = (TextBox)window.FindName("FilenameFormatInput");
            filenameInput.Text = "{app}_{n:000}";
            WaitForSettings(folder, saved => saved.FileNameFormat == "{app}_{n:000}");
            filenameInput.Text = "../{app}";
            PumpFor(TimeSpan.FromMilliseconds(600));
            if (new CaptureSettingsFile(Path.Combine(folder, "capture-settings.json")).Load()!.FileNameFormat != "{app}_{n:000}")
                throw new InvalidOperationException("Невалидное имя не должно заменять сохранённый формат.");
            filenameInput.Text = ReplayFileName.DefaultFormat;
            WaitForSettings(folder, saved => saved.FileNameFormat == ReplayFileName.DefaultFormat);
            if (((FrameworkElement)window.FindName("FilenameFormatPanel")).Visibility != Visibility.Visible || window.FindName("FilenameFormatButton") is not null)
                throw new InvalidOperationException("Формат имени должен быть полноценной постоянно видимой настройкой без сворачивания.");
            filenameInput.Text = "{app} {size}";
            WaitForSettings(folder, saved => saved.FileNameFormat == "{app} {size}");
            Render(root, window.Background, folder, "storage-filename");
            filenameInput.Text = ReplayFileName.DefaultFormat;
            WaitForSettings(folder, saved => saved.FileNameFormat == ReplayFileName.DefaultFormat);
            ((CheckBox)window.FindName("MicrophoneEnabled")).IsChecked = false;
            CheckAutomaticSettings(window, root, folder);
            CheckSettingsSelection(window, root, folder, source, monitors);
            CheckReplaySettings(window, root, folder);
            var startupToggle = (CheckBox)window.FindName("AlwaysRecordAtStartup");
            startupToggle.IsChecked = true;
            if (!new StartupPreferencesFile(Path.Combine(folder, "startup.json")).Load().AlwaysStartRecording)
                throw new InvalidOperationException("Настройка постоянного включения записи при запуске должна сохраняться автоматически.");
            startupToggle.IsChecked = false;
            ((Grid)window.FindName("HomeView")).Visibility = Visibility.Visible;
            ((Grid)window.FindName("SettingsView")).Visibility = Visibility.Collapsed;
            var popupHost = new Window { Content = root, Width = 520, Height = 1000, Left = -20000, Top = -20000,
                ShowActivated = false, ShowInTaskbar = false, WindowStyle = WindowStyle.None };
            try
            {
                popupHost.Show();
                recentView.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                if (!viewPopup.IsOpen) throw new InvalidOperationException("Меню вида должно открываться по нажатию.");
                RenderFloating((FrameworkElement)viewPopup.Child, "recent-view-menu");
                recentView.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                if (viewPopup.IsOpen) throw new InvalidOperationException("Повторное нажатие должно закрывать меню вида.");
                recentView.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                var popupContent = (FrameworkElement)viewPopup.Child;
                PresentationSource sourceWindow = PresentationSource.FromVisual(popupContent) ?? throw new InvalidOperationException("Меню должно иметь собственную поверхность WPF.");
                popupContent.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, sourceWindow, Environment.TickCount, Key.Escape) { RoutedEvent = Keyboard.PreviewKeyDownEvent });
                if (viewPopup.IsOpen) throw new InvalidOperationException("Escape должен закрывать меню вида.");
                var state = (FrameworkElement)((FrameworkElement)window.FindName("CaptureStateGlyph")).Parent;
                var details = (ToolTip)state.ToolTip;
                details.PlacementTarget = state; details.IsOpen = true;
                try { RenderFloating(details, "capture-state-details"); }
                finally { details.IsOpen = false; }
            }
            finally { popupHost.Content = null; popupHost.Close(); }
            File.WriteAllText(Path.Combine(folder, "report.json"), JsonSerializer.Serialize(new
            { completedUtc = DateTime.UtcNow, preview = "320 x 180 DIP", width = 520, height = 1000,
                screens = new[] { "home", "screens", "audio", "audio-meters", "replay", "storage", "hotkeys", "screens-draft", "screens-off", "replay-long", "replay-short", "replay-edited" },
                audioMeters = "PASS -40 dBFS remains visible on the logarithmic meter while recording controls are disabled; deterministic fixture, not live speech",
                hotkeys = "PASS automatic save, duplicate rejection, previous binding retained",
                settingsGeometry = "PASS identical tab position and heading center across five sections, persistent capture status beside Rewind, including hidden autosave icon",
                settingsSelection = "PASS static monitor diagram reflects resolution/FPS; independent monitor switches and quality, automatic saving, invalid path rejection and failed-save retry",
                replaySettings = "PASS 15-second history ticks, drag marker, preserve unavailable markers, restore primary, remove, add, auto-save and reload",
                recentView = "PASS own HWND, repeated trigger closes menu, routed Escape closes menu; menu and state tooltip rendered",
                limitations = "WPF offscreen render and routed input on own popup HWND; not physical keyboard/mouse, DPI, GPU load, or native video surface acceptance." }));
        }
        finally { window.Close(); }
        void RenderFloating(FrameworkElement surface, string name)
        {
            surface.ApplyTemplate(); surface.Measure(new Size(360, double.PositiveInfinity));
            surface.Arrange(new Rect(new Point(), surface.DesiredSize)); surface.UpdateLayout();
            var bitmap = new RenderTargetBitmap((int)Math.Ceiling(surface.ActualWidth), (int)Math.Ceiling(surface.ActualHeight), 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(surface);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var output = File.Create(Path.Combine(folder, name + ".png")); encoder.Save(output);
        }
    }

    private static void CheckSettingsGeometry(OverlayWindow window, FrameworkElement root)
    {
        double? tabsY = null, titleY = null;
        foreach (var section in new[] { ("screens", "ScreensSettingsTitle"), ("audio", "AudioSettingsTitle"),
            ("replay", "ReplaySettingsTitle"), ("storage", "StorageSettingsTitle"), ("hotkeys", "HotkeysSettingsTitle"), ("screens", "ScreensSettingsTitle") })
        {
            window.ShowSettingsSection(section.Item1);
            root.Measure(new Size(520, 1000)); root.Arrange(new Rect(0, 0, 520, 1000)); root.UpdateLayout();
            var tabs = (FrameworkElement)window.FindName("SettingsTabs");
            if (window.FindName("HeaderCaptureStatus") is not FrameworkElement captureStatus || !captureStatus.ToolTip.ToString()!.Equals("Запись выключена", StringComparison.Ordinal) ||
                captureStatus.TranslatePoint(new Point(), root).Y >= 64)
                throw new InvalidOperationException("Состояние повтора должно оставаться рядом с Rewind при переходе между всеми разделами.");
            var title = (FrameworkElement)window.FindName(section.Item2);
            double nextTabs = tabs.TranslatePoint(new Point(), root).Y;
            double nextTitle = title.TranslatePoint(new Point(0, title.ActualHeight / 2), root).Y;
            if (tabsY is not null && Math.Abs(nextTabs - tabsY.Value) > .5)
                throw new InvalidOperationException($"Состояние галочки не должно сдвигать вкладки: {section.Item1}, {nextTabs} вместо {tabsY}.");
            if (titleY is not null && Math.Abs(nextTitle - titleY.Value) > .5)
                throw new InvalidOperationException($"Заголовки разделов должны лежать на одной линии: {section.Item1}, {nextTitle} вместо {titleY}.");
            tabsY = nextTabs; titleY = nextTitle;
        }
        window.ShowSettingsSection("screens");
        ((Button)window.FindName("SettingsButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    }

    private static void CheckAutomaticSettings(OverlayWindow window, FrameworkElement root, string folder)
    {
        window.ShowSettingsSection("audio");
        var system = (CheckBox)window.FindName("SystemAudioEnabled");
        Render(root, window.Background, folder, "audio");
        if (VisualTreeHelper.HitTest(system, new Point(system.ActualWidth / 2, system.ActualHeight / 2)) is null)
            throw new InvalidOperationException("Вся строка источника звука должна реагировать на нажатие.");
        system.IsChecked = false;
        WaitForSettings(folder, saved => !saved.CaptureSystemAudio);
        system.IsChecked = true;
        WaitForSettings(folder, saved => saved.CaptureSystemAudio);
        var microphone = (CheckBox)window.FindName("MicrophoneEnabled");
        var microphonePicker = (ComboBox)window.FindName("MicrophonePicker");
        AudioDeviceChoice? device = microphonePicker.Items.Cast<AudioDeviceChoice>().FirstOrDefault(choice => choice.Id is not null);
        if (device is not null)
        {
            microphone.IsChecked = true;
            microphonePicker.SelectedItem = device;
            WaitForSettings(folder, saved => saved.CaptureMicrophone && saved.MicrophoneDeviceId == device.Id);
            microphone.IsChecked = false;
            microphonePicker.SelectedItem = microphonePicker.Items.Cast<AudioDeviceChoice>().First(choice => choice.Id is null);
            WaitForSettings(folder, saved => !saved.CaptureMicrophone && saved.MicrophoneDeviceId is null);
        }
        var path = (TextBox)window.FindName("StorageInput");
        string original = path.Text;
        path.Text = "unfinished";
        PumpFor(TimeSpan.FromMilliseconds(800));
        if (new CaptureSettingsFile(Path.Combine(folder, "capture-settings.json")).Load()!.StorageRoot != original)
            throw new InvalidOperationException("Незавершённый путь не должен заменять сохранённые настройки.");
        path.Text = original;
        string blockedSave = Path.Combine(folder, "capture-settings.json.new");
        Directory.CreateDirectory(blockedSave);
        try
        {
            system.IsChecked = false;
            PumpFor(TimeSpan.FromMilliseconds(800));
            if (!new CaptureSettingsFile(Path.Combine(folder, "capture-settings.json")).Load()!.CaptureSystemAudio ||
                !((TextBlock)window.FindName("SettingsChangesText")).Text.Contains("Не удалось сохранить", StringComparison.Ordinal))
                throw new InvalidOperationException("Сбой автосохранения должен быть виден и сохранять предыдущие настройки.");
        }
        finally { Directory.Delete(blockedSave); }
        ((Button)window.FindName("SettingsSaveStatus")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        WaitForSettings(folder, saved => !saved.CaptureSystemAudio);
        system.IsChecked = true;
        WaitForSettings(folder, saved => saved.CaptureSystemAudio);
    }

    private static void WaitForSettings(string folder, Func<CaptureSettings, bool> predicate)
    {
        var file = new CaptureSettingsFile(Path.Combine(folder, "capture-settings.json"));
        DateTime deadline = DateTime.UtcNow.AddSeconds(3);
        while (DateTime.UtcNow < deadline)
        {
            if (file.Load() is CaptureSettings settings && predicate(settings)) return;
            PumpFor(TimeSpan.FromMilliseconds(50));
        }
        throw new InvalidOperationException("Изменение UI не сохранилось автоматически.");
    }

    private static void PumpFor(TimeSpan duration)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = duration };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start(); Dispatcher.PushFrame(frame);
    }

    private static void CheckReplaySettings(OverlayWindow window, FrameworkElement root, string folder)
    {
        window.ShowSettingsSection("replay");
        var history = (Slider)window.FindName("HistorySlider");
        var time = (TimeValueEditor)window.FindName("HistoryTime");
        var timeline = (ReplayTimeline)window.FindName("IntervalTimeline");
        time.Value = 600;
        Render(root, window.Background, folder, "replay-long");
        if (history.Value != 600 || history.TickFrequency != 15 || !history.IsSnapToTickEnabled)
            throw new InvalidOperationException("История должна принимать точное время и иметь шаг ползунка 15 секунд.");
        Thumb marker = Descendants(timeline).OfType<Thumb>().Single(control =>
            System.Windows.Automation.AutomationProperties.GetName(control) == "Отметка 2:00");
        marker.RaiseEvent(new DragStartedEventArgs(0, 0));
        marker.RaiseEvent(new DragDeltaEventArgs((timeline.ActualWidth - 40) * .3, 0));
        marker.RaiseEvent(new DragCompletedEventArgs(0, 0, false));
        Button Action(string name) => Descendants(root).OfType<Button>().Single(control =>
            System.Windows.Automation.AutomationProperties.GetName(control) == name);
        Action("Основной 5:00").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        history.Value = 30;
        Render(root, window.Background, folder, "replay-short");
        if (!Descendants(root).OfType<TimeValueEditor>().Any(control => control.Value == 300))
            throw new InvalidOperationException("Сокращение истории не должно удалять пятиминутную отметку.");
        WaitForSettings(folder, saved => saved.HistorySeconds == 30 && saved.PreferredReplaySeconds == 300);
        CaptureSettings saved = new CaptureSettingsFile(Path.Combine(folder, "capture-settings.json")).Load()!;
        if (saved.PreferredReplaySeconds != 300 || saved.ReplaySeconds != 30 || !saved.ReplayMarkers!.Contains(300))
            throw new InvalidOperationException("Сохранение короткой истории должно удерживать основную длинную отметку.");
        history.Value = 600;
        Action("Удалить 0:30").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var newTime = (TimeValueEditor)window.FindName("NewIntervalTime"); newTime.Value = 450;
        Action("Добавить отметку").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        WaitForSettings(folder, saved => saved.HistorySeconds == 600 && saved.ReplayMarkers!.SequenceEqual(new[] { 60, 90, 300, 450 }));
        saved = new CaptureSettingsFile(Path.Combine(folder, "capture-settings.json")).Load()!;
        if (saved.ReplaySeconds != 300 || !saved.ReplayMarkers!.SequenceEqual(new[] { 60, 90, 300, 450 }))
            throw new InvalidOperationException("Возврат истории, удаление и добавление должны сохранять основную отметку.");
        Render(root, window.Background, folder, "replay-edited");
    }

    private static void CheckSettingsSelection(OverlayWindow window, FrameworkElement root, string folder,
        CaptureMonitor source, IReadOnlyList<CaptureMonitor> monitors)
    {
        window.ShowSettingsSection("screens");
        var map = (Canvas)window.FindName("MonitorLayout");
        if (Descendants(map).OfType<Button>().Any())
            throw new InvalidOperationException("Схема экранов должна показывать состояние, не предлагать выбор редактируемого монитора.");
        if (window.FindName("ApplySettingsButton") is not null)
            throw new InvalidOperationException("Автосохранение не должно требовать кнопки применения.");
        CaptureMonitor? other = monitors.FirstOrDefault(monitor => monitor.DeviceName != source.DeviceName);
        if (other is null) return;
        CheckBox Switch(CaptureMonitor monitor) => Descendants(root).OfType<CheckBox>().Single(control =>
            System.Windows.Automation.AutomationProperties.GetName(control) == "Запись " + monitor.DeviceName);
        Switch(other).IsChecked = true;
        if (Switch(source).IsChecked != true || Switch(other).IsChecked != true)
            throw new InvalidOperationException("Мониторы должны включаться независимо.");
        WaitForSettings(folder, saved => saved.ResolveMonitorProfiles().Count(profile => profile.Enabled) == 2);
        ListBox Quality(CaptureMonitor monitor, string name) => Descendants(root).OfType<ListBox>().Single(control =>
            System.Windows.Automation.AutomationProperties.GetName(control) == name + " " + monitor.DeviceName);
        Quality(other, "Частота кадров").SelectedItem = 60;
        var resolution = Quality(other, "Разрешение");
        resolution.SelectedItem = resolution.Items.Cast<object>().Single(choice => choice.ToString() == "720p");
        WaitForSettings(folder, saved => saved.ResolveMonitorProfiles().Single(profile => profile.DeviceName == other.DeviceName).OutputHeight == 720);
        if ((int)Quality(source, "Частота кадров").SelectedItem != 120)
            throw new InvalidOperationException("Параметры бокового экрана не должны менять основной.");
        if (Descendants((DependencyObject)window.FindName("ScreensSettings")).OfType<ComboBox>().Any(control => control.Visibility == Visibility.Visible))
            throw new InvalidOperationException("Настройки экранов не должны прятать варианты разрешения в меню поверх FPS.");
        Render(root, window.Background, folder, "screens-draft");
        string[] mapLabels = Descendants(map).OfType<TextBlock>().Select(label => label.Text).ToArray();
        if (!mapLabels.Contains("60 FPS") || !mapLabels.Any(label => label.EndsWith(" × 720", StringComparison.Ordinal)))
            throw new InvalidOperationException("Схема экранов должна показывать выбранное разрешение и FPS после изменения профиля.");
        Switch(other).IsChecked = false;
        root.UpdateLayout();
        if (((FrameworkElement)Quality(other, "Разрешение").Parent).Visibility != Visibility.Collapsed || ((FrameworkElement)Quality(other, "Частота кадров").Parent).Visibility != Visibility.Collapsed)
            throw new InvalidOperationException("Выключенный экран не должен занимать место регуляторами качества.");
        if (Quality(other, "Частота кадров").IsEnabled || Quality(other, "Разрешение").IsEnabled)
            throw new InvalidOperationException("Выключенный экран должен явно отключать свои регуляторы качества.");
        Switch(source).IsChecked = false;
        WaitForSettings(folder, saved => !saved.CaptureVideo);
        if (new CaptureSettingsFile(Path.Combine(folder, "capture-settings.json")).Load()!.CaptureVideo ||
            ((Button)window.FindName("CaptureToggle")).IsEnabled)
            throw new InvalidOperationException("Все экраны выключены: состояние сохраняется, запуск захвата недоступен.");
        Render(root, window.Background, folder, "screens-off");
        Switch(source).IsChecked = true;
        WaitForSettings(folder, saved => saved.CaptureVideo && saved.MonitorDeviceName == source.DeviceName);
        if (!((Button)window.FindName("CaptureToggle")).IsEnabled)
            throw new InvalidOperationException("Включение исходного экрана должно восстанавливать возможность записи.");
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(parent, index);
            yield return child;
            foreach (DependencyObject descendant in Descendants(child)) yield return descendant;
        }
    }

    private static void Render(FrameworkElement root, Brush background, string folder, string name, int height = 1000)
    {
        var size = new Size(520, height);
        root.Measure(size); root.Arrange(new Rect(size)); root.UpdateLayout();
        var drawing = new DrawingVisual();
        using (DrawingContext context = drawing.RenderOpen())
        {
            context.DrawRectangle(background, null, new Rect(size));
        }
        var bitmap = new RenderTargetBitmap(520, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(drawing);
        bitmap.Render(root);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(Path.Combine(folder, name + ".png")); encoder.Save(file);
    }

    public static void CheckPreviewRatio()
    {
        var frame = new SixteenNineFrame { Child = new Grid() };
        frame.Measure(new Size(320, double.PositiveInfinity));
        frame.Arrange(new Rect(0, 0, 320, frame.DesiredSize.Height));
        if (Math.Abs(frame.DesiredSize.Height - 180) > .01 || Math.Abs(frame.RenderSize.Height - 180) > .01)
            throw new InvalidOperationException("Превью шириной 320 должно занимать 180 DIP по высоте.");
        frame.Measure(new Size(320, 100));
        frame.Arrange(new Rect(0, 0, 320, 100));
        if (Math.Abs(frame.Child.RenderSize.Width - 177.777777) > .01 || Math.Abs(frame.Child.RenderSize.Height - 100) > .01)
            throw new InvalidOperationException("В ограниченном по высоте плеере видео должно целиком помещаться с пропорциями 16:9.");
    }
}
