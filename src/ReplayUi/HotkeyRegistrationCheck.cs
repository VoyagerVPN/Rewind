using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Rewind.Preview;

internal static class HotkeyRegistrationCheck
{
    public static void Run(string diagnostics)
    {
        SynchronizationContext? previousContext = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        string folder = Path.Combine(diagnostics, "hotkey-native-" + Guid.NewGuid().ToString("N"));
        var file = new HotkeySettingsFile(Path.Combine(folder, "hotkeys.json"));
        var original = new HotkeySettings(new HotkeyChord(3, 0x83), new HotkeyChord(3, 0x84), null);
        file.Save(original);
        CaptureMonitor monitor = MonitorCatalog.Read().First(source => source.Primary);
        new CaptureSettingsFile(Path.Combine(folder, "capture-settings.json")).Save(new CaptureSettings(
            monitor.DeviceName, 30, 4, 1, Path.Combine(folder, "clips"), monitor.Identity, [1, 4],
            CaptureSystemAudio: false, CaptureMicrophone: false, SeparateDiscord: false));
        var window = new OverlayWindow([], folder);
        try
        {
            _ = new WindowInteropHelper(window).EnsureHandle();
            CheckDurationOverlay(monitor, diagnostics);
            if (window.FindName("CaptureToggleHotkey") is not HotkeyEditor captureEditor || captureEditor.Chord?.Label != "S + F10")
                throw new InvalidOperationException("У переключения записи должно быть отдельное настраиваемое сочетание S + F10 без Shift.");
            var captureReplacement = new HotkeyChord(3, 0x87);
            captureEditor.Chord = captureReplacement;
            if (file.Load().CaptureToggle != captureReplacement || CanRegisterElsewhere(captureReplacement))
                throw new InvalidOperationException("Переключение записи должно поддерживать переназначение через реальную регистрацию Windows.");
            captureEditor.Chord = null;
            if (file.Load().CaptureToggle is not null || !CanRegisterElsewhere(captureReplacement))
                throw new InvalidOperationException("Отключение сочетания записи должно сохраняться и освобождать регистрацию.");
            captureEditor.Chord = HotkeySettings.Default.CaptureToggle;
            var toggle = (Button)window.FindName("CaptureToggle");
            nint handle = new WindowInteropHelper(window).Handle;
            if (!PostMessage(handle, 0x0312, (nint)HotkeyAction.CaptureToggle, 0))
                throw new InvalidOperationException("Не удалось доставить команду переключения записи.");
            PumpUntil(() => toggle.IsEnabled && ((TextBlock)window.FindName("CaptureToggleText")).Text == "Остановить");
            PumpUntil(() => FindToast("Запись включена") is not null);
            RenderToast("Запись включена", "toast-recording.png", diagnostics);
            PumpUntil(() => ((Button)window.FindName("SaveReplayButton")).IsEnabled);
            ((Button)window.FindName("SaveAllScreensButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            if (FindToast("Сохранение повтора…") is null) throw new InvalidOperationException("Операция сохранения должна сразу показывать экранный тост ожидания.");
            RenderToast("Сохранение повтора…", "toast-saving.png", diagnostics);
            PumpUntil(() => FindToast("Повтор сохранён") is not null);
            RenderToast("Повтор сохранён", "toast-saved.png", diagnostics);
            string savedFile = Directory.GetFiles(Path.Combine(folder, "clips", "Clips"), "*.mp4", SearchOption.AllDirectories).Single();
            if (!File.Exists(savedFile) || FindToast("Сохранение повтора…") is not null)
                throw new InvalidOperationException("Успех должен заменять ожидание только после появления MP4.");
            if (!PostMessage(handle, 0x0312, (nint)HotkeyAction.CaptureToggle, 0))
                throw new InvalidOperationException("Не удалось доставить команду остановки записи.");
            PumpUntil(() => toggle.IsEnabled && ((TextBlock)window.FindName("CaptureToggleText")).Text == "Включить");
            PumpUntil(() => FindToast("Запись выключена") is not null);
            RenderToast("Запись выключена", "toast-stopped.png", diagnostics);
            if (FindToast("Запись включена") is not null)
                throw new InvalidOperationException("Остановка должна заменить устаревший тост включения.");
            PumpUntil(() => FindToast("Запись выключена") is null);
            if (!PostMessage(handle, 0x0312, (nint)HotkeyAction.Save, 0)) throw new InvalidOperationException("Не доставлена команда сохранения выключенного повтора.");
            PumpUntil(() => FindToast("Повтор не сохранён") is not null);
            RenderToast("Повтор не сохранён", "toast-error.png", diagnostics);
            if (((Button)FindToast("Повтор не сохранён")!.FindName("ToastAction")).ToolTip is not null || !File.Exists(savedFile))
                throw new InvalidOperationException("Ошибка сохранения не должна иметь цель файла или удалять предыдущий повтор.");
            if (!window.HandlePanelShortcut(new HotkeyChord(2, 0xbc)) || ((FrameworkElement)window.FindName("SettingsView")).Visibility != Visibility.Visible)
                throw new InvalidOperationException("Ctrl + запятая должен открывать настройки панели.");
            if (!window.HandlePanelShortcut(new HotkeyChord(2, 0xbc)) || ((FrameworkElement)window.FindName("SettingsView")).Visibility != Visibility.Visible)
                throw new InvalidOperationException("Повторное сочетание открытия настроек не должно закрывать их.");
            var settingsEditor = (HotkeyEditor)window.FindName("SettingsHotkey");
            settingsEditor.Chord = new HotkeyChord(6, 0xbc);
            if (window.HandlePanelShortcut(new HotkeyChord(2, 0xbc)) || !window.HandlePanelShortcut(new HotkeyChord(6, 0xbc)) || file.Load().Settings != settingsEditor.Chord)
                throw new InvalidOperationException("Сочетание настроек должно переназначаться и сохраняться.");
            settingsEditor.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            if (window.HandlePanelShortcut(new HotkeyChord(6, 0xbc))) throw new InvalidOperationException("Ввод сочетания не должен выполнять команду настроек.");
            settingsEditor.CancelCapture();
            settingsEditor.Chord = HotkeySettings.Default.Settings;
            if (CanRegisterElsewhere(original.Panel!)) throw new InvalidOperationException("Исходное сочетание не зарегистрировано в Windows.");
            var editor = (HotkeyEditor)window.FindName("PanelHotkey");
            var replacement = new HotkeyChord(3, 0x85);
            editor.Chord = replacement;
            if (file.Load().Panel != replacement || !CanRegisterElsewhere(original.Panel!) || CanRegisterElsewhere(replacement))
                throw new InvalidOperationException("Переназначение должно освободить старое и зарегистрировать новое сочетание.");

            using var acquired = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            var blocked = new HotkeyChord(3, 0x86);
            Task blocker = Task.Run(() =>
            {
                if (!Native.RegisterHotKey(0, 88, blocked.Modifiers | 0x4000, blocked.Key))
                    throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
                acquired.Set();
                try { release.Wait(); } finally { Native.UnregisterHotKey(0, 88); }
            });
            try
            {
                if (!acquired.Wait(TimeSpan.FromSeconds(5)))
                { if (blocker.IsFaulted) blocker.GetAwaiter().GetResult(); throw new TimeoutException("Не получен тестовый резерв сочетания."); }
                editor.Chord = blocked;
                if (file.Load().Panel != replacement || CanRegisterElsewhere(replacement) || editor.Chord != replacement ||
                    !((TextBlock)window.FindName("PanelHotkeyStatus")).Text.Contains("1409", StringComparison.Ordinal))
                    throw new InvalidOperationException("Windows-конфликт должен сохранять старое сочетание и показывать точный код.");
            }
            finally { release.Set(); blocker.GetAwaiter().GetResult(); }

            string temporary = Path.Combine(folder, "hotkeys.json.new");
            Directory.CreateDirectory(temporary);
            try
            {
                editor.Chord = original.Panel;
                if (file.Load().Panel != replacement || CanRegisterElsewhere(replacement) || !CanRegisterElsewhere(original.Panel!))
                    throw new InvalidOperationException("Сбой записи настроек должен отменить новую регистрацию и сохранить прежнюю.");
            }
            finally { Directory.Delete(temporary); }

            editor.Chord = null;
            if (file.Load().Panel is not null || !CanRegisterElsewhere(replacement))
                throw new InvalidOperationException("Отключение должно освободить сочетание.");
            editor.Chord = original.Panel;
            editor.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            if (!CanRegisterElsewhere(original.Panel!)) throw new InvalidOperationException("Во время ввода глобальные клавиши не должны перехватывать выбранное сочетание.");
            editor.CancelCapture();
            if (CanRegisterElsewhere(original.Panel!)) throw new InvalidOperationException("Отмена ввода должна восстановить глобальную регистрацию.");
            File.WriteAllText(Path.Combine(diagnostics, "hotkeys-check.json"), JsonSerializer.Serialize(new
            { completedUtc = DateTime.UtcNow, verified = "Real Windows registration, capture-toggle reassign and disable, held-key hook installation, HWND command starts and stops native capture, panel settings open and reassign, ERROR_HOTKEY_ALREADY_REGISTERED 1409, rollback on save failure, disabling, capture suspension and cancel",
                durationOverlay = "PASS lower-center placement, no activation or click interception, duration labels, hollow unselected points, 1:30 to 0:30, marker movement and cancel hint",
                toasts = "PASS start after first encoded frames, stop, persistent saving replaced by saved MP4, error without a file, non-activating HWND, automatic expiry, monitor work-area bounds",
                toastRenders = new[] { "toast-recording.png", "toast-saving.png", "toast-saved.png", "toast-stopped.png", "toast-error.png" },
                limitations = "Held S + F10 matching is tested with controlled key events; physical keyboard delivery and games are not verified.", folder }));
        }
        finally { window.Close(); SynchronizationContext.SetSynchronizationContext(previousContext); }
        if (!CanRegisterElsewhere(original.Panel!) || !CanRegisterElsewhere(original.Save!))
            throw new InvalidOperationException("Закрытие окна должно освободить его сочетания.");
    }

    private static void PumpUntil(Func<bool> ready)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(15);
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(20) };
        timer.Tick += (_, _) => { if (ready() || DateTime.UtcNow >= deadline) frame.Continue = false; };
        timer.Start();
        try { Dispatcher.PushFrame(frame); }
        finally { timer.Stop(); }
        if (!ready()) throw new TimeoutException("Команда горячей клавиши не переключила фактическое состояние записи.");
    }

