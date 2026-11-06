using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Rewind.Preview;

internal sealed record VideoFileIdentity(ulong Volume, ulong IdLow, ulong IdHigh, long Bytes, long Created, long Written, long Changed);
internal sealed record VideoProbeProof(string File, string Position, VideoFileIdentity Identity, int Version = 1);

// Только результаты успешного строгого probe; кэш ограничен памятью текущего процесса и не доверяет одному mtime.
internal static class VideoProbeCache
{
    private const int Capacity = 128;
    private static readonly object Gate = new();
    private static readonly Dictionary<string, VideoProbeProof> Entries = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Queue<string> Order = new();
    private static string Position(double seconds) => seconds.ToString("F3", CultureInfo.InvariantCulture);
    private static string Key(string file, double seconds) => Path.GetFullPath(file) + "\n" + Position(seconds);

    // Родитель не делает файловый I/O в UI/IPC-потоке. Видеопроцесс заново сверяет предложенное подтверждение с файлом.
    public static VideoProbeProof? Candidate(string file, double seconds)
    {
        lock (Gate) return Entries.GetValueOrDefault(Key(file, seconds));
    }
    public static void Remember(string file, double seconds, VideoProbeProof? proof)
    {
        if (proof is null || proof.Version != 1 || proof.Position != Position(seconds) ||
            !string.Equals(proof.File, Path.GetFullPath(file), StringComparison.OrdinalIgnoreCase)) return;
        string key = Key(file, seconds);
        lock (Gate)
        {
            if (!Entries.ContainsKey(key))
            {
                while (Entries.Count >= Capacity) Entries.Remove(Order.Dequeue());
                Order.Enqueue(key);
            }
            Entries[key] = proof;
        }
    }
    public static bool Matches(VideoProbeProof? proof, string file, double seconds, VideoFileIdentity? current)
        => proof is not null && current is not null && proof.Version == 1 && proof.Identity == current &&
            proof.Position == Position(seconds) && string.Equals(proof.File, Path.GetFullPath(file), StringComparison.OrdinalIgnoreCase);
    public static VideoProbeProof? Create(string file, double seconds, VideoFileIdentity? before, VideoFileIdentity? after)
        => before is not null && before == after ? new(Path.GetFullPath(file), Position(seconds), before) : null;

    // Вызывается только в изолированном worker: зависший/недоступный filesystem ограничен его обычным watchdog.
    public static VideoFileIdentity? ReadIdentity(string file)
    {
        try
        {
            // Открытый writer не даёт достоверного завершённого timestamp: в этом случае нужен полный probe.
            using SafeFileHandle handle = File.OpenHandle(file, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (!ReadBasic(handle, 0, out BasicInfo basic, Marshal.SizeOf<BasicInfo>()) ||
                !ReadId(handle, 18, out IdInfo id, Marshal.SizeOf<IdInfo>()) ||
                basic.ChangeTime <= 0 || (id.Low == 0 && id.High == 0)) return null;
            return new(id.Volume, id.Low, id.High, RandomAccess.GetLength(handle), basic.CreationTime, basic.LastWriteTime, basic.ChangeTime);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or NotSupportedException) { return null; }
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct BasicInfo
    {
        public long CreationTime, LastAccessTime, LastWriteTime, ChangeTime;
        public uint Attributes;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct IdInfo { public ulong Volume, Low, High; }
    [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReadBasic(SafeFileHandle file, int informationClass, out BasicInfo information, int size);
    [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReadId(SafeFileHandle file, int informationClass, out IdInfo information, int size);
}
