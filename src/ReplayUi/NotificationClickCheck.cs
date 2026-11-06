using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Rewind.Preview;

internal static class NotificationClickCheck
{
    public static void CheckPlacement(string diagnostics)
    {
        CaptureMonitor[] monitors = MonitorCatalog.Read().ToArray();
        CaptureMonitor primary = monitors.Single(monitor => monitor.Primary);
        CaptureMonitor secondary = monitors.First(monitor => !monitor.Primary);
        string folder = Path.Combine(diagnostics, "notification-placement-" + Guid.NewGuid().ToString("N"));
        var overlay = new OverlayWindow([], folder);
        nint[] Targets(string key) => Application.Current.Windows.OfType<ActionToastWindow>()
            .Where(window => window.IsVisible && window.NoticeKey == key)
            .Select(window => Native.MonitorFromWindow(new System.Windows.Interop.WindowInteropHelper(window).Handle, 2))
            .Order().ToArray();
        try
        {
            overlay.ShowActionToast("ordinary", "Состояние записи", "", Wpf.Ui.Controls.SymbolRegular.Video24);
            if (!Targets("ordinary").SequenceEqual([(nint)primary.Handle]))
                throw new InvalidOperationException("Обычное уведомление должно появиться только на основном мониторе.");
            overlay.ShowActionToast("save", "Сохранение повтора…", "", Wpf.Ui.Controls.SymbolRegular.Save24, persistent: true);
            if (!Targets("save").SequenceEqual([(nint)primary.Handle]))
                throw new InvalidOperationException("Подготовка сохранения должна отображаться только на основном мониторе.");
            nint[] recorded = [(nint)primary.Handle, (nint)secondary.Handle];
            overlay.ShowActionToast("save", "Повтор сохранён", "", Wpf.Ui.Controls.SymbolRegular.CheckmarkCircle24, recordingMonitors: recorded);
            if (!Targets("save").SequenceEqual(recorded.Order()))
                throw new InvalidOperationException("Подтверждение должно заменить подготовку и появиться на каждом записываемом мониторе.");
            overlay.ShowActionToast("single", "Повтор сохранён", "", Wpf.Ui.Controls.SymbolRegular.CheckmarkCircle24,
                recordingMonitors: [(nint)secondary.Handle]);
            if (!Targets("single").SequenceEqual([(nint)primary.Handle]))
                throw new InvalidOperationException("При записи одного экрана подтверждение остаётся на основном мониторе.");
            nint[] secondaryOnly = monitors.Where(monitor => !monitor.Primary).Select(monitor => (nint)monitor.Handle).ToArray();
            if (secondaryOnly.Length > 1)
            {
                overlay.ShowActionToast("other-screens", "Сохранение повтора…", "", Wpf.Ui.Controls.SymbolRegular.Save24, persistent: true);
                overlay.ShowActionToast("other-screens", "Повтор сохранён", "", Wpf.Ui.Controls.SymbolRegular.CheckmarkCircle24, recordingMonitors: secondaryOnly);
                if (!Targets("other-screens").SequenceEqual(secondaryOnly.Order()))
                    throw new InvalidOperationException("Если основной экран не записывается, подтверждение должно закрыть подготовку на нём и появиться на записываемых экранах.");
            }
        }
        finally { overlay.Close(); }
        File.WriteAllText(Path.Combine(diagnostics, "notification-placement.json"), JsonSerializer.Serialize(new
        { primary = primary.DeviceName, secondary = secondary.DeviceName, testedSecondaryOnly = monitors.Length > 2, result = "PASS", verification = "MonitorFromWindow для настоящих WPF-тостов" }));
    }

