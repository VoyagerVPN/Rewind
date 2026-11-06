using System.Globalization;
using System.IO;

namespace Rewind.Preview;

internal static class VideoRecovery
{
    public static async Task<double?> FindNextAsync(string file, double from, double duration, IProgress<double> progress, Action<double> failedProbe, CancellationToken cancellation)
    {
        if (!File.Exists(file)) throw new FileNotFoundException(Localization.Text("Message.TheReplayFileIsUnavailable"), file);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(TimeSpan.FromSeconds(25));
        double end = Math.Max(0, duration - .1), step = Math.Max(1, (duration - from) / 16);
        try
        {
            for (int index = 0; index <= 16 && from < end; index++)
            {
                double seconds = Math.Min(end, from + 1 + index * step);
                deadline.Token.ThrowIfCancellationRequested(); progress.Report(seconds);
                try { await ProbeAsync(file, seconds, deadline.Token); return seconds; }
                catch (InvalidDataException) { failedProbe(seconds); }
                if (seconds >= end) break;
            }
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested) { }
        return null;
    }
    public static async Task ProbeAsync(string file, double seconds, CancellationToken cancellation)
    {
        try
        {
            // Проверяем выбранный участок, а не начало файла: ошибки до точки перехода допустимы.
            string progress = await CaptureProcesses.RunAsync(CaptureProcesses.Ffmpeg,
                ["-hide_banner", "-v", "error", "-nostdin", "-xerror", "-err_detect", "explode", "-threads", "2",
                 "-ss", seconds.ToString("F3", CultureInfo.InvariantCulture), "-i", file, "-map", "0:v:0", "-t", "0.5",
                 "-an", "-sn", "-dn", "-progress", "pipe:1", "-nostats", "-f", "null", "-"],
                TimeSpan.FromSeconds(5), cancellation, nonFatalDiagnostics: _ => { });
            if (!progress.Split('\n').Any(line => line.StartsWith("frame=", StringComparison.Ordinal) && int.TryParse(line.AsSpan(6).Trim(), out int frames) && frames > 0))
                throw new InvalidDataException(Localization.Text("Message.ThisSegmentHasNoReadableFrames"));
        }
        catch (Exception error) when (error is InvalidOperationException or InvalidDataException)
        { throw new InvalidDataException(Localization.Text("Message.CouldNotPlayThisSegmentTryALaterPositionOrFindAnAvailableSegment"), error); }
        catch (OperationCanceledException error) when (!cancellation.IsCancellationRequested)
        { throw new TimeoutException(Localization.Text("Message.TheSegmentCheckTimedOutTryAgainOrChooseALaterPosition"), error); }
    }
}
