using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Rewind.Preview;

internal sealed record LibraryEntry(Clip Clip, string Game, string Folder, DateTime CreatedUtc, long Bytes, DateTime? ModifiedUtc = null);
internal sealed record LibraryPreferences(string[] Folders, bool ListView = false, double Zoom = .65, int Sort = 0, int RecentSort = 0, int RecentGrouping = 0);

internal sealed partial class ReplayLibraryIndex(string cache, UiTelemetry telemetry)
{
    private readonly SemaphoreSlim _probeGate = new(1);
    private readonly SemaphoreSlim _playbackProbeGate = new(1);
    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase) { ".mp4", ".mkv", ".mov", ".webm" };
    private static readonly HashSet<string> Ignored = new(StringComparer.OrdinalIgnoreCase) { "Buffer", "Rewind-QA", "$RECYCLE.BIN", "System Volume Information" };

    private static Clip Unprobed(string file) => new(Path.GetFileNameWithoutExtension(file), "", file, "", [null, null, null], 0, 30);

    private static bool HasThumbnail(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        try { var file = new FileInfo(path); return file.Exists && file.Length > 0; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException) { return false; }
    }

    public async Task<Clip> PreparePlaybackAsync(Clip clip, CancellationToken cancellation)
    {
        if (clip.Screens is not { Length: > 1 } screens) return await PreparePlaybackScreenAsync(clip, cancellation);
        var ready = new List<Clip>();
        foreach (Clip screen in screens) ready.Add(await PreparePlaybackScreenAsync(screen, cancellation));
        return ready.First(screen => screen.Video == clip.Video) with { Screens = ready.ToArray() };
    }

    private Task<Clip> PreparePlaybackScreenAsync(Clip clip, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        // Плееру не нужен JPEG: готовая метадата не должна ждать очередь фоновых превью.
        if (double.IsFinite(clip.Duration) && clip.Duration > 0 && double.IsFinite(clip.Fps) && clip.Fps > 0)
            return Task.FromResult(clip);
        return PrepareCoreAsync(clip, cancellation, preview: false);
    }

    public Task<Clip> PrepareAsync(Clip clip, CancellationToken cancellation) => PrepareCoreAsync(clip, cancellation, preview: true);

    private async Task<Clip> PrepareCoreAsync(Clip clip, CancellationToken cancellation, bool preview)
    {
        if (clip.Duration == 0 && clip.EventId is null)
        {
            SavedReplay? saved = await Task.Run(() => ReplayMetadata.Read(clip.Video), cancellation);
            if (saved is not null) clip = OverlayWindow.ClipFromReplay(saved, false);
        }
        if (double.IsFinite(clip.Duration) && clip.Duration > 0 && double.IsFinite(clip.Fps) && clip.Fps > 0 && (!preview || HasThumbnail(clip.Thumbnail))) return clip;
        // Два ограниченных потока: открытие файла не ждёт фоновой генерации JPEG.
        SemaphoreSlim gate = preview ? _probeGate : _playbackProbeGate;
        await gate.WaitAsync(cancellation);
        try
        {
            var info = new FileInfo(clip.Video);
            string key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(info.FullName + info.Length + info.LastWriteTimeUtc.Ticks)));
            try { Directory.CreateDirectory(cache); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            { telemetry.Event("library-cache-unavailable", new { error.Message }); }
            string metadata = Path.Combine(cache, key + ".json"), thumbnail = Path.Combine(cache, key + ".jpg");
            if (File.Exists(metadata))
            {
                Clip? cached = null;
                try { cached = JsonSerializer.Deserialize<Clip>(await File.ReadAllTextAsync(metadata, cancellation)); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
                { telemetry.Event("library-cache-unavailable", new { file = Path.GetFileName(metadata), error.Message }); }
                if (cached is not null && double.IsFinite(cached.Duration) && cached.Duration > 0 && double.IsFinite(cached.Fps) && cached.Fps > 0 && (!preview || HasThumbnail(thumbnail)))
                {
                    Clip restored = clip with { Duration = cached.Duration, Fps = cached.Fps, Thumbnail = preview && HasThumbnail(thumbnail) ? thumbnail : clip.Thumbnail,
                        EmbeddedSystemAudio = cached.EmbeddedSystemAudio, EmbeddedAudioSources = cached.EmbeddedAudioSources };
                    StorePrepared(restored); return restored;
                }
            }
            string output = await CaptureProcesses.RunAsync(CaptureProcesses.Ffprobe,
                ["-v", "error", "-show_streams", "-show_format", "-of", "json", clip.Video], TimeSpan.FromSeconds(15), cancellation,
                warning => telemetry.Event("library-source-diagnostics", new { clip.Video, warning }));
            using JsonDocument document = JsonDocument.Parse(output);
            JsonElement[] streams = document.RootElement.GetProperty("streams").EnumerateArray().ToArray();
            JsonElement video = streams.First(stream => stream.GetProperty("codec_type").GetString() == "video");
            string[] rate = (video.GetProperty("avg_frame_rate").GetString() ?? "30/1").Split('/');
            double fps = clip.EventId is not null ? clip.Fps : double.Parse(rate[0], CultureInfo.InvariantCulture) / Math.Max(1, double.Parse(rate.ElementAtOrDefault(1) ?? "1", CultureInfo.InvariantCulture));
            double duration = double.Parse(document.RootElement.GetProperty("format").GetProperty("duration").GetString()!, CultureInfo.InvariantCulture);
            int audioCount = streams.Count(stream => stream.GetProperty("codec_type").GetString() == "audio");
            string[]? sources = clip.EmbeddedAudioSources ?? (audioCount == 2 ? ["system", "microphone"] : audioCount == 3 ? ["system", "discord", "microphone"] : null);
            string preparedThumbnail = preview && HasThumbnail(thumbnail) ? thumbnail : clip.Thumbnail;
            if (preview)
            {
                try
                {
                    await CaptureProcesses.RunAsync(CaptureProcesses.Ffmpeg,
                    ["-hide_banner", "-v", "error", "-nostdin", "-y", "-ss", "0", "-i", clip.Video, "-threads", "1", "-frames:v", "1", "-vf", "scale=480:-2", "-update", "1", thumbnail], TimeSpan.FromSeconds(20), cancellation,
                    warning => telemetry.Event("library-source-diagnostics", new { clip.Video, warning }));
                    if (HasThumbnail(thumbnail)) preparedThumbnail = thumbnail;
                }
                catch (Exception error) when (!cancellation.IsCancellationRequested && error is (InvalidOperationException or InvalidDataException or OperationCanceledException))
                { telemetry.Event("library-thumbnail-unavailable", new { clip.Video, error.Message }); }
            }
            Clip ready = clip with { Duration = duration, Fps = fps, Thumbnail = preparedThumbnail, EmbeddedAudioSources = sources,
                EmbeddedSystemAudio = sources is null && audioCount > 0 };
            try { await File.WriteAllTextAsync(metadata, JsonSerializer.Serialize(ready), cancellation); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            { telemetry.Event("library-cache-save-error", new { file = Path.GetFileName(metadata), error.Message }); }
            StorePrepared(ready);
            return ready;
        }
        finally { gate.Release(); }
    }
}
