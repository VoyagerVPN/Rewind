using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace Rewind.Preview;

// Через pipe проходят только сжатые пакеты с исходными временными метками.
internal sealed class NativeCaptureBridge : IAsyncDisposable
{
    private static readonly TimeSpan RecoveryLimit = TimeSpan.FromMinutes(1);
    private readonly NamedPipeServerStream _pipe;
    private readonly CancellationTokenSource _cancel = new();
    private readonly Task _task;
    private readonly object _gate = new();
    private Process? _process;
    private volatile bool _stopping;
    private volatile bool _recovering;
    private CaptureMonitor _monitor;
    private string? _error, _logWarning;
    private readonly Action<string>? _diagnosticWarning;
    private int _disposed;
    private double _firstFrameSeconds = double.NaN;
    public string Path { get; }
    public string? Error => Volatile.Read(ref _error);
    public bool Recovering => _recovering;
    public CaptureMonitor Monitor => Volatile.Read(ref _monitor);
    public string? LogWarning => Volatile.Read(ref _logWarning);
    public double FirstFrameSeconds => Volatile.Read(ref _firstFrameSeconds);

    public NativeCaptureBridge(CaptureMonitor monitor, MonitorRecordingProfile profile, long epoch, string log,
        Action<string>? diagnosticWarning = null, Func<IReadOnlyList<CaptureMonitor>>? readMonitors = null)
    {
        _diagnosticWarning = diagnosticWarning;
        _monitor = monitor;
        readMonitors ??= MonitorCatalog.Read;
        string name = "rewind-video-capture-" + Guid.NewGuid().ToString("N");
        Path = @"\\.\pipe\" + name;
        _pipe = new NamedPipeServerStream(name, PipeDirection.Out, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 4096, 1024 * 1024);
        _task = Task.Run(async () =>
        {
            try
            {
                await _pipe.WaitForConnectionAsync(_cancel.Token);
                (int width, int height) = profile.OutputSize(monitor.Width, monitor.Height);
                CaptureMonitor current = monitor;
                Stopwatch? outage = null;
                int restarts = 0;
                while (!_stopping)
                {
                    if (outage is not null)
                    {
                        int delay = Math.Min(5000, 150 * (1 << Math.Min(restarts, 6)));
                        TimeSpan remaining = RecoveryLimit - outage.Elapsed;
                        if (remaining <= TimeSpan.FromMilliseconds(delay))
                        {
                            if (remaining > TimeSpan.Zero) await Task.Delay(remaining, _cancel.Token);
                            if (_stopping) break;
                            throw new IOException("Не удалось восстановить видеозахват " + monitor.Label + ". Подробности: " + log);
                        }
                        await Task.Delay(delay, _cancel.Token);
                        if (_stopping) break;
                        await WriteLogAsync(log, $"restart={++restarts}");
                        // При переустановке драйвера порядковые номера DXGI могут измениться.
                        try
                        {
                            CaptureMonitor? match = readMonitors().SingleOrDefault(item =>
                                string.Equals(item.Identity, monitor.Identity, StringComparison.OrdinalIgnoreCase));
                            if (match is null) { await WriteLogAsync(log, "monitor-not-found: " + monitor.Label); continue; }
                            current = match;
                        }
                        catch (Exception error) when (error is ExternalException or InvalidOperationException)
                        { await WriteLogAsync(log, "monitor-discovery-error: " + error.Message); continue; }
                    }
                    long launched = Stopwatch.GetTimestamp();
                    using Process process = CaptureProcesses.Start(System.IO.Path.Combine(AppContext.BaseDirectory, "capture", "Rewind.CaptureWorker.exe"),
                        [current.AdapterIndex.ToString(), current.OutputIndex.ToString(), profile.Fps.ToString(), width.ToString(), height.ToString(),
                        Environment.ProcessId.ToString(), epoch.ToString(CultureInfo.InvariantCulture)]);
                    lock (_gate) { _process = process; if (_stopping) process.StandardInput.Close(); }
                    Task errors = ReadErrorsAsync(process, log);
                    try
                    {
                        Stream video = process.StandardOutput.BaseStream;
                        if (outage is null || await ResumePipeAsync(process, video, current, RecoveryLimit - outage.Elapsed))
                            await video.CopyToAsync(_pipe, 256 * 1024, _cancel.Token);
                        else if (!process.HasExited)
                            process.Kill(entireProcessTree: true);
                        await process.WaitForExitAsync(_cancel.Token);
                        await errors;
                    }
                    finally
                    {
                        lock (_gate) _process = null;
                        if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); }
                        await errors;
                    }
                    if (_stopping) break;
                    if (outage is null || !_recovering && Stopwatch.GetElapsedTime(launched) >= TimeSpan.FromSeconds(10))
                    { outage = Stopwatch.StartNew(); restarts = 0; }
                    _recovering = true;
                    await WriteLogAsync(log, $"worker-exit={process.ExitCode}");
                }
            }
            catch (Exception error) when (_stopping && error is OperationCanceledException or IOException or ObjectDisposedException) { }
            catch (Exception error) { Volatile.Write(ref _error, "Ошибка видеозахвата: " + error.Message); }
            finally { _pipe.Dispose(); }
        });
    }

    private async Task<bool> ResumePipeAsync(Process process, Stream video, CaptureMonitor monitor, TimeSpan remaining)
    {
        if (remaining <= TimeSpan.Zero) return false;
        byte[] first = new byte[188 * 32];
        Task<int> read = video.ReadAsync(first, _cancel.Token).AsTask();
        int bytes;
        try { bytes = await read.WaitAsync(TimeSpan.FromSeconds(Math.Min(8, remaining.TotalSeconds)), _cancel.Token); }
        catch (TimeoutException)
        {
            // Чтение stdout может ждать драйвер. Завершение worker разблокирует его даже без отмены I/O.
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await read;
            return false;
        }
        if (bytes == 0) return false;
        await _pipe.WriteAsync(first.AsMemory(0, bytes), _cancel.Token);
        Volatile.Write(ref _monitor, monitor);
        _recovering = false;
        return true;
    }

    private async Task ReadErrorsAsync(Process process, string log)
    {
        while (await process.StandardError.ReadLineAsync() is string line)
        {
            if (line.StartsWith("{\"capture\":\"started\"", StringComparison.Ordinal) && double.IsNaN(FirstFrameSeconds))
            {
                using JsonDocument started = JsonDocument.Parse(line);
                Volatile.Write(ref _firstFrameSeconds, started.RootElement.GetProperty("seconds").GetDouble());
            }
            if (line.Length > 2000) line = line[..2000];
            await WriteLogAsync(log, line);
        }
    }

    private async Task WriteLogAsync(string log, string line)
    {
        // Даже при отказе диска продолжаем дренировать stderr, иначе worker блокируется на fprintf.
        try
        {
            if (File.Exists(log) && new FileInfo(log).Length > 2 * 1024 * 1024) File.Move(log, log + ".previous", overwrite: true);
            await File.AppendAllTextAsync(log, line + "\n");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            string warning = "Не удалось записать диагностику видеозахвата: " + log + ": " + error.Message;
            if (Interlocked.CompareExchange(ref _logWarning, warning, null) is null)
                try { _diagnosticWarning?.Invoke(warning); }
                catch (Exception) { /* Ошибка уведомления тоже не должна останавливать видеопоток. */ }
        }
    }

    public async Task StopAsync()
    {
        _stopping = true;
        bool cancelUnstarted;
        lock (_gate)
        {
            try { _process?.StandardInput.Close(); } catch (IOException) { }
            cancelUnstarted = _process is null && !_task.IsCompleted;
        }
        // До запуска worker нет кадров для сброса: сразу отменяем ожидание pipe.
        if (cancelUnstarted) _cancel.Cancel();
        try { await _task.WaitAsync(TimeSpan.FromSeconds(4)); }
        catch (TimeoutException)
        {
            _cancel.Cancel(); _pipe.Dispose();
            lock (_gate) { if (_process is { HasExited: false } process) process.Kill(entireProcessTree: true); }
            await _task;
        }
    }
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        await StopAsync(); _cancel.Dispose();
    }
}