    private static Window? FindToast(string title) => Application.Current.Windows.OfType<Window>()
        .FirstOrDefault(candidate => candidate.IsVisible && candidate.FindName("ToastTitle") is TextBlock label && label.Text == title);

    private static void CheckDurationOverlay(CaptureMonitor monitor, string diagnostics)
    {
        var selection = new ReplayDurationSelection(new ReplayIntervals(600, [30, 90, 300, 600], 90));
        var popup = new ReplayDurationWindow(selection);
        nint foreground = Native.GetForegroundWindow();
        try
        {
            popup.ShowOnMonitor((nint)monitor.Handle);
            var title = (TextBlock)popup.FindName("SelectedDuration");
            var shortcuts = (TextBlock)popup.FindName("DurationShortcuts");
            var timeline = (Canvas)popup.FindName("Timeline");
            nint handle = new WindowInteropHelper(popup).Handle;
            var info = new Native.MonitorInfo { Size = Marshal.SizeOf<Native.MonitorInfo>() };
            long style = GetWindowLongPtr(handle, -20).ToInt64();
            if (title.Text != "1:30" || foreground != Native.GetForegroundWindow() ||
                (style & 0x08000020) != 0x08000020 || !Native.GetWindowRect(handle, out Native.Rect bounds) ||
                !Native.GetMonitorInfo((nint)monitor.Handle, ref info) ||
                Math.Abs(bounds.Left + bounds.Right - info.Work.Left - info.Work.Right) > 2 ||
                info.Work.Bottom - bounds.Bottom is < 10 or > 120)
                throw new InvalidOperationException("Шкала должна показывать отметки по центру снизу, не перехватывая фокус и клики.");
            if (!shortcuts.Text.Contains("C отменить", StringComparison.Ordinal) ||
                !timeline.Children.OfType<TextBlock>().Select(label => label.Text).SequenceEqual(["0:30", "1:30", "5:00", "10:00"]) ||
                timeline.Children.OfType<System.Windows.Shapes.Ellipse>().Count(point => point.Stroke is not null &&
                    ReferenceEquals(point.Fill, popup.FindResource("Raised"))) != 4)
                throw new InvalidOperationException("Каждая отметка должна иметь подпись и полый контур под подвижным маркером.");
            var marker = (TranslateTransform)((FrameworkElement)timeline.Children[^1]).RenderTransform;
            double originalPosition = marker.X;
            selection.Step(-1);
            popup.UpdateSelection();
            double firstLabelCenter = Canvas.GetLeft(timeline.Children.OfType<TextBlock>().First()) +
                timeline.Children.OfType<TextBlock>().First().Width / 2;
            PumpUntil(() => marker.X < originalPosition && Math.Abs(marker.X - firstLabelCenter) < .01);
            if (title.Text != "0:30") throw new InvalidOperationException("Шкала должна показывать выбранные 30 секунд.");
            RenderWindow(popup, "replay-duration.png", diagnostics);
        }
        finally { popup.Close(); }
    }

