using System.Windows;
using System.Windows.Interop;

namespace Rewind.Preview;

internal static class TestWindowActivation
{
    internal static bool Enabled => Environment.GetEnvironmentVariable("REWIND_TEST_NO_ACTIVATE") == "1";

    internal static void Configure(Window window)
    {
        if (!Enabled) return;
        window.ShowActivated = false;
        window.SourceInitialized += (_, _) =>
        {
            nint handle = new WindowInteropHelper(window).Handle;
            long style = Native.GetWindowLongPtr(handle, -20).ToInt64();
            Native.SetWindowLongPtr(handle, -20, (nint)(style | 0x08000000));
            HwndSource.FromHwnd(handle).AddHook(PreventMouseActivation);
        };
    }

    private static nint PreventMouseActivation(nint handle, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message != 0x21) return 0;
        handled = true;
        return 4;
    }
}
