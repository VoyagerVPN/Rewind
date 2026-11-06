using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Rewind.Preview;

internal sealed class OutsideClickWatcher : IDisposable
{
    private readonly HookCallback _callback;
    private nint _hook;

    public OutsideClickWatcher(nint panel, Action outside)
    {
        _callback = (code, message, data) =>
        {
            if (code >= 0 && (long)message is 0x0201 or 0x0204 or 0x0207 or 0x020B)
            {
                Native.Point point = Marshal.PtrToStructure<Native.Point>(data);
                nint hit = Native.GetAncestor(Native.WindowFromPoint(point), 2);
                bool owned = false;
                // Выпадающие меню и файловый диалог принадлежат панели, даже когда выходят за её края.
                for (int depth = 0; hit != 0 && depth < 16; depth++, hit = GetWindow(hit, 4))
                    if (hit == panel) { owned = true; break; }
                if (!owned) outside();
            }
            // Наблюдаем клик, не блокируя его доставку другому приложению.
            return CallNextHookEx(0, code, message, data);
        };
        _hook = SetWindowsHookEx(14, _callback, GetModuleHandle(null), 0);
        if (_hook == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    public void Dispose()
    {
        if (_hook == 0) return;
        UnhookWindowsHookEx(_hook); _hook = 0;
        GC.KeepAlive(_callback);
    }

    private delegate nint HookCallback(int code, nint message, nint data);
    [DllImport("user32.dll", EntryPoint = "SetWindowsHookExW", SetLastError = true)] private static extern nint SetWindowsHookEx(int type, HookCallback callback, nint module, uint thread);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool UnhookWindowsHookEx(nint hook);
    [DllImport("user32.dll")] private static extern nint CallNextHookEx(nint hook, int code, nint message, nint data);
    [DllImport("user32.dll")] private static extern nint GetWindow(nint window, uint command);
    [DllImport("kernel32.dll", EntryPoint = "GetModuleHandleW", CharSet = CharSet.Unicode)] private static extern nint GetModuleHandle(string? name);
}
