using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Channels;

namespace Rewind.Preview;

internal static class VideoWorker
{
    public static async Task RunAsync(string pipeName, int parentId)
    {
        SetErrorMode(0x0002);
        using var clock = new VideoPlaybackClock();
        _ = ExitWithParentAsync(parentId);
        using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(10000);
        using var reader = new StreamReader(pipe, leaveOpen: true);
        using var writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };
        string? traceFolder = Environment.GetEnvironmentVariable("REWIND_VIDEO_TRACE_FOLDER");
        if (traceFolder is not null) Directory.CreateDirectory(traceFolder);
        await using StreamWriter? traceWriter = traceFolder is null ? null : new StreamWriter(new FileStream(
            Path.Combine(traceFolder, "mpv-" + Environment.ProcessId + ".jsonl"), FileMode.Create, FileAccess.Write, FileShare.Read));
        if (traceWriter is not null) traceWriter.AutoFlush = true;
        var messages = Channel.CreateUnbounded<VideoMessage>(new() { SingleReader = true });
        using var cancellation = new CancellationTokenSource();
        Task output = Task.Run(async () =>
        {
            try { await foreach (VideoMessage message in messages.Reader.ReadAllAsync()) await writer.WriteLineAsync(JsonSerializer.Serialize(message)); }
            catch (Exception error) when (error is IOException or OperationCanceledException) { }
        });
        MpvVideo? video = null; VideoOverlayCompatibility? overlay = null; Task? pulse = null; int ready = 0;
        try
        {
            while (await reader.ReadLineAsync() is string line)
            {
                VideoCommand command = JsonSerializer.Deserialize<VideoCommand>(line) ?? throw new IOException("Пустая команда.");
                if (traceWriter is not null) await traceWriter.WriteLineAsync(JsonSerializer.Serialize(new { utc = DateTime.UtcNow, stage = "issued", command.Kind, command.Id, command.Time, command.Paused }));
                switch (command.Kind)
                {
                    case "open":
                        if (!command.Headless && command.Handle == 0) throw new InvalidOperationException("Не удалось подготовить область видео.");
                        long openStamp = Stopwatch.GetTimestamp();
                        VideoFileIdentity? before = command.AllowDamaged ? null : VideoProbeCache.ReadIdentity(command.File!);
                        bool probeCached = !command.AllowDamaged && VideoProbeCache.Matches(command.Validation, command.File!, command.Time / 1000d, before);
                        if (!command.AllowDamaged && !probeCached) await VideoRecovery.ProbeAsync(command.File!, command.Time / 1000d, cancellation.Token);
                        double probeMs = Stopwatch.GetElapsedTime(openStamp).TotalMilliseconds;
                        string cache = Environment.GetEnvironmentVariable("REWIND_VIDEO_CACHE") ?? Path.Combine(AppContext.BaseDirectory, ".cache", "video");
                        video = new(new nint(command.Handle), cache, command.Software, command.Headless);
                        MpvVideo active = video;
                        pulse = Task.Run(async () =>
                        {
                            try
                            {
                                bool ended = false;
                                while (!cancellation.IsCancellationRequested)
                                {
                                    if (active.Fault is string fault) { messages.Writer.TryWrite(new("error", Error: fault)); return; }
                                    long time = (long)(active.PositionSeconds * 1000);
                                    // mpv не предоставляет честных суммарных decoded/displayed counters.
                                    long? dropped = active.Counters.Dropped;
                                    messages.Writer.TryWrite(new("state", time, Volatile.Read(ref ready) != 0 ? 1u : 0u,
                                        Statistics: new(null, null, dropped is long count ? (int)Math.Min(int.MaxValue, count) : null, command.Software)));
                                    if (active.HasEnded && !ended) messages.Writer.TryWrite(new("ended"));
                                    ended = active.HasEnded;
                                    await Task.Delay(16, cancellation.Token);
                                }
                            }
                            catch (OperationCanceledException) { }
                        });
                        await video.OpenAsync(command.File!, command.Time / 1000d, cancellation.Token);
                        var overlayWatch = Stopwatch.StartNew();
                        overlay?.Dispose();
                        overlay = await VideoOverlayCompatibility.CreateAsync(VideoOverlayCompatibility.NvidiaHookLoaded);
                        string overlayStatus = JsonSerializer.Serialize(new { requested = overlay.Requested, active = overlay.Active,
                            hresult = overlay.HResult, error = overlay.Error, threadId = overlay.ThreadId,
                            initializationMs = overlayWatch.Elapsed.TotalMilliseconds });
                        messages.Writer.TryWrite(new("overlay-compatibility", Details: overlayStatus));
                        if (traceWriter is not null) await traceWriter.WriteLineAsync(JsonSerializer.Serialize(new
                            { utc = DateTime.UtcNow, stage = "overlay-compatibility", details = overlayStatus }));
                        VideoProbeProof? validation = command.AllowDamaged ? null : VideoProbeCache.Create(command.File!, command.Time / 1000d, before, VideoProbeCache.ReadIdentity(command.File!));
                        if (traceWriter is not null) await traceWriter.WriteLineAsync(JsonSerializer.Serialize(new { utc = DateTime.UtcNow, stage = "open-stages", probeMs, probeCached, mpvMs = Stopwatch.GetElapsedTime(openStamp).TotalMilliseconds - probeMs }));
                        Volatile.Write(ref ready, 1);
                        messages.Writer.TryWrite(new("opened", Validation: validation));
                        messages.Writer.TryWrite(new("playing", (long)(video.PositionSeconds * 1000), 1));
                        break;
                    case "pause":
                        if (video is not null) await video.PauseAsync(command.Paused, cancellation.Token);
                        break;
                    case "seek":
                        if (video is not null)
                        {
                            await video.PauseAsync(true, cancellation.Token);
                            await video.SeekAsync(command.Time / 1000d, cancellation.Token);
                        }
                        break;
                    case "snapshot":
                        if (video is not null) await video.SaveFrameAsync(command.File!, cancellation.Token);
                        break;
                    case "close": return;
                }
                if (command.Id != 0) messages.Writer.TryWrite(new("ready", (long)((video?.PositionSeconds ?? 0) * 1000), 1, Id: command.Id));
                if (traceWriter is not null) await traceWriter.WriteLineAsync(JsonSerializer.Serialize(new { utc = DateTime.UtcNow, stage = "completed", command.Kind, command.Id, video?.PositionSeconds, video?.ReadyEvidence }));
            }
        }
        catch (Exception error)
        {
            messages.Writer.TryWrite(new("error", Error: error is InvalidDataException or FileNotFoundException ? error.Message : "Видеоплеер не смог завершить действие. Попробуй другое место или перезапусти просмотр.",
                Details: error.ToString(), DamageConfirmed: error is InvalidDataException));
        }
        finally
        {
            if (traceWriter is not null) await traceWriter.WriteLineAsync(JsonSerializer.Serialize(new { utc = DateTime.UtcNow, stage = "close-pulse" }));
            cancellation.Cancel();
            if (pulse is not null) await pulse;
            if (traceWriter is not null) await traceWriter.WriteLineAsync(JsonSerializer.Serialize(new { utc = DateTime.UtcNow, stage = "close-output" }));
            messages.Writer.TryComplete(); await output;
            if (traceWriter is not null) await traceWriter.WriteLineAsync(JsonSerializer.Serialize(new { utc = DateTime.UtcNow, stage = "close-native" }));
            // Зависание нативного освобождения ограничивается watchdog родителя.
            try { if (video is not null) await video.DisposeAsync(); }
            finally { overlay?.Dispose(); }
            if (traceWriter is not null) await traceWriter.WriteLineAsync(JsonSerializer.Serialize(new { utc = DateTime.UtcNow, stage = "closed" }));
        }
    }
    private static async Task ExitWithParentAsync(int id)
    {
        try { using Process parent = Process.GetProcessById(id); await parent.WaitForExitAsync(); }
        catch (ArgumentException) { }
        Environment.Exit(0);
    }
    [DllImport("kernel32.dll")] private static extern uint SetErrorMode(uint mode);
}
