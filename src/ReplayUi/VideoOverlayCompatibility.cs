using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Rewind.Preview;

// Собственный обслуживаемый HWND устраняет таймаут NVIDIA в изолированном видеопроцессе.
// Окно никогда не показывается; дополнительные GPU-ресурсы не создаются.
internal sealed class VideoOverlayCompatibility : IDisposable
{
    private const string ClassName = "RewindVideoOverlayCompatibility";
    private static readonly WindowProcedure Procedure = OnWindowMessage;
    private static readonly Lazy<ushort> RegisteredClass = new(RegisterWindowClass);
    private readonly TaskCompletionSource<bool> _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Func<nint>? _createWindow;
    private Thread? _thread;
    private nint _window;
    private uint _threadId;
    private int _disposed;
    public bool Requested { get; }
    public bool Active => Volatile.Read(ref _window) != 0 && Volatile.Read(ref _disposed) == 0;
    public uint HResult { get; private set; }
    public string? Error { get; private set; }
    public uint ThreadId => _threadId;
    public static bool NvidiaHookLoaded => GetModuleHandleW("nvspcap64.dll") != 0;
    private VideoOverlayCompatibility(bool requested, Func<nint>? createWindow) { Requested = requested; _createWindow = createWindow; }

    public static async Task<VideoOverlayCompatibility> CreateAsync(bool requested, Func<nint>? createWindow = null)
    {
        var owner = new VideoOverlayCompatibility(requested, createWindow);
        if (!requested) return owner;
        try
        {
            owner._thread = new Thread(owner.Run) { IsBackground = true, Name = "Rewind video overlay messages" };
            owner._thread.SetApartmentState(ApartmentState.STA);
            owner._thread.Start();
            if (!await owner._ready.Task.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false)) owner.Dispose();
        }
        catch (Exception error)
        {
            owner.HResult = unchecked((uint)error.HResult); owner.Error = error.Message; owner.Dispose();
        }
        return owner;
    }

    private void Run()
    {
        _threadId = GetCurrentThreadId();
        try
        {
            _ = RegisteredClass.Value;
            if (Volatile.Read(ref _disposed) != 0) return;
            nint window = _createWindow is not null ? _createWindow() :
                CreateWindowExW(0x08000080, ClassName, "Rewind", 0x80000000, 0, 0, 1, 1, 0, 0, GetModuleHandleW(null), 0);
            if (window == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
            Volatile.Write(ref _window, window);
            if (Volatile.Read(ref _disposed) != 0) return;
            _ready.TrySetResult(true);
            int received;
            while ((received = GetMessageW(out Message message, 0, 0, 0)) > 0)
            {
                TranslateMessage(ref message); DispatchMessageW(ref message);
            }
            if (received < 0) throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        catch (Exception error) { HResult = unchecked((uint)error.HResult); Error = error.Message; }
        finally
        {
            nint window = Interlocked.Exchange(ref _window, 0);
            if (window != 0 && IsWindow(window)) DestroyWindow(window);
            _ready.TrySetResult(false);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        nint window = Volatile.Read(ref _window);
        if (window != 0) PostMessageW(window, 0x0010, 0, 0);
        if (_thread is not null && _thread != Thread.CurrentThread) _thread.Join(TimeSpan.FromSeconds(2));
    }

    private static ushort RegisterWindowClass()
    {
        var value = new WindowClass { Procedure = Marshal.GetFunctionPointerForDelegate(Procedure), Instance = GetModuleHandleW(null), ClassName = ClassName };
        ushort atom = RegisterClassW(ref value);
        if (atom == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
        return atom;
    }
    private static nint OnWindowMessage(nint window, uint message, nint wParam, nint lParam)
    {
        if (message == 0x0002) PostQuitMessage(0);
        return DefWindowProcW(window, message, wParam, lParam);
    }
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate nint WindowProcedure(nint window, uint message, nint wParam, nint lParam);
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WindowClass
    {
        public uint Style;
        public nint Procedure;
        public int ClassExtra, WindowExtra;
        public nint Instance, Icon, Cursor, Background;
        [MarshalAs(UnmanagedType.LPWStr)] public string? MenuName;
        [MarshalAs(UnmanagedType.LPWStr)] public string ClassName;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct Message { public nint Window; public uint Id; public nint WParam, LParam; public uint Time; public int X, Y; public uint Private; }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)] private static extern nint GetModuleHandleW(string? name);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)] private static extern ushort RegisterClassW(ref WindowClass value);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)] private static extern nint CreateWindowExW(uint extended, string className, string title, uint style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint parameter);
    [DllImport("user32.dll", ExactSpelling = true)] private static extern nint DefWindowProcW(nint window, uint message, nint wParam, nint lParam);
    [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)] private static extern int GetMessageW(out Message message, nint window, uint minimum, uint maximum);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool TranslateMessage(ref Message message);
    [DllImport("user32.dll", ExactSpelling = true)] private static extern nint DispatchMessageW(ref Message message);
    [DllImport("user32.dll", ExactSpelling = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool PostMessageW(nint window, uint message, nint wParam, nint lParam);
    [DllImport("user32.dll")] private static extern void PostQuitMessage(int code);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DestroyWindow(nint window);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindow(nint window);
}
