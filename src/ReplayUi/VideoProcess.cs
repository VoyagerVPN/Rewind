using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using System.Collections.Concurrent;

namespace Rewind.Preview;

internal sealed record VideoCommand(string Kind, string? File = null, long Handle = 0, long Time = 0, bool Headless = false, bool Paused = false, bool Software = false, bool AllowDamaged = false, long Id = 0, [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] VideoProbeProof? Validation = null);
public sealed record VideoPlaybackStatistics(int? DecodedFrames, int? DisplayedFrames, int? LostFrames, bool SoftwareForced);
internal sealed record VideoMessage(string Kind, long Time = 0, uint Vout = 0, string? Error = null, string? Details = null, VideoPlaybackStatistics? Statistics = null, long Id = 0, bool DamageConfirmed = false, VideoProbeProof? Validation = null);
internal sealed class VideoTimeEventArgs(long time) : EventArgs { public long Time { get; } = time; }

// Здесь нет ссылок на LibVLC: повреждение нативного декодера ограничено дочерним процессом.
internal sealed class VideoProcess
{
    private sealed class Connection(Process process, NamedPipeServerStream pipe, string file, long time)
    {
        public Process Process { get; } = process;
        public string File { get; } = file;
        public long RequestedTime { get; } = time;
        public NamedPipeServerStream Pipe { get; } = pipe;
        public CancellationTokenSource Cancellation { get; } = new();
        public Channel<VideoCommand> Commands { get; } = Channel.CreateUnbounded<VideoCommand>(new() { SingleReader = true });
        public TaskCompletionSource Opened { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public long LastMessage = Stopwatch.GetTimestamp();
        public int Failed;
        public ConcurrentDictionary<long, TaskCompletionSource<long>> Pending { get; } = new();
    }
    private Connection? _connection;
    private long _nextRequest;
    private long _time;
    private uint _vout;
    private VideoPlaybackStatistics? _statistics;
    public VideoPlaybackStatistics? Statistics => Volatile.Read(ref _statistics);
    public int? ProcessId => _connection?.Process.Id;
    public long Time { get => Interlocked.Read(ref _time); set => Send(new("seek", Time: value)); }
    public uint VoutCount => _vout;
    public event EventHandler<EventArgs>? Playing, EndReached, EncounteredError;
    public event EventHandler<VideoTimeEventArgs>? TimeChanged;
    public string? Error { get; private set; }
    public string? ErrorDetails { get; private set; }
    public string? OverlayCompatibility { get; private set; }
    public bool FailureMayIndicateDamage { get; private set; }

