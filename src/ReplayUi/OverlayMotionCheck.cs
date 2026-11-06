using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Controls;
using System.Windows.Media;
using System.Diagnostics;

namespace Rewind.Preview;

internal static class OverlayMotionCheck
{
    public static async Task RunAsync(string diagnostics)
    {
        string folder = Path.Combine(diagnostics, "motion-" + Guid.NewGuid().ToString("N"));
        new HotkeySettingsFile(Path.Combine(folder, "hotkeys.json")).Save(new(null, null, null));
        CaptureMonitor source = MonitorCatalog.Read().First(monitor => monitor.Primary);
        new CaptureSettingsFile(Path.Combine(folder, "capture-settings.json")).Save(new CaptureSettings(
            source.DeviceName, 30, 15, 15, Path.Combine(folder, "clips"), source.Identity, [15],
            CaptureSystemAudio: false, CaptureMicrophone: false, SeparateDiscord: false));
        var panel = new OverlayWindow([], folder);
        nint foreground = Native.GetForegroundWindow();
        try
        {
            var monitor = new Native.MonitorInfo { Size = Marshal.SizeOf<Native.MonitorInfo>() };
            Native.GetMonitorInfo(Native.MonitorFromWindow(0, 1), ref monitor);
            var root = (Grid)panel.Content;
            var stripe = new Border { Height = 12, VerticalAlignment = VerticalAlignment.Top,
                Background = new SolidColorBrush(Color.FromRgb(11, 223, 147)), IsHitTestVisible = false };
            root.Children.Add(stripe);
            using var samplerReady = new ManualResetEventSlim();
            Task<List<int>> pixelSampling = Task.Run(() =>
            {
                nint dc = GetDC(0);
                nint memory = CreateCompatibleDC(dc);
                nint bitmap = CreateCompatibleBitmap(dc, 520, 12);
                nint previousBitmap = SelectObject(memory, bitmap);
                var result = new List<int>();
                var time = Stopwatch.StartNew();
                samplerReady.Set();
                try
                {
                    while (time.ElapsedMilliseconds < 1000)
                    {
                        BitBlt(memory, 0, 0, 520, 12, dc, monitor.Monitor.Left, monitor.Monitor.Top, 0x00CC0020);
                        int green = 0;
                        for (int x = 10; x < 510; x += 20)
                        {
                            uint pixel = GetPixel(memory, x, 6);
                            int red = (int)(pixel & 255), value = (int)((pixel >> 8) & 255), blue = (int)((pixel >> 16) & 255);
                            if (value > red + 80 && value > blue + 30) green++;
                        }
                        result.Add(green);
                        Thread.Sleep(8);
                    }
                }
                finally { SelectObject(memory, previousBitmap); DeleteObject(bitmap); DeleteDC(memory); ReleaseDC(0, dc); }
                return result;
            });
            samplerReady.Wait();
            var opening = Stopwatch.StartNew();
            panel.ShowOverlay();
            double openingMs = opening.Elapsed.TotalMilliseconds;
            nint foregroundAfterOpening = Native.GetForegroundWindow();
            nint handle = new WindowInteropHelper(panel).Handle;
            if (foregroundAfterOpening == handle || panel.IsActive)
                throw new InvalidOperationException("Штатная анимация не должна активировать панель.");
            Native.GetWindowRect(handle, out Native.Rect first);
            await Task.Delay(420);
            List<int> pixelSamples = await pixelSampling;
            root.Children.Remove(stripe);
            if (SystemParameters.ClientAreaAnimation && (!pixelSamples.Any(count => count is > 0 and < 24) || !pixelSamples.Contains(25)))
                throw new InvalidOperationException("Во время штатного выезда должно появляться настоящее содержимое WPF, а не пустая поверхность: " + string.Join(",", pixelSamples));
            Native.GetWindowRect(handle, out Native.Rect settled);
            if (first.Left != monitor.Monitor.Left || HasWindowRegion(handle))
                throw new InvalidOperationException("Открытие компактной панели не должно смещать HWND за монитор или добавлять собственную обрезку.");
            if (settled.Left != monitor.Monitor.Left || settled.Bottom != monitor.Monitor.Bottom)
                throw new InvalidOperationException("После выезда панель должна точно занять край и всю высоту монитора.");
            panel.HideOverlay();
            panel.ShowOverlay();
            await Task.Delay(45);
            panel.HideOverlay();
            if (panel.IsVisible) throw new InvalidOperationException("Скрытие во время выезда должно срабатывать сразу.");
            panel.ShowOverlay();
            await Task.Delay(420);
            Native.GetWindowRect(handle, out Native.Rect reopened);
            if (reopened.Left != settled.Left || reopened.Right != settled.Right)
                throw new InvalidOperationException("Быстрое повторное открытие не должно оставлять панель смещённой.");
            panel.ShowOverlay();
            Native.GetWindowRect(handle, out Native.Rect repeated);
            if (repeated.Left != settled.Left)
                throw new InvalidOperationException("Уже открытая панель не должна повторять выезд.");
            panel.ShowOverlay();
            await panel.ToggleCaptureAsync();
            await Task.Delay(35);
            var status = (TextBlock)panel.FindName("CaptureStatusText");
            var glyph = (FrameworkElement)panel.FindName("CaptureToggleGlyph");
            if (!status.Text.StartsWith("Запись идёт", StringComparison.Ordinal))
                throw new InvalidOperationException("Проверка анимации должна запустить настоящую запись: " + ((TextBlock)panel.FindName("CaptureMessageText")).Text);
            if (SystemParameters.ClientAreaAnimation && glyph.RenderTransform.Value.IsIdentity)
                throw new InvalidOperationException("Успешный старт записи должен дать короткий визуальный акцент на кнопке.");
            await Task.Delay(650);
            if (!glyph.RenderTransform.Value.IsIdentity)
                throw new InvalidOperationException("После акцента кнопка записи должна оставаться неподвижной.");
            panel.ShowOverlay();
            Task stopping = panel.ToggleCaptureAsync();
            bool stopAccent = false;
            do
            {
                if (status.Text == "Запись выключена" && !glyph.RenderTransform.Value.IsIdentity) stopAccent = true;
                if (stopping.IsCompleted) break;
                await Task.Delay(16);
            } while (true);
            await stopping;
            await Task.Delay(35);
            if (!glyph.RenderTransform.Value.IsIdentity) stopAccent = true;
            if (status.Text != "Запись выключена" || SystemParameters.ClientAreaAnimation && !stopAccent)
                throw new InvalidOperationException("Остановка записи должна дать отдельный короткий акцент.");
            await Task.Delay(650);
            if (!glyph.RenderTransform.Value.IsIdentity)
                throw new InvalidOperationException("Акцент остановки не должен продолжаться после завершения перехода.");
            panel.ShowSettingsSection("screens");
            await Task.Delay(250);
            panel.ShowSettingsSection("audio");
            if (panel.FindName("SettingsSelectionIndicator") is not FrameworkElement indicator)
                throw new InvalidOperationException("Выбранную вкладку должен отмечать единый движущийся указатель.");
            await Task.Delay(35);
            double movingX = indicator.RenderTransform.Value.OffsetX;
            if (SystemParameters.ClientAreaAnimation && (movingX <= 0 || movingX >= indicator.ActualWidth))
                throw new InvalidOperationException("Указатель должен двигаться между вкладками, не перескакивая сразу в конец.");
            panel.ShowSettingsSection("hotkeys");
            panel.ShowSettingsSection("screens");
            await Task.Delay(300);
            if (Math.Abs(indicator.RenderTransform.Value.OffsetX) > .5)
                throw new InvalidOperationException("При быстром переключении указатель должен остановиться на последней выбранной вкладке.");
            panel.HideOverlay();
            panel.ShowOverlay();
            var samples = new List<int>();
            for (int index = 0; index < 22; index++)
            {
                await Task.Delay(16);
                Native.GetWindowRect(handle, out Native.Rect frame);
                samples.Add(frame.Left);
                CheckMonitorClip(handle, frame.Left, monitor.Monitor.Left);
            }
            if (samples.Any(left => left != monitor.Monitor.Left) || HasWindowRegion(handle))
                throw new InvalidOperationException("Штатная анимация должна сохранять положение HWND без динамической области обрезки.");
            await panel.OpenClipAsync(new Clip("Анимация плеера", "", Path.Combine(diagnostics, "player-timed-fixture.mp4"), "", [null, null, null], 24, 30));
            var player = (ReplayPlayerView)((ContentControl)panel.FindName("PlayerContent")).Content;
            await Task.Delay(400);
            if (player.HasPlaybackError || !player.IsPlaying)
                throw new InvalidOperationException("Проверка открытия с плеером требует работающей нативной видеоповерхности.");
            panel.HideOverlay();
            panel.ShowOverlay();
            // Плеер должен вернуться сразу с живой HWND-поверхностью: GDI-выезд дублирует её.
            Marshal.ThrowExceptionForHR(DwmGetWindowAttribute(handle, 14, out int playerCloaked, sizeof(int)));
            if (playerCloaked != 0)
                throw new InvalidOperationException("Открытие с плеером не должно скрывать окно для подготовки выезда: нативное видео должно появляться сразу.");
            for (int index = 0; index < 22; index++)
            {
                await Task.Delay(16);
                Native.GetWindowRect(handle, out Native.Rect frame);
                CheckMonitorClip(handle, frame.Left, monitor.Monitor.Left);
            }
            Native.GetWindowRect(handle, out Native.Rect expanded);
            if (expanded.Left != monitor.Monitor.Left || expanded.Right != monitor.Monitor.Right || player.HasPlaybackError || player.IsPlaying)
                throw new InvalidOperationException("Повторный выезд с плеером должен сохранять геометрию и паузу видео.");
            await panel.ClosePlayerExtensionAsync();
            await File.WriteAllTextAsync(Path.Combine(diagnostics, "motion-check.json"), JsonSerializer.Serialize(new
            { completedUtc = DateTime.UtcNow, animationsEnabled = SystemParameters.ClientAreaAnimation, firstLeft = first.Left, finalLeft = settled.Left,
                samples, foregroundPreserved = foregroundAfterOpening == foreground, panelNotActivated = true, immediateHide = true, rapidReopen = true, recordingStartStop = true,
                openingMs, pixelSamples, nativeWpfContentDuringOpening = true,
                noRepeatedRecordingPulse = true, movingTabIndicator = true, monitorClip = true, nativePlayerReopen = true, immediateNativePlayerVisible = true,
                limitations = "Desktop and current monitor DPI only; exclusive fullscreen and gaming performance are not verified." }));
        }
        finally { panel.Close(); }
    }