    private static void RenderToast(string title, string name, string diagnostics)
    {
        Window toast = FindToast(title) ?? throw new InvalidOperationException("Не найден видимый тост: " + title);
        toast.UpdateLayout();
        nint handle = new WindowInteropHelper(toast).Handle;
        if (toast.ShowActivated || (GetWindowLongPtr(handle, -20).ToInt64() & 0x08000000) == 0)
            throw new InvalidOperationException("Тост должен иметь неактивируемое окно.");
        var info = new Native.MonitorInfo { Size = Marshal.SizeOf<Native.MonitorInfo>() };
        if (!Native.GetWindowRect(handle, out Native.Rect rect) || !Native.GetMonitorInfo(Native.MonitorFromWindow(handle, 2), ref info) ||
            rect.Left < info.Work.Left || rect.Right > info.Work.Right || rect.Top < info.Work.Top || rect.Bottom > info.Work.Bottom)
            throw new InvalidOperationException("Тост должен помещаться в рабочую область экрана.");
        RenderWindow(toast, name, diagnostics);
    }

    private static void RenderWindow(Window window, string name, string diagnostics)
    {
        var content = (FrameworkElement)window.Content;
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(content.ActualWidth), (int)Math.Ceiling(content.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(content);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = File.Create(Path.Combine(diagnostics, name)); encoder.Save(output);
    }

    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool PostMessage(nint window, int message, nint wParam, nint lParam);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern nint GetWindowLongPtr(nint window, int index);

    private static bool CanRegisterElsewhere(HotkeyChord chord) => Task.Run(() =>
    {
        bool registered = Native.RegisterHotKey(0, 89, chord.Modifiers | 0x4000, chord.Key);
        if (registered) Native.UnregisterHotKey(0, 89);
        else if (Marshal.GetLastWin32Error() != 1409) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        return registered;
    }).GetAwaiter().GetResult();
}
