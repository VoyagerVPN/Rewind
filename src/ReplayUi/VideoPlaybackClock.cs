using System.Runtime.InteropServices;

namespace Rewind.Preview;

// У видеопроцесса нет собственного верхнего окна и звука; Windows может считать его невидимым.
internal sealed class VideoPlaybackClock : IDisposable
{
    [StructLayout(LayoutKind.Sequential)]
    private struct PowerThrottling { public uint Version, ControlMask, StateMask; }
    private bool _requested;
    public bool PrecisionHonored { get; }
    public VideoPlaybackClock()
    {
        var policy = new PowerThrottling { Version = 1, ControlMask = 4, StateMask = 0 };
        PrecisionHonored = SetProcessInformation(GetCurrentProcess(), 4, ref policy, (uint)Marshal.SizeOf<PowerThrottling>());
        SetActive(true);
    }
    public void SetActive(bool active)
    {
        if (active == _requested) return;
        if (active) _requested = TimeBeginPeriod(1) == 0;
        else { TimeEndPeriod(1); _requested = false; }
    }
    public void Dispose() => SetActive(false);
    [DllImport("kernel32.dll")] private static extern nint GetCurrentProcess();
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetProcessInformation(nint process, int informationClass, ref PowerThrottling information, uint size);
    [DllImport("winmm.dll", EntryPoint = "timeBeginPeriod")] private static extern uint TimeBeginPeriod(uint period);
    [DllImport("winmm.dll", EntryPoint = "timeEndPeriod")] private static extern uint TimeEndPeriod(uint period);
}