    private static void CheckMonitorClip(nint handle, int windowLeft, int monitorLeft)
    {
        nint region = CreateRectRgn(0, 0, 0, 0);
        try
        {
            int kind = GetWindowRgn(handle, region);
            if (kind == 0)
            {
                if (windowLeft < monitorLeft) throw new InvalidOperationException("Смещённое окно должно быть обрезано по краю монитора.");
                return;
            }
            if (GetRgnBox(region, out Native.Rect bounds) > 1 && windowLeft + bounds.Left < monitorLeft)
                throw new InvalidOperationException("Выезжающее окно не должно рисоваться на мониторе слева.");
        }
        finally { DeleteObject(region); }
    }

    private static bool HasWindowRegion(nint handle)
    {
        nint region = CreateRectRgn(0, 0, 0, 0);
        try { return GetWindowRgn(handle, region) != 0; }
        finally { DeleteObject(region); }
    }

    [DllImport("gdi32.dll")] private static extern nint CreateRectRgn(int left, int top, int right, int bottom);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(nint window, uint attribute, out int value, int size);
    [DllImport("user32.dll")] private static extern nint GetDC(nint window);
    [DllImport("user32.dll")] private static extern int ReleaseDC(nint window, nint dc);
    [DllImport("gdi32.dll")] private static extern uint GetPixel(nint dc, int x, int y);
    [DllImport("gdi32.dll")] private static extern nint CreateCompatibleDC(nint dc);
    [DllImport("gdi32.dll")] private static extern nint CreateCompatibleBitmap(nint dc, int width, int height);
    [DllImport("gdi32.dll")] private static extern nint SelectObject(nint dc, nint value);
    [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DeleteDC(nint dc);
    [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool BitBlt(nint target, int x, int y,
        int width, int height, nint source, int sourceX, int sourceY, uint operation);
    [DllImport("user32.dll")] private static extern int GetWindowRgn(nint window, nint region);
    [DllImport("gdi32.dll")] private static extern int GetRgnBox(nint region, out Native.Rect bounds);
    [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DeleteObject(nint region);
}
