using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;

namespace Rewind.Preview;

internal static class OverlayRegionCheck
{
    public static void Run(string folder)
    {
        var window = new Window { WindowStyle = WindowStyle.None, ShowInTaskbar = false, ShowActivated = false };
        nint handle = new WindowInteropHelper(window).EnsureHandle();
        nint region = CreateRectRgn(0, 0, 0, 0);
        try
        {
            foreach (double scale in new[] { 1d, 1.25 })
            {
                PanelBounds sidebar = PanelGeometry.ForMonitor(0, 0, 2560, 1440, scale, false);
                PanelBounds player = PanelGeometry.PlayerSurfaceBounds(2560, 1440, scale);
                OverlayWindowRegion.Apply(handle, sidebar.Width, 1440, player);
                if (GetWindowRgn(handle, region) != 3 ||
                    !PtInRegion(region, sidebar.Width / 2, 720) || !PtInRegion(region, player.Left + player.Width / 2, 720) ||
                    PtInRegion(region, 1600, 10) || PtInRegion(region, 1600, 1430) ||
                    PtInRegion(region, sidebar.Width + 2, 720) || PtInRegion(region, 2559, 720))
                    throw new InvalidOperationException("Область Windows должна сохранять панель и плеер, открывая рабочий стол вокруг них.");
            }
            OverlayWindowRegion.Apply(handle, 520, 1440, null);
            if (GetWindowRgn(handle, region) != 0 || IsWindowVisible(handle))
                throw new InvalidOperationException("Компактный режим должен сбрасывать область. Проверочное окно нельзя показывать.");
            File.WriteAllText(Path.Combine(folder, "region.json"), JsonSerializer.Serialize(new
            { verified = new[] { "sidebar-visible", "player-visible", "top-bottom-gaps", "side-gaps", "100-and-125-percent-dpi", "reset-after-player-close" }, shown = false }));
        }
        finally { DeleteObject(region); window.Close(); }
    }
    [DllImport("gdi32.dll")] private static extern nint CreateRectRgn(int left, int top, int right, int bottom);
    [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool PtInRegion(nint region, int x, int y);
    [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DeleteObject(nint region);
    [DllImport("user32.dll")] private static extern int GetWindowRgn(nint window, nint region);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindowVisible(nint window);
}