    public async Task OpenAsync(string file, nint handle, long time, bool headless, CancellationToken cancellation, bool software = false, bool allowDamaged = false)
    {
        string name = "rewind-video-" + Guid.NewGuid().ToString("N");
        var pipe = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        if (string.Equals(Path.GetFileNameWithoutExtension(Environment.ProcessPath), "dotnet", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(typeof(VideoProcess).Assembly.Location);
        start.ArgumentList.Add("--video-worker"); start.ArgumentList.Add(name); start.ArgumentList.Add(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Process process;
        try { process = Process.Start(start) ?? throw new InvalidOperationException("Не удалось запустить видеоплеер."); }
        catch { pipe.Dispose(); throw; }
        var connection = new Connection(process, pipe, file, time); _connection = connection;
        Interlocked.Exchange(ref _time, time); _vout = 0; Volatile.Write(ref _statistics, null); Error = ErrorDetails = OverlayCompatibility = null; FailureMayIndicateDamage = false;
        _ = DrainAsync(process.StandardOutput); _ = DrainAsync(process.StandardError);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            await pipe.WaitForConnectionAsync(deadline.Token);
            _ = ReadAsync(connection); _ = WriteAsync(connection); _ = WatchAsync(connection);
            connection.Commands.Writer.TryWrite(new("open", file, handle.ToInt64(), time, headless, Software: software, AllowDamaged: allowDamaged, Validation: allowDamaged ? null : VideoProbeCache.Candidate(file, time / 1000d)));
            await connection.Opened.Task.WaitAsync(deadline.Token);
        }
        catch (Exception error)
        {
            await StopAsync();
            cancellation.ThrowIfCancellationRequested();
            throw new InvalidOperationException(Error ?? "Видеоплеер не завершил открытие повтора. Попробуй открыть его ещё раз.", error);
        }
    }

    public void SetPause(bool paused) => Send(new("pause", Paused: paused));
    public async Task SeekAsync(long time, CancellationToken cancellation = default)
        => await RequestAsync(new("seek", Time: time), cancellation);
    public async Task SetPauseAsync(bool paused, CancellationToken cancellation = default)
        => await RequestAsync(new("pause", Paused: paused), cancellation);
    private async Task RequestAsync(VideoCommand command, CancellationToken cancellation)
    {
        Connection connection = _connection ?? throw new IOException("Видеоплеер завершился.");
        long id = Interlocked.Increment(ref _nextRequest);
        var completion = new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.Pending[id] = completion;
        try
        {
            if (connection.Failed != 0 || !connection.Commands.Writer.TryWrite(command with { Id = id })) throw new IOException(Error ?? "Видеоплеер не отвечает.");
            await completion.Task.WaitAsync(TimeSpan.FromSeconds(8), cancellation);
        }
        finally { connection.Pending.TryRemove(id, out _); }
    }
    public async Task WaitUntilReadyAsync(CancellationToken cancellation)
    {
        Connection connection = _connection ?? throw new IOException("Видеоплеер завершился.");
        await connection.Started.Task.WaitAsync(cancellation);
        if (connection.Failed != 0) throw new InvalidOperationException(Error ?? "Видеоплеер завершился с ошибкой.");
    }
    private void Send(VideoCommand command) => _connection?.Commands.Writer.TryWrite(command);
    private async Task ReadAsync(Connection connection)
    {
        try
        {
            using var reader = new StreamReader(connection.Pipe, leaveOpen: true);
            while (await reader.ReadLineAsync(connection.Cancellation.Token) is string line)
            {
                VideoMessage message = JsonSerializer.Deserialize<VideoMessage>(line) ?? throw new IOException("Пустой ответ видеоплеера.");
                Interlocked.Exchange(ref connection.LastMessage, Stopwatch.GetTimestamp());
                // Закрываемый процесс ещё может дописывать ответы: читаем их без публикации в UI.
                if (!ReferenceEquals(_connection, connection)) continue;
                long previousTime = Interlocked.Read(ref _time);
                if (message.Statistics is not null) Volatile.Write(ref _statistics, message.Statistics);
                if (message.Kind is "state" or "playing") { Interlocked.Exchange(ref _time, message.Time); _vout = message.Vout; }
                switch (message.Kind)
                {
                    case "overlay-compatibility": OverlayCompatibility = message.Details; break;
                    case "ready":
                        Interlocked.Exchange(ref _time, message.Time);
                        if (connection.Pending.TryGetValue(message.Id, out TaskCompletionSource<long>? request)) request.TrySetResult(message.Time);
                        break;
                    case "opened":
                        VideoProbeCache.Remember(connection.File, connection.RequestedTime / 1000d, message.Validation);
                        connection.Opened.TrySetResult(); break;
                    case "playing": Playing?.Invoke(this, EventArgs.Empty); connection.Started.TrySetResult(); break;
                    case "state": if (message.Time != previousTime) TimeChanged?.Invoke(this, new(message.Time)); break;
                    case "ended": EndReached?.Invoke(this, EventArgs.Empty); break;
                    case "error": ErrorDetails = message.Details; Fail(connection, message.Error ?? "Не удалось воспроизвести видео.", rememberPosition: message.DamageConfirmed); return;
                }
            }
            Fail(connection, "Видеоплеер завершился с ошибкой. Файл может быть повреждён. Можно открыть другой повтор.");
        }
        catch (Exception error) when (error is IOException or OperationCanceledException or ObjectDisposedException or JsonException)
        { Fail(connection, "Связь с видеоплеером потеряна. Можно открыть другой повтор."); }
    }
    private async Task WriteAsync(Connection connection)
    {
        try
        {
            using var writer = new StreamWriter(connection.Pipe, leaveOpen: true) { AutoFlush = true };
            await foreach (VideoCommand command in connection.Commands.Reader.ReadAllAsync(connection.Cancellation.Token))
                await writer.WriteLineAsync(JsonSerializer.Serialize(command).AsMemory(), connection.Cancellation.Token);
        }
        catch (Exception error) when (error is IOException or OperationCanceledException or ObjectDisposedException)
        { Fail(connection, "Видеоплеер не отвечает. Можно открыть другой повтор."); }
    }
    private async Task WatchAsync(Connection connection)
    {
        try
        {
            while (!connection.Cancellation.IsCancellationRequested)
            {
                await Task.Delay(250, connection.Cancellation.Token);
                if (!ReferenceEquals(_connection, connection)) return;
                bool exited = connection.Process.HasExited;
                if (exited || Stopwatch.GetElapsedTime(Interlocked.Read(ref connection.LastMessage)).TotalSeconds > 5)
                { Fail(connection, "Видеоплеер не отвечает. Можно открыть другой повтор.", rememberPosition: exited); return; }
            }
        }
        catch (OperationCanceledException) { }
    }
    private void Fail(Connection connection, string message, bool rememberPosition = true)
    {
        if (!ReferenceEquals(_connection, connection) || Interlocked.Exchange(ref connection.Failed, 1) != 0) return;
        Error = message;
        FailureMayIndicateDamage = rememberPosition;
        foreach (TaskCompletionSource<long> request in connection.Pending.Values) request.TrySetException(new IOException(message));
        connection.Started.TrySetResult();
        if (!connection.Opened.Task.IsCompletedSuccessfully) connection.Opened.TrySetException(new InvalidOperationException(message));
        else EncounteredError?.Invoke(this, EventArgs.Empty);
    }
    public async Task StopAsync()
    {
        Connection? connection = Interlocked.Exchange(ref _connection, null);
        if (connection is null) return;
        var closeTimer = Stopwatch.StartNew();
        void Trace(string stage)
        {
            string? folder = Environment.GetEnvironmentVariable("REWIND_VIDEO_TRACE_FOLDER");
            if (folder is null) return;
            try { File.AppendAllText(Path.Combine(folder, $"video-close-{connection.Process.Id}.jsonl"), JsonSerializer.Serialize(new { stage, milliseconds = closeTimer.Elapsed.TotalMilliseconds }) + "\n"); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
        Trace("start");
        try
        {
            connection.Commands.Writer.TryWrite(new("close")); connection.Commands.Writer.TryComplete();
            Trace("close-queued");
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            // Дочерний HWND связывает очереди ввода: зависший worker может задержать dispatcher.
            // Watchdog должен завершать процесс независимо от доступности UI-потока.
            try { await connection.Process.WaitForExitAsync(deadline.Token).ConfigureAwait(false); }
            catch (OperationCanceledException)
            {
                Trace("watchdog");
                try { if (!connection.Process.HasExited) connection.Process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) { }
                Trace("killed");
                await connection.Process.WaitForExitAsync().ConfigureAwait(false);
            }
            Trace("exited");
        }
        finally
        {
            foreach (TaskCompletionSource<long> request in connection.Pending.Values) request.TrySetCanceled();
            connection.Cancellation.Cancel(); Trace("cancelled");
            connection.Pipe.Dispose(); Trace("pipe-disposed");
            connection.Process.Dispose();
            _vout = 0;
        }
    }
    private static async Task DrainAsync(StreamReader reader)
    {
        try { char[] buffer = new char[2048]; while (await reader.ReadAsync(buffer) > 0) { } }
        catch (Exception error) when (error is IOException or ObjectDisposedException) { }
    }
}
