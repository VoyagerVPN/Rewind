using System.Buffers.Binary;
using System.IO;
using System.Text;
using System.Text.Json;

namespace Rewind.Preview;

internal static class ReplayMetadata
{
    private const string Prefix = "Rewind/1:";

    public static string Comment(SavedReplay replay) => Prefix + JsonSerializer.Serialize(new
    {
        replay.Width, replay.Height, replay.Fps, replay.HasSystemAudio, replay.AudioSources,
        replay.MonitorDeviceName, replay.EventId, replay.RequestedEndSeconds, replay.RequestedSeconds, replay.ExtraLeadingSeconds,
        replay.ApplicationName, replay.SessionId, replay.SessionStartedUtc, replay.SavedAtUtc
    });

    public static SavedReplay? Read(string file)
    {
        if (Path.GetExtension(file).Equals(".mp4", StringComparison.OrdinalIgnoreCase))
        {
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            string? text = ReadComment(stream);
            if (text?.StartsWith(Prefix, StringComparison.Ordinal) == true)
            {
                SavedReplay? embedded = JsonSerializer.Deserialize<SavedReplay>(text[Prefix.Length..]);
                if (embedded is not null && embedded.EventId is { Length: > 0 and <= 128 } &&
                    embedded.Width is > 0 and <= 32768 && embedded.Height is > 0 and <= 32768 &&
                    double.IsFinite(embedded.Fps) && embedded.Fps is > 0 and <= 1000 &&
                    embedded.AudioSources is { Length: <= 3 } sources &&
                    sources.Distinct().Count() == sources.Length && sources.All(source => source is "system" or "discord" or "microphone"))
                    return embedded with { File = Path.GetFullPath(file), Thumbnail = "", Duration = 0, Bytes = stream.Length, RelatedFiles = null };
            }
        }
        return ReadLegacy(file);
    }

    public static SavedReplay? ReadLegacy(string file)
    {
        // Совместимость со старыми клипами. Новый путь никогда не создаёт боковые JSON.
        string sidecar = Path.ChangeExtension(file, ".json");
        if (!System.IO.File.Exists(sidecar) || new FileInfo(sidecar).Length > 65536) return null;
        SavedReplay? old = JsonSerializer.Deserialize<SavedReplay>(System.IO.File.ReadAllText(sidecar));
        string directory = Path.GetDirectoryName(Path.GetFullPath(file))!;
        return old is null || old.Width <= 0 || old.Height <= 0 ? null : old with { File = Path.GetFullPath(file),
            RelatedFiles = old.RelatedFiles?.Select(path => Path.Combine(directory, Path.GetFileName(path))).ToArray() };
    }

    private readonly record struct Box(long Content, long End);

    private static string? ReadComment(FileStream stream)
    {
        Box? box = new(0, stream.Length);
        // Читаем только заголовки контейнера, пропуская mdat через seek. Лимиты не зависят от размера видео.
        foreach ((uint type, int skip) in new (uint, int)[] { (0x6d6f6f76, 0), (0x75647461, 0), (0x6d657461, 0), (0x696c7374, 4), (0xa9636d74, 0), (0x64617461, 0) })
        {
            if (box is not Box parent || parent.Content + skip > parent.End) return null;
            box = FindBox(stream, parent.Content + skip, parent.End, type);
        }
        if (box is not Box data || data.End - data.Content is < 8 or > 32768) return null;
        stream.Position = data.Content;
        Span<byte> flags = stackalloc byte[8]; stream.ReadExactly(flags);
        if (BinaryPrimitives.ReadUInt32BigEndian(flags) != 1) return null;
        byte[] text = new byte[(int)(data.End - stream.Position)]; stream.ReadExactly(text);
        return Encoding.UTF8.GetString(text);
    }

    private static Box? FindBox(FileStream stream, long start, long end, uint wanted)
    {
        Span<byte> header = stackalloc byte[16];
        for (int count = 0; count < 4096 && end - start >= 8; count++)
        {
            stream.Position = start; stream.ReadExactly(header[..8]);
            ulong size = BinaryPrimitives.ReadUInt32BigEndian(header);
            uint type = BinaryPrimitives.ReadUInt32BigEndian(header[4..]);
            int length = 8;
            if (size == 1)
            {
                if (end - start < 16) return null;
                stream.ReadExactly(header[8..]); size = BinaryPrimitives.ReadUInt64BigEndian(header[8..]); length = 16;
            }
            if (size == 0) size = (ulong)(end - start);
            if (size < (ulong)length || size > (ulong)(end - start)) return null;
            long next = start + (long)size;
            if (type == wanted) return new Box(start + length, next);
            start = next;
        }
        return null;
    }
}