    public static async Task RunAsync(string diagnostics)
    {
        string folder = Path.Combine(diagnostics, "notification-check-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder);
        string first = Path.Combine(folder, "Первый повтор, один.mp4"), second = Path.Combine(folder, "Второй повтор.mp4");
        File.Copy(Path.Combine(diagnostics, "player-timed-fixture.mp4"), first); File.WriteAllText(second, "notification fixture");
        CaptureMonitor primary = MonitorCatalog.Read().First(monitor => monitor.Primary);
        using var toasts = new ActionToastPresenter(ExplorerSelection.SelectFile);
        dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application")!)!;
        try
        {
            nint foreground = Native.GetForegroundWindow();
            toasts.Show("first", "Первый повтор", "Проверка первого файла", Wpf.Ui.Controls.SymbolRegular.CheckmarkCircle24, (nint)primary.Handle, first, persistent: true);
            toasts.Show("error", "Ошибка без файла", "Проверка ошибки", Wpf.Ui.Controls.SymbolRegular.ErrorCircle24, (nint)primary.Handle, error: true, persistent: true);
            toasts.Show("second", "Второй повтор", "Проверка второго файла", Wpf.Ui.Controls.SymbolRegular.CheckmarkCircle24, (nint)primary.Handle, second, persistent: true);
            if (Native.GetForegroundWindow() != foreground) throw new InvalidOperationException("Экранные тосты не должны забирать фокус.");
            Button NoticeAction(string title) => (Button)Application.Current.Windows.OfType<Window>()
                .Single(window => window.IsVisible && window.FindName("ToastTitle") is TextBlock label && label.Text == title).FindName("ToastAction");
            NoticeAction("Первый повтор").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await ExpectSelected(first);
            NoticeAction("Ошибка без файла").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await ExpectSelected(first);
            NoticeAction("Второй повтор").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await ExpectSelected(second);
            new HotkeySettingsFile(Path.Combine(folder, "hotkeys.json")).Save(new(null, null, null));
            new CaptureSettingsFile(Path.Combine(folder, "capture-settings.json")).Save(new CaptureSettings(primary.DeviceName, 30, 120, 60, folder,
                primary.Identity, CaptureSystemAudio: false, CaptureMicrophone: false, SeparateDiscord: false));
            var clip = new Clip("Проводник", "", first, "", [null, null, null], 24, 30);
            var overlay = new OverlayWindow([clip], folder);
            try
            {
                overlay.Show();
                await overlay.AddLibraryFolderAsync(folder);
                await overlay.OpenClipAsync(clip);
                overlay.UpdateLayout();
                var player = (ReplayPlayerView)((ContentControl)overlay.FindName("PlayerContent")).Content;
                Button card = Descendants((DependencyObject)overlay.FindName("LibraryItems")).OfType<Button>().Single(button => button.ToolTip?.ToString() == first);
                FrameworkElement badge = Descendants(card).OfType<FrameworkElement>()
                    .Single(element => System.Windows.Automation.AutomationProperties.GetName(element) == "В записи обнаружены ошибки");
                if (badge.Visibility == Visibility.Visible) throw new InvalidOperationException("У исправного повтора метка должна отсутствовать.");
                player.ShowError("Проверка новой ошибки просмотра");
                if (badge.Visibility != Visibility.Visible || ((FrameworkElement)player.FindName("PlaybackHealthIcon")).Visibility != Visibility.Visible)
                    throw new InvalidOperationException("Новая ошибка должна сразу отмечать время в плеере и видимую карточку без закрытия плеера.");
                ((Button)player.FindName("ShowClipInExplorerButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await ExpectSelected(first);
                if (overlay.IsVisible || player.IsPlaying) throw new InvalidOperationException("После показа файла Rewind должен скрыться и поставить просмотр на паузу.");
            }
            finally { await overlay.ClosePlayerExtensionAsync(); overlay.Close(); }
            File.WriteAllText(Path.Combine(diagnostics, "notification-check.json"), JsonSerializer.Serialize(new
            { selected = new[] { first, second }, liveHealthBadges = "PASS", menuRevealHidesOverlay = "PASS", result = "PASS custom screen toasts preserve foreground and reveal their own files in Explorer; an error has no file target; Unicode, spaces and comma preserved; no Windows balloon notifications", limitations = "WPF toast button activation with real Explorer selection; not physical clicks or exclusive fullscreen." }));
        }
        finally
        {
            foreach (dynamic window in shell.Windows())
            {
                try { if (string.Equals((string)window.Document.Folder.Self.Path, folder, StringComparison.OrdinalIgnoreCase)) window.Quit(); }
                catch (Exception error) when (error is COMException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException) { }
            }
            Marshal.FinalReleaseComObject(shell);
        }
        async Task ExpectSelected(string file)
        {
            DateTime deadline = DateTime.UtcNow.AddSeconds(12);
            while (DateTime.UtcNow < deadline)
            {
                foreach (dynamic window in shell.Windows())
                {
                    try
                    {
                        dynamic selected = window.Document.SelectedItems();
                        if ((int)selected.Count == 1 && string.Equals((string)selected.Item(0).Path, file, StringComparison.OrdinalIgnoreCase)) return;
                    }
                    catch (Exception error) when (error is COMException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException) { }
                }
                await Task.Delay(100);
            }
            throw new InvalidOperationException("Клик уведомления не выделил нужный файл в Проводнике: " + file);
        }
    }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        yield return root;
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
            foreach (DependencyObject child in Descendants(VisualTreeHelper.GetChild(root, index))) yield return child;
    }
}
