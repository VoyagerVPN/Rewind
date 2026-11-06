using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace Rewind.Preview;

internal static class VideoOverlayCompatibilityCheck
{
    public static async Task RunAsync(string diagnostics)
    {
        using (var disabled = await VideoOverlayCompatibility.CreateAsync(false))
            if (disabled.Requested || disabled.Active || disabled.ThreadId != 0)
                throw new InvalidOperationException("Выключенная совместимость не должна создавать окно или поток.");

        nint foreground = GetForegroundWindow();
        using var active = await VideoOverlayCompatibility.CreateAsync(true);
        if (!active.Active || active.ThreadId == 0 || active.HResult != 0)
            throw new InvalidOperationException("Включённая совместимость должна создать обслуживаемое скрытое окно: " + active.Error);
        var windows = new List<nint>();
        EnumThreadWindows(active.ThreadId, (window, _) => { windows.Add(window); return true; }, 0);
        if (windows.Count == 0 || windows.Any(IsWindowVisible) || GetForegroundWindow() != foreground)
            throw new InvalidOperationException("Вспомогательное окно должно оставаться невидимым и не менять foreground.");
        using var thread = new SafeWaitHandle(OpenThread(0x00100000, false, active.ThreadId), true);
        if (thread.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
        var closing = Stopwatch.StartNew();
        active.Dispose();
        if (active.Active || WaitForSingleObject(thread, 2000) != 0 || windows.Any(IsWindow))
            throw new InvalidOperationException("Завершение владельца должно закрывать собственное окно и поток.");
        double closeMs = closing.Elapsed.TotalMilliseconds;
        using var failed = await VideoOverlayCompatibility.CreateAsync(true, () => { Marshal.SetLastPInvokeError(8); return 0; });
        if (failed.Active || failed.HResult == 0 || string.IsNullOrEmpty(failed.Error))
            throw new InvalidOperationException("Отказ Win32 CreateWindow должен возвращать неактивный ресурс с диагностикой, не выбрасывая ошибку в плеер.");
        await File.WriteAllTextAsync(Path.Combine(diagnostics, "video-overlay-check.json"), JsonSerializer.Serialize(new
        { passed = true, windows = windows.Count, closeMs, visibleWindows = 0, foregroundPreserved = true, creationFailureRecovered = true }, new JsonSerializerOptions { WriteIndented = true }));
    }

    private delegate bool EnumWindow(nint window, nint parameter);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool EnumThreadWindows(uint thread, EnumWindow callback, nint parameter);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindow(nint window);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern nint OpenThread(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint thread);
    [DllImport("kernel32.dll")] private static extern uint WaitForSingleObject(SafeWaitHandle handle, uint milliseconds);
}
