using System.Globalization;
using System.IO;
using System.Text.Json;
using Rewind.DiskBuffer;

namespace Rewind.Preview;

internal static class ReplayExportCheck
{
    public static async Task RunAsync(string diagnostics)
    {
        string run = Path.Combine(diagnostics, "replay-export-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(run);
        string reference = Path.Combine(run, "reference.mp4"), manifest = Path.Combine(run, "segments.csv");
        await CaptureProcesses.RunAsync(CaptureProcesses.Ffmpeg,
            ["-v", "error", "-nostdin", "-y", "-f", "lavfi", "-i", "testsrc2=size=320x180:rate=120",
                "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=48000", "-f", "lavfi", "-i", "anullsrc=r=48000:cl=stereo",
                "-f", "lavfi", "-i", "sine=frequency=880:sample_rate=48000", "-t", "12", "-map", "0:v", "-map", "1:a", "-map", "2:a", "-map", "3:a",
                "-c:v", "libx264", "-preset", "ultrafast", "-threads", "2", "-g", "240", "-bf", "0",
                "-b:v", "32M", "-minrate", "32M", "-maxrate", "32M", "-bufsize", "32M", "-x264-params", "nal-hrd=cbr:filler=1",
                "-c:a", "aac", "-b:a", "128k", "-ar", "48000", "-ac", "2", reference], TimeSpan.FromSeconds(20));
        // Ранний отказ FFmpeg должен возвращать его диагностику, а не ошибку записи в stdin.
        string? rejected = null;
        try
        {
            await CaptureProcesses.RunAsync(CaptureProcesses.Ffmpeg,
                ["-v", "error", "-nostdin", "-y", "-f", "rawvideo", "-pixel_format", "rgb24", "-video_size", "320x180", "-i", "pipe:0", "-f", "rewind-invalid-format", Path.Combine(run, "invalid.mp4")],
                TimeSpan.FromSeconds(10), inputFiles: Enumerable.Repeat(reference, 32).ToArray());
        }
        catch (InvalidOperationException error) { rejected = error.Message; }
        if (rejected is null || !rejected.Contains("rewind-invalid-format", StringComparison.Ordinal))
            throw new InvalidOperationException("Отказ экспорта должен объяснять ошибку FFmpeg.");
        await CaptureProcesses.RunAsync(CaptureProcesses.Ffmpeg,
            new[] { "-v", "error", "-nostdin", "-y", "-copyts", "-i", reference, "-map", "0", "-c", "copy" }
                .Concat(ReplayExport.SegmentArguments(run, manifest, 0)), TimeSpan.FromSeconds(20));
        MediaSegment[] segments = File.ReadAllLines(manifest).Select(line => line.Split(',')).Select(values => new MediaSegment(
            Path.Combine(run, Path.GetFileName(values[0].Trim('"'))), TimeSpan.FromSeconds(double.Parse(values[1], CultureInfo.InvariantCulture)),
            TimeSpan.FromSeconds(double.Parse(values[2], CultureInfo.InvariantCulture)))).ToArray();
        string output = Path.Combine(run, "export.mp4");
        ReplayExportDiagnostics export = await ReplayExport.ExportAsync(segments, output, new("test", ["system", "discord", "microphone"], 120, 12));
        string json = await CaptureProcesses.RunAsync(CaptureProcesses.Ffprobe,
            ["-v", "error", "-show_streams", "-show_packets", "-show_entries", "stream=index,codec_type,time_base,avg_frame_rate:packet=stream_index,pts", "-of", "json", output], TimeSpan.FromSeconds(10));
        using JsonDocument document = JsonDocument.Parse(json);
        var counts = new List<int>();
        foreach (JsonElement stream in document.RootElement.GetProperty("streams").EnumerateArray())
        {
            int index = stream.GetProperty("index").GetInt32();
            long[] pts = document.RootElement.GetProperty("packets").EnumerateArray().Where(packet => packet.GetProperty("stream_index").GetInt32() == index)
                .Select(packet => packet.GetProperty("pts").GetInt64()).ToArray();
            counts.Add(pts.Length);
            string[] timeBase = stream.GetProperty("time_base").GetString()!.Split('/');
            double unit = double.Parse(timeBase[0], CultureInfo.InvariantCulture) / double.Parse(timeBase[1], CultureInfo.InvariantCulture);
            bool video = stream.GetProperty("codec_type").GetString() == "video";
            double expected = video ? 1d / 120 : 1024d / 48000;
            for (int packet = 1; packet < pts.Length; packet++)
                if (Math.Abs((pts[packet] - pts[packet - 1]) * unit - expected) > 0.0000001)
                    throw new InvalidOperationException($"Склейка должна сохранять точный шаг {(video ? "120 FPS" : "AAC 1024/48000")}: поток {index}, пакет {packet}, интервал {(pts[packet] - pts[packet - 1]) * unit:F9} с.");
            if (video && (pts.Length != 1440 || stream.GetProperty("avg_frame_rate").GetString() != "120/1"))
                throw new InvalidOperationException("12 секунд эталона должны сохранить все 1440 кадров и частоту 120/1.");
        }
        if (counts.Count != 4) throw new InvalidOperationException("Нужны видео и три независимые аудиодорожки.");
        await CaptureProcesses.RunAsync(CaptureProcesses.Ffmpeg,
            ["-v", "error", "-nostdin", "-i", output, "-map", "0", "-fps_mode", "passthrough", "-enc_time_base", "demux", "-f", "null", "-"], TimeSpan.FromSeconds(20));
        string[] expectedHashes = await FrameHashesAsync(reference);
        string[] actualHashes = await FrameHashesAsync(output);
        if (!expectedHashes.SequenceEqual(actualHashes))
            throw new InvalidOperationException("Экспорт изменил декодированные видеокадры.");
        var epochs = new List<object>();
        foreach (double epoch in new[] { 100.3, 95440d })
        {
            string epochRoot = Path.Combine(run, "Сегменты ' " + epoch.ToString(CultureInfo.InvariantCulture));
            Directory.CreateDirectory(epochRoot);
            string epochManifest = Path.Combine(epochRoot, "segments.csv");
            await CaptureProcesses.RunAsync(CaptureProcesses.Ffmpeg,
                new[] { "-v", "error", "-nostdin", "-y", "-copyts", "-itsoffset", epoch.ToString(CultureInfo.InvariantCulture), "-i", reference, "-map", "0", "-c", "copy" }
                    .Concat(ReplayExport.SegmentArguments(epochRoot, epochManifest, 0)), TimeSpan.FromSeconds(20));
            MediaSegment[] shifted = File.ReadAllLines(epochManifest).Select(line => line.Split(',')).Select(values => new MediaSegment(
                Path.Combine(epochRoot, Path.GetFileName(values[0].Trim('"'))), TimeSpan.FromSeconds(double.Parse(values[1], CultureInfo.InvariantCulture)),
                TimeSpan.FromSeconds(double.Parse(values[2], CultureInfo.InvariantCulture)))).ToArray();
            string shiftedOutput = Path.Combine(epochRoot, "export.mp4");
            ReplayExportDiagnostics shiftedExport = await ReplayExport.ExportAsync(shifted, shiftedOutput,
                new("epoch", ["system", "discord", "microphone"], 120, 12));
            string[] shiftedHashes = await FrameHashesAsync(shiftedOutput);
            if (!expectedHashes.SequenceEqual(shiftedHashes))
                throw new InvalidOperationException($"Экспорт потерял или изменил видеокадры при эпохе {epoch} секунд.");
            await CaptureProcesses.RunAsync(CaptureProcesses.Ffmpeg,
                ["-v", "error", "-nostdin", "-i", shiftedOutput, "-map", "0", "-fps_mode", "passthrough", "-enc_time_base", "demux", "-f", "null", "-"], TimeSpan.FromSeconds(20));
            epochs.Add(new { epoch, shiftedExport, frames = expectedHashes.Length });
        }
        await File.WriteAllTextAsync(Path.Combine(diagnostics, "replay-export.json"), JsonSerializer.Serialize(new { run, export, counts, epochs, exactVideoClock = true, exactAudioClock = true, fullDecode = true, decodedFramesPreserved = true }));
    }

    private static async Task<string[]> FrameHashesAsync(string file)
    {
        string hashes = await CaptureProcesses.RunAsync(CaptureProcesses.Ffmpeg,
            ["-v", "error", "-nostdin", "-i", file, "-map", "0:v:0", "-fps_mode", "passthrough", "-f", "framemd5", "-"], TimeSpan.FromSeconds(20));
        return hashes.Split('\n').Where(line => line.Length > 0 && !line.StartsWith('#'))
            .Select(line => line.Split(',')[^1].Trim()).ToArray();
    }
}
