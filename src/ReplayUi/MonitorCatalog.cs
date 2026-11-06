using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace Rewind.Preview;

public sealed record CaptureMonitor(string DeviceName, string Name, int AdapterIndex, int OutputIndex,
    int Left, int Top, int Width, int Height, int ScalePercent, bool Primary, long Handle, string Identity)
{
    public string Label => $"{DeviceName.Replace("\\\\.\\", "")} · {Name}";
    public string Details => $"{Width} × {Height} · масштаб {ScalePercent}%" + (Primary ? " · основной" : "");
    public override string ToString() => Label;
}

public static class MonitorCatalog
{
    public static IReadOnlyList<CaptureMonitor> Read()
    {
        var result = new List<CaptureMonitor>();
        Guid iid = new("770aae78-f26f-4dba-a829-253c83d1b387");
        Marshal.ThrowExceptionForHR(CreateDXGIFactory1(ref iid, out nint factory));
        try
        {
            var adapters = Method<Enumerate>(factory, 12);
            for (uint adapterIndex = 0; ; adapterIndex++)
            {
                int hr = adapters(factory, adapterIndex, out nint adapter);
                if (hr == unchecked((int)0x887A0002)) break;
                Marshal.ThrowExceptionForHR(hr);
                try
                {
                    var outputs = Method<Enumerate>(adapter, 7);
                    for (uint outputIndex = 0; ; outputIndex++)
                    {
                        hr = outputs(adapter, outputIndex, out nint output);
                        if (hr == unchecked((int)0x887A0002)) break;
                        Marshal.ThrowExceptionForHR(hr);
                        try
                        {
                            Marshal.ThrowExceptionForHR(Method<GetDescription>(output, 7)(output, out OutputDescription desc));
                            if (!desc.Attached) continue;
                            var device = new DisplayDevice { Size = Marshal.SizeOf<DisplayDevice>() };
                            bool found = EnumDisplayDevices(desc.DeviceName, 0, ref device, 1);
                            string identity = found ? device.Id : desc.DeviceName;
                            string name = found ? ReadEdidName(identity) ?? device.Description : desc.DeviceName;
                            var info = new Native.MonitorInfo { Size = Marshal.SizeOf<Native.MonitorInfo>() };
                            bool primary = Native.GetMonitorInfo(desc.Monitor, ref info) && (info.Flags & 1) != 0;
                            int scale = GetScaleFactorForMonitor(desc.Monitor, out int factor) == 0 ? factor : 100;
                            result.Add(new CaptureMonitor(desc.DeviceName, name, (int)adapterIndex, (int)outputIndex,
                                desc.Desktop.Left, desc.Desktop.Top, desc.Desktop.Right - desc.Desktop.Left,
                                desc.Desktop.Bottom - desc.Desktop.Top, scale, primary, desc.Monitor.ToInt64(), identity));
                        }
                        finally { Marshal.Release(output); }
                    }
                }
                finally { Marshal.Release(adapter); }
            }
        }
        finally { Marshal.Release(factory); }
        return result.OrderBy(monitor => monitor.Left).ThenBy(monitor => monitor.Top).ToArray();
    }

    private static T Method<T>(nint instance, int slot) where T : Delegate =>
        Marshal.GetDelegateForFunctionPointer<T>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(instance), slot * nint.Size));

    private static string? ReadEdidName(string identity)
    {
        string[] parts = identity.Split('#');
        if (parts.Length < 3) return null;
        try
        {
            using RegistryKey? key = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Enum\DISPLAY\{parts[1]}\{parts[2]}\Device Parameters");
            if (key?.GetValue("EDID") is not byte[] bytes || bytes.Length < 128) return null;
            for (int offset = 54; offset + 18 <= 126; offset += 18)
                if (bytes[offset] == 0 && bytes[offset + 1] == 0 && bytes[offset + 3] == 0xfc)
                    return Encoding.ASCII.GetString(bytes, offset + 5, 13).Trim('\0', '\n', '\r', ' ');
        }
        catch (Exception error) when (error is UnauthorizedAccessException or System.IO.IOException or System.Security.SecurityException) { }
        return null;
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int Enumerate(nint instance, uint index, out nint value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetDescription(nint instance, out OutputDescription value);
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct OutputDescription
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        public Native.Rect Desktop;
        [MarshalAs(UnmanagedType.Bool)] public bool Attached;
        public uint Rotation;
        public nint Monitor;
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DisplayDevice
    {
        public int Size;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Description;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Id;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Key;
    }
    [DllImport("dxgi.dll", ExactSpelling = true)]
    private static extern int CreateDXGIFactory1(ref Guid iid, out nint factory);
    [DllImport("user32.dll", EntryPoint = "EnumDisplayDevicesW", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplayDevices(string deviceName, uint index, ref DisplayDevice device, uint flags);
    [DllImport("shcore.dll")]
    private static extern int GetScaleFactorForMonitor(nint monitor, out int scale);
}
