using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Rewind.Preview;

internal sealed record ApplicationCaptureContext(string Name, string? SessionId = null, DateTime? SessionStartedUtc = null);

internal static class CaptureApplication
{
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint processId);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern SafeProcessHandle OpenProcess(uint access, bool inherit, uint processId);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageNameW(SafeProcessHandle process, uint flags, StringBuilder name, ref uint size);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassNameW(nint window, StringBuilder name, int maximum);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowTextW(nint window, StringBuilder text, int maximum);

    private static bool IsJava(string executable) => executable.Equals("java", StringComparison.OrdinalIgnoreCase) || executable.Equals("javaw", StringComparison.OrdinalIgnoreCase);

    public static string ResolveName(string executable, string? product, string? windowClass = null, string? windowTitle = null)
    {
        // Сохраняем только известное имя игры, без названия мира или сервера из заголовка.
        if (IsJava(executable) && windowClass is "GLFW30" or "LWJGL" &&
            windowTitle is not null && (windowTitle == "Minecraft" ||
                (windowTitle.StartsWith("Minecraft ", StringComparison.Ordinal) && windowTitle.Length > 10 && char.IsAsciiDigit(windowTitle[10])) ||
                (windowTitle.StartsWith("Minecraft* ", StringComparison.Ordinal) && windowTitle.Length > 11 && char.IsAsciiDigit(windowTitle[11]))))
            return "Minecraft";
        return ReplayFileName.ApplicationFolder(string.IsNullOrWhiteSpace(product) || product.Contains("Unreal Engine", StringComparison.OrdinalIgnoreCase) ? executable : product);
    }

    public static ApplicationCaptureContext Read(nint window)
    {
        GetWindowThreadProcessId(window, out uint processId);
        try
        {
            using Process process = Process.GetProcessById((int)processId);
            string executable = process.ProcessName;
            if (executable is "explorer" or "Rewind" or "Rewind.Preview" or "ShellExperienceHost" or "StartMenuExperienceHost" || processId == Environment.ProcessId) return new("Desktop");
            // Используем имя продукта приложения; заголовок окна часто содержит текущий документ или чат.
            string? product = null;
            try
            {
                // Защищённые игры запрещают перечисление модулей, но разрешают чтение пути образа.
                using SafeProcessHandle handle = OpenProcess(0x1000, false, processId);
                var path = new StringBuilder(32768); uint size = (uint)path.Capacity;
                if (!handle.IsInvalid && QueryFullProcessImageNameW(handle, 0, path, ref size))
                    product = FileVersionInfo.GetVersionInfo(path.ToString()).ProductName;
            }
            catch (Exception error) when (error is System.ComponentModel.Win32Exception or System.IO.IOException or UnauthorizedAccessException) { }
            string? windowClass = null, windowTitle = null;
            if (IsJava(executable))
            {
                var classText = new StringBuilder(256);
                GetClassNameW(window, classText, classText.Capacity);
                windowClass = classText.ToString();
                if (windowClass is "GLFW30" or "LWJGL")
                {
                    var titleText = new StringBuilder(256);
                    GetWindowTextW(window, titleText, titleText.Capacity);
                    windowTitle = titleText.ToString();
                }
            }
            string name = ResolveName(executable, product, windowClass, windowTitle);
            try
            {
                DateTime started = process.StartTime.ToUniversalTime();
                string id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(executable + ":" + processId + ":" + started.Ticks)));
                return new(name, id, started);
            }
            catch (System.ComponentModel.Win32Exception) { return new(name); }
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { return new("Desktop"); }
    }
}
