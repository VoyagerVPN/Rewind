using System.IO;
using System.Globalization;
using System.Text.Json;

namespace Rewind.Preview;

internal static class CaptureCadenceCheck
{
    public static async Task CheckUnconnectedStopAsync(string diagnostics)
    {
        CaptureMonitor monitor = MonitorCatalog.Read().First(item => item.Primary);
        string root = Path.Combine(diagnostics, "capture-stop-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var profile = new MonitorRecordingProfile(monitor.DeviceName, true, 120, 0, monitor.Identity);
        await using var bridge = new NativeCaptureBridge(monitor, profile, 0, Path.Combine(root, "native.log"));
        var watch = System.Diagnostics.Stopwatch.StartNew();
        await bridge.StopAsync();
        double milliseconds = watch.Elapsed.TotalMilliseconds;
        await File.WriteAllTextAsync(Path.Combine(diagnostics, "capture-stop-check.json"), JsonSerializer.Serialize(new
        { root, milliseconds, noFrames = double.IsNaN(bridge.FirstFrameSeconds), error = bridge.Error, scope = "Stop video pipe before any client connection; no encoder or GPU capture starts." }));
        if (milliseconds > 500 || !double.IsNaN(bridge.FirstFrameSeconds) || bridge.Error is not null)
            throw new InvalidOperationException("Unconnected capture must stop within500ms without waiting for the worker flush watchdog: " + milliseconds);
    }

    public static async Task CheckLogContentionAsync(string diagnostics, bool recoverWorker = false)
    {
        CaptureMonitor monitor = MonitorCatalog.Read().First(item => item.Primary);
        string root = Path.Combine(diagnostics, "capture-log-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string log = Path.Combine(root, "native.log"), stream = Path.Combine(root, "video.ts");
        long epoch = (long)(System.Diagnostics.Stopwatch.GetTimestamp() * (10_000_000.0 / System.Diagnostics.Stopwatch.Frequency));
        var profile = new MonitorRecordingProfile(monitor.DeviceName, true, 120, 0, monitor.Identity);
        var warnings = new List<string>();
        await using var bridge = new NativeCaptureBridge(monitor, profile, epoch, log, warnings.Add);
        await using var client = new System.IO.Pipes.NamedPipeClientStream(".", Path.GetFileName(bridge.Path), System.IO.Pipes.PipeDirection.In, System.IO.Pipes.PipeOptions.Asynchronous);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        await client.ConnectAsync(deadline.Token);
        long bytes = 0;
        await using var output = new FileStream(stream, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 65536, FileOptions.Asynchronous);
        Task reader = Task.Run(async () =>
        {
            byte[] block = new byte[65536]; int count;
            while ((count = await client.ReadAsync(block, deadline.Token)) > 0)
            { await output.WriteAsync(block.AsMemory(0, count), deadline.Token); Interlocked.Add(ref bytes, count); }
        });
        var deltas = new List<long>();
        double stopMilliseconds = 0;
        try
        {
            DateTime ready = DateTime.UtcNow.AddSeconds(10);
            while ((double.IsNaN(bridge.FirstFrameSeconds) || !File.Exists(log)) && DateTime.UtcNow < ready) await Task.Delay(50);
            if (double.IsNaN(bridge.FirstFrameSeconds)) throw new InvalidOperationException("Native capture did not become ready before log contention.");
            using (var locked = new FileStream(log, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                long previous = Interlocked.Read(ref bytes); int stagnant = 0;
                for (int second = 0; second < 16; second++)
                {
                    await Task.Delay(1000); long current = Interlocked.Read(ref bytes);
                    deltas.Add(current - previous); stagnant = current == previous ? stagnant + 1 : 0; previous = current;
                    if (stagnant >= 3) throw new InvalidOperationException("Encoded output stalled for three seconds while only the diagnostic log was locked.");
                }
                if (recoverWorker)
                {
                    string expected = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "capture", "Rewind.CaptureWorker.exe"));
                    var matches = new List<int>();
                    foreach (var candidate in System.Diagnostics.Process.GetProcessesByName("Rewind.CaptureWorker"))
                    using (candidate)
                        try { if (string.Equals(candidate.MainModule?.FileName, expected, StringComparison.OrdinalIgnoreCase)) matches.Add(candidate.Id); }
                        catch (System.ComponentModel.Win32Exception) { }
                    if (matches.Count != 1) throw new InvalidOperationException("Recovery fixture must own exactly one native worker at its isolated binary path.");
                    using (var worker = System.Diagnostics.Process.GetProcessById(matches[0]))
                    {
                        if (!string.Equals(worker.MainModule?.FileName, expected, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Worker identity changed before the fault injection.");
                        worker.Kill(entireProcessTree: true); await worker.WaitForExitAsync();
                    }
                    await Task.Delay(300); long afterExit = Interlocked.Read(ref bytes);
                    DateTime recoveryDeadline = DateTime.UtcNow.AddSeconds(6);
                    while (Interlocked.Read(ref bytes) <= afterExit && bridge.Error is null && DateTime.UtcNow < recoveryDeadline) await Task.Delay(100);
                    if (bridge.Error is not null || Interlocked.Read(ref bytes) <= afterExit)
                        throw new InvalidOperationException("Locked diagnostic log must not prevent native worker recovery: " + bridge.Error);
                }
            }
            long length = new FileInfo(log).Length; await Task.Delay(1500);
            if (new FileInfo(log).Length <= length) throw new InvalidOperationException("Native log must resume after the temporary file lock is released.");
        }
        finally
        {
            var stopping = System.Diagnostics.Stopwatch.StartNew(); await bridge.StopAsync(); stopMilliseconds = stopping.Elapsed.TotalMilliseconds;
            await reader; await output.FlushAsync();
            await File.WriteAllTextAsync(Path.Combine(diagnostics, recoverWorker ? "capture-log-recovery-check.json" : "capture-log-check.json"), JsonSerializer.Serialize(new
            { root, stream, bytes, deltas, stopMilliseconds, warnings, recoverWorker, fullDecodeRequired = !recoverWorker, error = bridge.Error, scope = "Temporary exclusive write denial on own native diagnostic log; stream output must continue and logging must recover." }));
        }
        if (bridge.Error is not null || warnings.Count != 1 || bridge.LogWarning is null)
            throw new InvalidOperationException("Logging failure must warn once and leave capture healthy: " + bridge.Error);
        if (!recoverWorker) await CaptureProcesses.RunAsync(CaptureProcesses.Ffmpeg, ["-v", "error", "-nostdin", "-threads", "2", "-i", stream, "-map", "0:v:0", "-f", "null", "-"], TimeSpan.FromSeconds(20));
    }


    public static async Task CheckWorkerRecoveryAsync(string diagnostics)
    {
        CaptureMonitor monitor = MonitorCatalog.Read().First(item => item.Primary);
        await CheckMonitorRebindAsync(diagnostics, monitor);
        string root = Path.Combine(diagnostics, "capture-recovery-" + Guid.NewGuid().ToString("N"));
        var settings = new CaptureSettings(monitor.DeviceName, 60, 20, 8, root,
            CaptureSystemAudio: true, CaptureMicrophone: true, SeparateDiscord: true,
            MonitorProfiles: [new MonitorRecordingProfile(monitor.DeviceName, true, 60, 720, monitor.Identity)]);
        long missingUntil = 0;
        await using var capture = DesktopReplaySession.Start(settings, [monitor], null, null, () =>
            System.Diagnostics.Stopwatch.GetTimestamp() < Volatile.Read(ref missingUntil) ? [] : MonitorCatalog.Read());
        DateTime ready = DateTime.UtcNow.AddSeconds(20);
        while (capture.Status.AvailableSeconds < 6 && DateTime.UtcNow < ready)
        {
            if (capture.Status.Error is string error) throw new InvalidOperationException(error);
            await Task.Delay(100);
        }
        if (capture.Status.AvailableSeconds < 6) throw new TimeoutException("Не накоплена исходная история для проверки восстановления.");
        string expected = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "capture", "Rewind.CaptureWorker.exe"));
        var matches = new List<int>();
        foreach (var candidate in System.Diagnostics.Process.GetProcessesByName("Rewind.CaptureWorker"))
        using (candidate)
            try { if (string.Equals(candidate.MainModule?.FileName, expected, StringComparison.OrdinalIgnoreCase)) matches.Add(candidate.Id); }
            catch (System.ComponentModel.Win32Exception) { }
        if (matches.Count != 1) throw new InvalidOperationException("Лаборатория должна владеть ровно одним worker по своему изолированному пути.");
        ReplayStatus before = capture.Status;
        using (var worker = System.Diagnostics.Process.GetProcessById(matches[0]))
        {
            if (!string.Equals(worker.MainModule?.FileName, expected, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Перед аварией изменилась принадлежность процесса.");
            Volatile.Write(ref missingUntil, System.Diagnostics.Stopwatch.GetTimestamp() + 4 * System.Diagnostics.Stopwatch.Frequency);
            worker.Kill(entireProcessTree: true); await worker.WaitForExitAsync();
        }
        await Task.Delay(2000);
        if (capture.Status is not { Running: true, Recovering: true, Error: null })
            throw new InvalidOperationException("При временной потере монитора сеанс должен оставаться в режиме восстановления.");
        DateTime recovered = DateTime.UtcNow.AddSeconds(12);
        while ((capture.Status.Recovering || capture.Status.AvailableSeconds < before.AvailableSeconds + 6) && DateTime.UtcNow < recovered)
        {
            if (capture.Status.Error is string error) throw new InvalidOperationException(error);
            await Task.Delay(100);
        }
        if (capture.Status.Recovering || capture.Status.AvailableSeconds < before.AvailableSeconds + 6)
            throw new TimeoutException("После аварии не возобновилось закрытие сегментов.");
        SavedReplay saved = await capture.SaveAsync(12);
        string json = await CaptureProcesses.RunAsync(CaptureProcesses.Ffprobe,
            ["-v", "error", "-show_streams", "-show_packets", "-show_entries", "stream=index,codec_type:packet=stream_index,pts_time", "-of", "json", saved.File], TimeSpan.FromSeconds(15));
        using JsonDocument probe = JsonDocument.Parse(json);
        var clocks = new List<object>(); var tails = new List<double>();
        foreach (JsonElement stream in probe.RootElement.GetProperty("streams").EnumerateArray())
        {
            int index = stream.GetProperty("index").GetInt32();
            bool video = stream.GetProperty("codec_type").GetString() == "video";
            double[] pts = probe.RootElement.GetProperty("packets").EnumerateArray().Where(packet => packet.GetProperty("stream_index").GetInt32() == index)
                .Select(packet => double.Parse(packet.GetProperty("pts_time").GetString()!, CultureInfo.InvariantCulture)).ToArray();
            if (pts.Length < 2) throw new InvalidDataException("После аварии пропала дорожка повтора.");
            double[] steps = pts.Skip(1).Select((point, i) => point - pts[i]).ToArray();
            if (steps.Any(step => step <= 0) || (video ? steps.Max() is < 3 or > 8 : steps.Any(step => Math.Abs(step - 1024d / 48000) > .000002)))
                throw new InvalidDataException("После аварии нарушена шкала времени дорожки " + index);
            clocks.Add(new { index, video, count = pts.Length, first = pts[0], last = pts[^1], maximumGap = steps.Max() }); tails.Add(pts[^1]);
        }
        if (clocks.Count != 4 || tails.Max() - tails.Min() > .15) throw new InvalidDataException("После аварии потеряны дорожки либо разошлись их окончания.");
        await CaptureProcesses.RunAsync(CaptureProcesses.Ffmpeg,
            ["-v", "error", "-xerror", "-err_detect", "explode", "-nostdin", "-threads", "2", "-i", saved.File, "-map", "0", "-f", "null", "-"], TimeSpan.FromSeconds(30));
        await File.WriteAllTextAsync(Path.Combine(diagnostics, "capture-recovery-check.json"), JsonSerializer.Serialize(new
        { root, before, after = capture.Status, saved, clocks, allStreamsStrictDecode = true,
            scope = "Изолированный worker остановлен, монитор недоступен четыре секунды. MP4 с тремя аудиодорожками сохранён из того же буфера. Временная шкала сохраняет разрыв видео." }, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static async Task CheckMonitorRebindAsync(string diagnostics, CaptureMonitor monitor)
    {
        string root = Path.Combine(diagnostics, "capture-rebind-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var profile = new MonitorRecordingProfile(monitor.DeviceName, true, 30, 720, monitor.Identity);
        long epoch = (long)(System.Diagnostics.Stopwatch.GetTimestamp() * (10_000_000.0 / System.Diagnostics.Stopwatch.Frequency));
        // Устаревший номер выхода имитирует изменение топологии при обновлении драйвера.
        var outage = System.Diagnostics.Stopwatch.StartNew();
        await using var bridge = new NativeCaptureBridge(monitor with { OutputIndex = 10000 }, profile, epoch, Path.Combine(root, "native.log"),
            readMonitors: () => outage.Elapsed < TimeSpan.FromSeconds(3) ? [] : MonitorCatalog.Read());
        await using var client = new System.IO.Pipes.NamedPipeClientStream(".", Path.GetFileName(bridge.Path), System.IO.Pipes.PipeDirection.In, System.IO.Pipes.PipeOptions.Asynchronous);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        await client.ConnectAsync(deadline.Token);
        byte[] packet = new byte[188];
        Task firstPacket = client.ReadExactlyAsync(packet, deadline.Token).AsTask();
        await Task.Delay(TimeSpan.FromSeconds(2), deadline.Token);
        if (firstPacket.IsCompleted || !bridge.Recovering || bridge.Error is not null)
            throw new InvalidOperationException("Пока монитор недоступен, поток должен оставаться открытым для восстановления.");
        await firstPacket;
        if (packet[0] != 0x47) throw new InvalidDataException("После повторного поиска монитора не получен пакет MPEG-TS.");
        Task drain = client.CopyToAsync(Stream.Null, deadline.Token);
        await bridge.StopAsync();
        await drain;
    }

    public static async Task RunAsync(string diagnostics)
    {
        CaptureMonitor[] monitors = MonitorCatalog.Read().OrderByDescending(monitor => monitor.Primary).Take(2).ToArray();
        if (monitors.Length < 2) throw new InvalidOperationException("Для проверки независимого темпа нужны два экрана.");
        string root = Path.Combine(diagnostics, "capture-cadence-" + Guid.NewGuid().ToString("N"));
        var settings = new CaptureSettings(monitors[0].DeviceName, 120, 12, 3, root,
            CaptureSystemAudio: true, CaptureMicrophone: true, SeparateDiscord: true,
            MonitorProfiles: monitors.Select((monitor, index) => new MonitorRecordingProfile(monitor.DeviceName, true, index == 0 ? 120 : 30, 0, monitor.Identity)).ToArray());
        await using var capture = DesktopReplaySession.Start(settings, monitors);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while (capture.Status.AvailableSeconds < 6)
        {
            if (capture.Status.Error is string error) throw new InvalidOperationException(error);
            await Task.Delay(100, deadline.Token);
        }
        ReplayStatus status = capture.Status;
        SavedReplay[] saved = await capture.SaveScreensAsync();
        if (saved.Length != 2 || saved.Select(replay => replay.RequestedEndSeconds).Distinct().Count() != 1 || saved.Any(replay => replay.AudioSources?.Length != 3))
            throw new InvalidOperationException("Независимые графы должны сохранять оба экрана с одним моментом конца и тремя дорожками.");
        foreach (SavedReplay replay in saved)
        {
            int expectedFps = replay.MonitorDeviceName == monitors[0].DeviceName ? 120 : 30;
            string packetJson = await CaptureProcesses.RunAsync(CaptureProcesses.Ffprobe,
                ["-v", "error", "-show_streams", "-show_packets", "-show_entries", "stream=index,codec_type,time_base,avg_frame_rate:packet=stream_index,pts", "-of", "json", replay.File], TimeSpan.FromSeconds(10));
            using JsonDocument packets = JsonDocument.Parse(packetJson);
            foreach (JsonElement stream in packets.RootElement.GetProperty("streams").EnumerateArray())
            {
                int index = stream.GetProperty("index").GetInt32();
                long[] pts = packets.RootElement.GetProperty("packets").EnumerateArray().Where(packet => packet.GetProperty("stream_index").GetInt32() == index)
                    .Select(packet => packet.GetProperty("pts").GetInt64()).ToArray();
                string[] scale = stream.GetProperty("time_base").GetString()!.Split('/');
                double unit = double.Parse(scale[0], CultureInfo.InvariantCulture) / double.Parse(scale[1], CultureInfo.InvariantCulture);
                bool video = stream.GetProperty("codec_type").GetString() == "video";
                double step = video ? 1d / expectedFps : 1024d / 48000;
                for (int packet = 1; packet < pts.Length; packet++)
                    if (Math.Abs((pts[packet] - pts[packet - 1]) * unit - step) > .0000001)
                        throw new InvalidOperationException($"Живая запись должна сохранять точную сетку {(video ? expectedFps + " FPS" : "AAC")}: экран {replay.MonitorDeviceName}, поток {index}, пакет {packet}.");
                if (video && stream.GetProperty("avg_frame_rate").GetString() != expectedFps + "/1")
                    throw new InvalidOperationException("Частота живой записи должна быть ровно " + expectedFps + "/1.");
            }
            await CaptureProcesses.RunAsync(CaptureProcesses.Ffmpeg, ["-v", "error", "-nostdin", "-threads", "2", "-i", replay.File, "-map", "0", "-fps_mode", "passthrough", "-enc_time_base", "demux", "-f", "null", "-"], TimeSpan.FromSeconds(20));
        }
        await File.WriteAllTextAsync(Path.Combine(diagnostics, "capture-cadence.json"), JsonSerializer.Serialize(new
        { root, status, saved, exactPacketClocks = true, fullDecode = true,
            fps120Acceptance = "NOT_VERIFIED: saved-frame identities must be checked by CaptureLab",
            scope = "Live mixed 120/30 capture with three audio sources: exact packet grids and full decode. New-image frequency is a separate acceptance criterion." }, new JsonSerializerOptions { WriteIndented = true }));
    }
}
