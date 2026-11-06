using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace Rewind.Preview;

internal static class PrototypeControl
{
    public static void Close(int processId, string expectedBinary)
    {
        Process process;
        try { process = Process.GetProcessById(processId); }
        catch (ArgumentException) { return; }
        using (process)
        {
            if (!string.Equals(process.MainModule?.FileName, System.IO.Path.GetFullPath(expectedBinary), StringComparison.OrdinalIgnoreCase) ||
                System.IO.Path.GetFileName(expectedBinary) is not ("Rewind.Preview.exe" or "Rewind.exe"))
                throw new InvalidOperationException("Путь процесса не совпал с нашим прототипом; закрытие отменено.");
            nint panel = 0;
            string version = FileVersionInfo.GetVersionInfo(expectedBinary).ProductVersion?.Split('+')[0] ?? "unknown";
            string expectedTitle = "Rewind · " + version;
            EnumerateWindows callback = (window, _) =>
            {
                GetWindowThreadProcessId(window, out uint owner);
                if (owner != processId) return true;
                var title = new StringBuilder(256);
                GetWindowText(window, title, title.Capacity);
                if (title.ToString() != expectedTitle && title.ToString() is not ("Rewind" or "Rewind · WPF prototype" or "Rewind · UI QA")) return true;
                panel = window;
                return false;
            };
            EnumWindows(callback, 0);
            if (panel == 0 || !PostMessage(panel, 0x0010, 0, 0)) throw new InvalidOperationException("Не найдено собственное окно для штатного завершения.");
            if (!process.WaitForExit(180000)) throw new TimeoutException("Прототип ещё завершает сохранения; принудительно не закрывался.");
        }
    }

    private delegate bool EnumerateWindows(nint window, nint parameter);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool EnumWindows(EnumerateWindows callback, nint parameter);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint processId);
    [DllImport("user32.dll", EntryPoint = "GetWindowTextW", CharSet = CharSet.Unicode)] private static extern int GetWindowText(nint window, StringBuilder text, int maximum);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool PostMessage(nint window, int message, nint wParam, nint lParam);
}
