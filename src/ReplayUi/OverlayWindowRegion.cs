using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Rewind.Preview;

internal static class OverlayWindowRegion
{
    // Область окна оставляет рабочий стол видимым и доступным для кликов вне панели и плеера.
    // Обычный HWND сохраняет нативную видеоповерхность без layered-прозрачности WPF.
    public static void Apply(nint window, int sidebarWidth, int height, PanelBounds? player)
    {
        if (player is null)
        {
            if (SetWindowRgn(window, 0, true) == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
            return;
        }
        nint region = CreateRectRgn(0, 0, sidebarWidth, height);
        if (region == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            if (player is PanelBounds bounds)
            {
                nint surface = CreateRectRgn(bounds.Left, bounds.Top, bounds.Left + bounds.Width, bounds.Top + bounds.Height);
                if (surface == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
                try { if (CombineRgn(region, region, surface, 2) == 0) throw new Win32Exception(Marshal.GetLastWin32Error()); }
                finally { DeleteObject(surface); }
            }
            if (SetWindowRgn(window, region, true) == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
            // После успешной передачи областью владеет Windows.
            region = 0;
        }
        finally { if (region != 0) DeleteObject(region); }
    }

    [DllImport("gdi32.dll", SetLastError = true)] private static extern nint CreateRectRgn(int left, int top, int right, int bottom);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern int CombineRgn(nint target, nint first, nint second, int mode);
    [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DeleteObject(nint region);
    [DllImport("user32.dll", SetLastError = true)] private static extern int SetWindowRgn(nint window, nint region, [MarshalAs(UnmanagedType.Bool)] bool redraw);
}
