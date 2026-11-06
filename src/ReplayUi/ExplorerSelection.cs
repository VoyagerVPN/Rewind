using System.IO;
using System.Runtime.InteropServices;

namespace Rewind.Preview;

internal static class ExplorerSelection
{
    public static void SelectFile(string file)
    {
        string fullPath = Path.GetFullPath(file);
        if (!File.Exists(fullPath)) throw new FileNotFoundException(Localization.Text("Message.TheReplayFileIsUnavailable"), fullPath);
        Marshal.ThrowExceptionForHR(CoInitializeEx(0, 2));
        nint item = 0;
        try
        {
            Marshal.ThrowExceptionForHR(SHParseDisplayName(fullPath, 0, out item, 0, out _));
            Marshal.ThrowExceptionForHR(SHOpenFolderAndSelectItems(item, 0, 0, 0));
        }
        finally { if (item != 0) Marshal.FreeCoTaskMem(item); CoUninitialize(); }
    }

    [DllImport("ole32.dll")] private static extern int CoInitializeEx(nint reserved, uint mode);
    [DllImport("ole32.dll")] private static extern void CoUninitialize();
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern int SHParseDisplayName(string name, nint context, out nint item, uint attributes, out uint resultAttributes);
    [DllImport("shell32.dll")] private static extern int SHOpenFolderAndSelectItems(nint item, uint count, nint children, uint flags);
}
