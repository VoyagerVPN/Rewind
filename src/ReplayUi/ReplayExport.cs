using System.Globalization;
using System.IO;
using System.Text.Json;
using Rewind.DiskBuffer;

namespace Rewind.Preview;

internal sealed record ReplayExportRequest(string Comment, string[] AudioSources, int Fps, double Seconds);
internal sealed record ReplayExportDiagnostics(double Milliseconds, long InputBytes, bool HeaderReserved);

internal static class ReplayExport
{
    public const string Extension = "ts";

    public static string[] SegmentArguments(string root, string manifest, int listSize) =>
        ["-f", "segment", "-segment_time", "2", "-reset_timestamps", "0", "-segment_list", manifest,
            "-segment_list_type", "csv", "-segment_list_size", listSize.ToString(CultureInfo.InvariantCulture),
            "-segment_format", "mpegts", "-segment_format_options", "mpegts_copyts=1:mpegts_flags=+initial_discontinuity",
            Path.Combine(root, "part%08d." + Extension)];

    public static async Task<ReplayExportDiagnostics> ExportAsync(IReadOnlyList<MediaSegment> segments, string destination,
        ReplayExportRequest request, CancellationToken cancellation = default)
    {
        if (segments.Count == 0 || !double.IsFinite(request.Seconds) || request.Seconds <= 0)
            throw new ArgumentException(Localization.Text("Message.ExportRequiresCompletedSegmentsAndAPositiveDuration"));
        var timer = System.Diagnostics.Stopwatch.StartNew();
        string json = await CaptureProcesses.RunAsync(CaptureProcesses.Ffprobe,
            ["-v", "error", "-select_streams", "v:0", "-show_entries", "stream=start_time", "-of", "json", segments[0].Path], TimeSpan.FromSeconds(15), cancellation);
        using JsonDocument probe = JsonDocument.Parse(json);
        double firstVideo = double.Parse(probe.RootElement.GetProperty("streams")[0].GetProperty("start_time").GetString()!, CultureInfo.InvariantCulture);
        if (!double.IsFinite(firstVideo)) throw new InvalidDataException(Localization.Text("Message.TheFirstVideoFrameTimestampIsUnavailable"));
        var arguments = new List<string> { "-hide_banner", "-v", "error", "-nostdin", "-y", "-copyts", "-itsoffset", (-firstVideo).ToString("F9", CultureInfo.InvariantCulture),
            // При 1440p120 стандартные 5 МБ заканчиваются до определения параметров тихой AAC-дорожки.
            "-probesize", "33554432", "-f", "mpegts", "-i", "pipe:0", "-map", "0:v:0", "-map", "0:a?", "-c", "copy", "-t", request.Seconds.ToString("F9", CultureInfo.InvariantCulture),
            "-avoid_negative_ts", "disabled", "-metadata", "comment=" + request.Comment };
        for (int index = 0; index < request.AudioSources.Length; index++)
            arguments.AddRange([$"-metadata:s:a:{index}", "handler_name=" + request.AudioSources[index], $"-metadata:s:a:{index}", "title=" + request.AudioSources[index]]);
        long estimate = 1024 * 1024 + (long)Math.Ceiling((request.Seconds + 1) * (request.Fps + request.AudioSources.Length * 47) * 24);
        long reserve = (estimate + 1024 * 1024 - 1) / (1024 * 1024) * (1024 * 1024);
        string[] files = segments.Select(segment => segment.Path).ToArray();
        bool reserved = true;
        try
        {
            // Общая шкала TS проходит через stdin без склейки по округлённым длительностям и без декодирования.
            await CaptureProcesses.RunAsync(CaptureProcesses.Ffmpeg,
                arguments.Concat(["-moov_size", reserve.ToString(CultureInfo.InvariantCulture), destination]), TimeSpan.FromMinutes(3), cancellation, inputFiles: files);
        }
        catch (InvalidOperationException error) when (error.Message.Contains("reserved_moov_size", StringComparison.OrdinalIgnoreCase) ||
            error.Message.Contains("moov atom not large enough", StringComparison.OrdinalIgnoreCase))
        {
            // При неожиданно большом заголовке сохраняем повтор прежним безопасным способом.
            reserved = false;
            await CaptureProcesses.RunAsync(CaptureProcesses.Ffmpeg, arguments.Concat(["-movflags", "+faststart", destination]),
                TimeSpan.FromMinutes(3), cancellation, inputFiles: files);
        }
        return new(timer.Elapsed.TotalMilliseconds, segments.Sum(segment => new FileInfo(segment.Path).Length), reserved);
    }
}
