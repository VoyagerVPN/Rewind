using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Controls;

namespace Rewind.Preview;

internal static class OverlayInteractionCheck
{
    public static async Task RunAsync(string diagnostics)
    {
        string folder = Path.Combine(diagnostics, "overlay-check-" + Guid.NewGuid().ToString("N"));
        new HotkeySettingsFile(Path.Combine(folder, "hotkeys.json")).Save(new(null, null, null));
        var panel = new OverlayWindow([], folder);
        nint foreground = Native.GetForegroundWindow();
        Window? obstruction = null;
        try
        {
            for (int index = 0; index < 3; index++)
            {
                panel.ShowOverlay();
                await Task.Delay(180);
                if (Native.GetForegroundWindow() != foreground || panel.IsActive)
                    throw new InvalidOperationException("Открытие панели забирает foreground у текущего приложения.");
                panel.HideOverlay();
                if (Native.GetForegroundWindow() != foreground)
                    throw new InvalidOperationException("Скрытие панели изменяет foreground.");
            }
            panel.ShowOverlay();
            await Task.Delay(360);
            Native.GetWindowRect(new WindowInteropHelper(panel).Handle, out Native.Rect initial);
            var monitor = new Native.MonitorInfo { Size = System.Runtime.InteropServices.Marshal.SizeOf<Native.MonitorInfo>() };
            Native.GetMonitorInfo(Native.MonitorFromWindow(new WindowInteropHelper(panel).Handle, 1), ref monitor);
            if (initial.Top != monitor.Monitor.Top || initial.Bottom != monitor.Monitor.Bottom)
                throw new InvalidOperationException("Панель должна перекрывать всю высоту монитора, включая панель задач.");
            obstruction = new Window { Width = 100, Height = 40, Left = initial.Left + 10, Top = initial.Bottom - 40,
                WindowStyle = WindowStyle.None, ShowActivated = false, ShowInTaskbar = false, Topmost = true };
            obstruction.Show(); await Task.Delay(80);
            panel.ShowOverlay(); await Task.Delay(100);
            nint hit = Native.GetAncestor(Native.WindowFromPoint(new Native.Point { X = initial.Left + 20, Y = initial.Bottom - 10 }), 2);
            if (hit != new WindowInteropHelper(panel).Handle)
                throw new InvalidOperationException("Повторное открытие должно поднимать панель над другим topmost-окном без активации.");
            obstruction.Close(); obstruction = null;
            await panel.OpenClipAsync(new Clip("Проверка общего окна", "", Path.Combine(diagnostics, "player-timed-fixture.mp4"), "", [null, null, null], 24, 30));
            await Task.Delay(400);
            var player = (ReplayPlayerView)((ContentControl)panel.FindName("PlayerContent")).Content;
            if (Window.GetWindow(player) != panel || ((FrameworkElement)panel.FindName("HomeView")).Visibility != Visibility.Visible ||
                Application.Current.Windows.OfType<Window>().Count(window => window.IsVisible) != 1)
                throw new InvalidOperationException("Плеер и боковая панель должны находиться в одном видимом окне.");
            Native.GetWindowRect(new WindowInteropHelper(panel).Handle, out Native.Rect expanded);
            panel.HideOverlay();
            if (player.IsPlaying) throw new InvalidOperationException("Скрытие общего окна должно приостанавливать просмотр.");
            panel.ShowOverlay();
            await Task.Delay(150);
            if (Native.GetForegroundWindow() != foreground)
                throw new InvalidOperationException("Повторное открытие панели с плеером забирает foreground.");
            await panel.ClosePlayerExtensionAsync();
            Native.GetWindowRect(new WindowInteropHelper(panel).Handle, out Native.Rect compact);
            if (expanded.Right - expanded.Left <= compact.Right - compact.Left || ((FrameworkElement)panel.FindName("PlayerHost")).Visibility != Visibility.Collapsed)
                throw new InvalidOperationException("Закрытие плеера должно возвращать ширину боковой панели.");
            await File.WriteAllTextAsync(Path.Combine(diagnostics, "overlay-check.json"), JsonSerializer.Serialize(new
            { completedUtc = DateTime.UtcNow, foregroundPreserved = true, openings = 6, singleWindowPlayer = true, coversTaskbarArea = true, raisesOverTopmostWithoutActivation = true,
                expandedWidth = expanded.Right - expanded.Left, compactWidth = compact.Right - compact.Left,
                limitation = "Native foreground retention on this desktop; exclusive fullscreen and game audio require game acceptance." }));
        }
        finally { obstruction?.Close(); panel.Close(); }
    }
}
