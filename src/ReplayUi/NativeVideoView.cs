using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace Rewind.Preview;

// VLC рисует непосредственно в дочернюю поверхность общего окна, без отдельного окна WPF поверх видео.
public sealed class NativeVideoView : HwndHost
{
    public event EventHandler? Clicked;
    public nint SurfaceHandle => Handle;

    protected override HandleRef BuildWindowCore(HandleRef parent)
    {
        // STATIC/SS_BLACKRECT даёт чёрный фон при смене кадра и освобождении видеовыхода.
        nint handle = CreateWindowEx(0, "STATIC", "", 0x56000104, 0, 0, 0, 0, parent.Handle, 0, 0, 0);
        if (handle == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
        return new HandleRef(this, handle);
    }

    protected override nint WndProc(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message == 0x0014)
        {
            GetClientRect(hwnd, out Native.Rect bounds); FillRect(wParam, ref bounds, GetStockObject(4));
            handled = true; return 1;
        }
        if (message == 0x0201 || message == 0x0210 && ((long)wParam & 0xffff) == 0x0201)
            Dispatcher.BeginInvoke(() => Clicked?.Invoke(this, EventArgs.Empty));
        return base.WndProc(hwnd, message, wParam, lParam, ref handled);
    }

    protected override void DestroyWindowCore(HandleRef window)
    {
        DestroyWindow(window.Handle);
    }

    [DllImport("user32.dll", EntryPoint = "CreateWindowExW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CreateWindowEx(uint extended, string className, string title, uint style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint parameter);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DestroyWindow(nint window);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetClientRect(nint window, out Native.Rect bounds);
    [DllImport("user32.dll")] private static extern int FillRect(nint dc, ref Native.Rect bounds, nint brush);
    [DllImport("gdi32.dll")] private static extern nint GetStockObject(int index);
}
