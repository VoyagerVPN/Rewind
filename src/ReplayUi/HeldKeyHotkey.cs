using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Rewind.Preview;

internal sealed class HeldKeyHotkey : IDisposable
{
    private readonly LowLevelKeyboardHook _hook;
    public bool IsDisposed => _hook.IsDisposed;

    public HeldKeyHotkey(HotkeyChord chord, Action pressed)
    {
        chord.Validate();
        if (chord.HeldKey is not uint held) throw new ArgumentException("Нужна удерживаемая клавиша.", nameof(chord));
        var tracker = new HeldKeyPressTracker(chord, LowLevelKeyboardHook.Down(chord.Key));
        _hook = new LowLevelKeyboardHook((key, down) =>
        {
            if (key == chord.Key)
            {
                uint modifiers = (LowLevelKeyboardHook.Down(0x11) ? 2u : 0) | (LowLevelKeyboardHook.Down(0x12) ? 1u : 0) |
                    (LowLevelKeyboardHook.Down(0x10) ? 4u : 0);
                if (tracker.Update(key, down, modifiers, LowLevelKeyboardHook.Down(held),
                    LowLevelKeyboardHook.Down(0x5b) || LowLevelKeyboardHook.Down(0x5c))) pressed();
            }
            // Буква и F-клавиша продолжают поступать в активное приложение.
            return false;
        });
    }

    public void Dispose() => _hook.Dispose();
}

internal sealed class LowLevelKeyboardHook : IDisposable
{
    private readonly HookCallback _callback;
    private nint _hook;
    public bool IsDisposed => _hook == 0;

    public LowLevelKeyboardHook(Func<uint, bool, bool> handle)
    {
        _callback = (code, message, data) =>
        {
            if (code >= 0 && (long)message is 0x0100 or 0x0101 or 0x0104 or 0x0105 &&
                handle((uint)Marshal.ReadInt32(data), (long)message is 0x0100 or 0x0104)) return 1;
            return CallNextHookEx(0, code, message, data);
        };
        _hook = SetWindowsHookEx(13, _callback, GetModuleHandle(null), 0);
        if (_hook == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    public void Dispose()
    {
        if (_hook == 0) return;
        UnhookWindowsHookEx(_hook); _hook = 0;
        GC.KeepAlive(_callback);
    }

    public static bool Down(uint key) => (GetAsyncKeyState((int)key) & 0x8000) != 0;
    private delegate nint HookCallback(int code, nint message, nint data);
    [DllImport("user32.dll", EntryPoint = "SetWindowsHookExW", SetLastError = true)] private static extern nint SetWindowsHookEx(int type, HookCallback callback, nint module, uint thread);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool UnhookWindowsHookEx(nint hook);
    [DllImport("user32.dll")] private static extern nint CallNextHookEx(nint hook, int code, nint message, nint data);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
    [DllImport("kernel32.dll", EntryPoint = "GetModuleHandleW", CharSet = CharSet.Unicode)] private static extern nint GetModuleHandle(string? name);
}
