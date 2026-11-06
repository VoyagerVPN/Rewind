using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;

namespace Rewind.Preview;

internal static class ReplayClipDeletion
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct FileOperation
    {
        public nint Window; public uint Operation;
        [MarshalAs(UnmanagedType.LPWStr)] public string From;
        public nint To; public ushort Flags;
        [MarshalAs(UnmanagedType.Bool)] public bool Aborted;
        public nint Mappings, Title;
    }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern int SHFileOperationW(ref FileOperation operation);
    public static Task RecycleAsync(string file) => Task.Run(() =>
    {
        string path = Path.GetFullPath(file);
        if (!File.Exists(path) || (File.GetAttributes(path) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0) throw new IOException(Localization.Text("Message.TheFileIsNoLongerAvailable"));
        // Только один подтверждённый файл, без связанных папок и дополнительных диалогов Shell.
        var operation = new FileOperation { Operation = 3, From = path + '\0' + '\0', Flags = 0x0040 | 0x0010 | 0x0004 | 0x0400 | 0x2000 };
        int result = SHFileOperationW(ref operation);
        if (result != 0 || operation.Aborted || File.Exists(path)) throw new Win32Exception(result, Localization.Text("Message.CouldNotDeleteTheClipTheFileMayBeInUseByAnotherApplication"));
    });
}

public sealed class ClipDeletedEventArgs(string file) : EventArgs { public string File { get; } = file; }
