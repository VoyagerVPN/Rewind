using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text.Json;

namespace Rewind.Preview;

internal static class AudioBufferCheck
{
    public static async Task RunAsync(string folder)
    {
        Directory.CreateDirectory(folder);
        var settings = new CaptureSettings("audio-check", 30, 8, 4, folder);
        await using var audio = new SystemAudioPipe(settings, (uint)Environment.ProcessId);
        using var client = new NamedPipeClientStream(".", audio.Path[9..], PipeDirection.In, PipeOptions.Asynchronous);
        await client.ConnectAsync(5000);
        using var cancel = new CancellationTokenSource();
        var clock = Stopwatch.StartNew();
        int paused = 0;
        long received = 0;
        Task reader = Task.Run(async () =>
        {
            byte[] bytes = new byte[65536];
            try
            {
                while (!cancel.IsCancellationRequested)
                {
                    if (Volatile.Read(ref paused) != 0) { await Task.Delay(10, cancel.Token); continue; }
                    int count = await client.ReadAsync(bytes, cancel.Token);
                    if (count == 0) break;
                    Interlocked.Add(ref received, count);
                }
            }
            catch (OperationCanceledException) { }
        });
        var samples = new List<object>();
        AudioCaptureDiagnostics? shortPause = null;
        AudioCaptureDiagnostics? recovered = null;
        try
        {
            while (clock.Elapsed.TotalSeconds < 15)
            {
                double seconds = clock.Elapsed.TotalSeconds;
                Volatile.Write(ref paused, seconds is >= 3 and < 4.5 or >= 8 and < 11 ? 1 : 0);
                AudioCaptureDiagnostics snapshot = audio.Diagnostics;
                samples.Add(new { seconds, snapshot });
                if (seconds >= 6.5 && shortPause is null)
                {
                    shortPause = snapshot;
                    if (snapshot.Sources.Length != 3 || snapshot.Sources.Any(source => !source.Connected || source.Reconnects != 0 || source.Buffer?.DroppedFrames != 0))
                        throw new InvalidOperationException("Пауза 1,5 с должна сохранить звук и все три захвата: " + JsonSerializer.Serialize(snapshot));
                }
                if (seconds >= 13 && recovered is null) recovered = snapshot;
                await Task.Delay(20);
            }
            AudioCaptureDiagnostics final = audio.Diagnostics;
            if (recovered is null || final.Sources.Where((source, index) => source.Buffer?.DroppedFrames != recovered.Sources[index].Buffer?.DroppedFrames).Any())
                throw new InvalidOperationException("После восстановления чтения новые потери должны прекратиться: " + JsonSerializer.Serialize(new { recovered, final }));
            if (final.Sources.Any(source => !source.Connected || source.Reconnects != 0 || source.Buffer is not { DroppedFrames: > 0, BufferedMilliseconds: < 100 }) ||
                final.Transport.PipeWaitMaximumMilliseconds < 2000)
                throw new InvalidOperationException("Длинная пауза должна дать учтённый пропуск и восстановиться без переподключения: " + JsonSerializer.Serialize(final));
            await File.WriteAllTextAsync(Path.Combine(folder, "audio-buffer-check.json"), JsonSerializer.Serialize(new
            {
                completedUtc = DateTime.UtcNow, shortPause, final, receivedBytes = Interlocked.Read(ref received), samples,
                verified = "Real WASAPI, 1500ms pause without loss, 3000ms pause with bounded timeline gaps, no source reconnect, pipe wait measured separately."
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
        finally { cancel.Cancel(); await reader; }
    }
}
