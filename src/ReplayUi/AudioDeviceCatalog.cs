using NAudio.CoreAudioApi;
using System.Runtime.InteropServices;

namespace Rewind.Preview;

public sealed record AudioDeviceChoice(string? Id, string Name)
{
    public override string ToString() => Name;
}

internal static class AudioDeviceCatalog
{
    public static AudioDeviceChoice[] Microphones()
    {
        using var enumerator = new MMDeviceEnumerator();
        using var devices = enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active);
        var result = new List<AudioDeviceChoice> { new(null, Localization.Text("Message.WindowsDefaultAutomatic")) };
        foreach (MMDevice device in devices) using (device) result.Add(new(device.ID, device.FriendlyName));
        try { result[0] = new(null, Localization.Text("Message.WindowsDefault") + ResolveMicrophone(null).Name); }
        catch (Exception) { result[0] = new(null, Localization.Text("Message.WindowsDefaultDeviceUnavailable")); }
        return result.ToArray();
    }

    public static string DefaultOutput()
    {
        using var enumerator = new MMDeviceEnumerator();
        using MMDevice device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        return device.FriendlyName;
    }

    public static (string Id, string Name) ResolveMicrophone(string? id)
    {
        using var enumerator = new MMDeviceEnumerator();
        using MMDevice device = id is null
            ? enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Multimedia) : enumerator.GetDevice(id);
        if (device.State != DeviceState.Active) throw new InvalidOperationException(Localization.Text("Message.TheSelectedMicrophoneIsDisconnected"));
        return (device.ID, device.FriendlyName);
    }

    public static uint? DiscordProcess()
    {
        // Голос Discord может работать в дочернем Electron-процессе: выбираем корень дерева.
        using var snapshot = CreateToolhelp32Snapshot(2, 0);
        if (snapshot.IsInvalid) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        var entry = new ProcessEntry { Size = (uint)Marshal.SizeOf<ProcessEntry>() };
        var matches = new Dictionary<uint, uint>();
        if (Process32FirstW(snapshot, ref entry))
            do
            {
                if (entry.ExeFile.Equals("Discord.exe", StringComparison.OrdinalIgnoreCase) ||
                    entry.ExeFile.Equals("DiscordPTB.exe", StringComparison.OrdinalIgnoreCase) ||
                    entry.ExeFile.Equals("DiscordCanary.exe", StringComparison.OrdinalIgnoreCase))
                    matches[entry.ProcessId] = entry.ParentProcessId;
            } while (Process32NextW(snapshot, ref entry));
        uint[] roots = matches.Where(pair => !matches.ContainsKey(pair.Value)).Select(pair => pair.Key).ToArray();
        if (roots.Length > 1) throw new InvalidOperationException(Localization.Text("Message.MultipleDiscordInstancesAreRunningLeaveOneRunningToSeparateItsAud"));
        return roots.Length == 1 ? roots[0] : null;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry
    {
        public uint Size, Usage, ProcessId;
        public nuint DefaultHeapId;
        public uint ModuleId, Threads, ParentProcessId;
        public int Priority;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string ExeFile;
    }
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern Microsoft.Win32.SafeHandles.SafeFileHandle CreateToolhelp32Snapshot(uint flags, uint processId);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool Process32FirstW(Microsoft.Win32.SafeHandles.SafeFileHandle snapshot, ref ProcessEntry entry);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool Process32NextW(Microsoft.Win32.SafeHandles.SafeFileHandle snapshot, ref ProcessEntry entry);
}
