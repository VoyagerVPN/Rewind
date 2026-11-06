using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Encodings.Web;
using System.Text.Unicode;
using Rewind.DiskBuffer;

namespace Rewind.Preview;

public sealed record ReplayStatus(bool Running, double AvailableSeconds, double LatestSeconds, long BufferBytes,
    long EncodedFrames, long DuplicatedFrames, int SavesInProgress, string? Error)
{
    public bool Recovering { get; init; }
}
public sealed record SavedReplay(string File, string Thumbnail, double Duration, int Width, int Height, double Fps,
    double RequestedEndSeconds, double RequestedSeconds, double ExtraLeadingSeconds, long Bytes, bool HasSystemAudio = false,
    string[]? AudioSources = null, string? MonitorDeviceName = null, string? EventId = null, string[]? RelatedFiles = null,
    string? ApplicationName = null, string? SessionId = null, DateTime? SessionStartedUtc = null, DateTime? SavedAtUtc = null);

public sealed class DesktopReplaySession : IAsyncDisposable
{
    private static readonly JsonSerializerOptions AudioLogJson = new() { Encoder = JavaScriptEncoder.Create(UnicodeRanges.BasicLatin, UnicodeRanges.Cyrillic) };
    private readonly object _gate = new();
    private readonly CaptureSettings _settings;
    private readonly Process _capture;
    private readonly SystemAudioPipe? _audio;
    private readonly NativeCaptureBridge[] _native;
    private readonly long _epoch;
    private sealed class Track(CaptureSettings settings, CaptureMonitor monitor, MonitorRecordingProfile profile, string root)
    {
        public readonly CaptureMonitor Monitor = monitor;
        public readonly MonitorRecordingProfile Profile = profile;
        public readonly string Root = root;
        public readonly string Manifest = Path.Combine(root, "segments.csv");
        public readonly DiskReplayBuffer Buffer = new(root, TimeSpan.FromSeconds(settings.HistorySeconds + 10), continuousTimeline: true);
        public int LastSequence = -1;
        public double ClosedEnd;
    }
    private readonly Track[] _tracks;
    private readonly string _sessionRoot;
    private readonly long _maximumBufferBytes;
    private readonly Queue<string> _errors = new();
    private readonly List<Task> _saves = [];
    private readonly Task _outputReader;
    private readonly Task _errorReader;
    private readonly Task _watcher;
    private readonly SemaphoreSlim _stopGate = new(1);
    private volatile bool _stopRequested;
    private bool _stopped;
    private string? _error;
    private double _mediaEnd;
    private double _closedEnd;
    private long _frames;
    private long _duplicates;
    private long _bufferBytes;
    private DateTime _lastAudioMetricsUtc;
    private long _lastAudioMetricEvents = -1;
    private string? _audioMetricsError;

    private DesktopReplaySession(CaptureSettings settings, IReadOnlyList<CaptureMonitor> monitors, uint? applicationProcessId,
        Action<string>? diagnosticWarning, Func<IReadOnlyList<CaptureMonitor>>? readMonitors)
    {
        _settings = settings;
        string id = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..8];
        _sessionRoot = Path.Combine(Path.GetFullPath(settings.StorageRoot), "Buffer", id);
        Directory.CreateDirectory(_sessionRoot);
        Directory.CreateDirectory(Path.Combine(settings.StorageRoot, "Clips"));
        long free = new DriveInfo(Path.GetPathRoot(_sessionRoot)!).AvailableFreeSpace;
        if (free < 1024L * 1024 * 1024) throw new IOException("Для записи доступно менее 1 ГиБ свободного места на диске.");
        _maximumBufferBytes = Math.Min(16L * 1024 * 1024 * 1024, free - 512L * 1024 * 1024);
        _tracks = settings.ResolveMonitorProfiles().Where(profile => profile.Enabled).Select((profile, index) =>
        {
            string root = Path.Combine(_sessionRoot, "screen-" + index); Directory.CreateDirectory(root);
            return new Track(settings, monitors.Single(monitor => profile.Identity is null ? monitor.DeviceName == profile.DeviceName : monitor.Identity == profile.Identity), profile, root);
        }).ToArray();
        long epoch = _epoch = (long)(Stopwatch.GetTimestamp() * (10_000_000.0 / Stopwatch.Frequency));
        _audio = settings.CaptureSystemAudio || settings.SeparateDiscord || settings.CaptureMicrophone ? new SystemAudioPipe(settings, applicationProcessId, epochQpc100ns: epoch) : null;
        _native = _tracks.Select((track, index) => new NativeCaptureBridge(track.Monitor, track.Profile, epoch,
            Path.Combine(_sessionRoot, $"native-{index}.log"), diagnosticWarning, readMonitors)).ToArray();
        var arguments = new List<string> { "-hide_banner", "-loglevel", "warning", "-nostats", "-stats_period", "0.25", "-progress", "pipe:1", "-copyts" };
        if (_audio is not null) arguments.AddRange(["-thread_queue_size", "64", "-probesize", "32", "-analyzeduration", "1", "-f", "f32le", "-ar", "48000", "-ac", "6", "-channel_layout", "5.1", "-i", _audio.Path]);
        var filters = new List<string>();
        foreach (var (track, index) in _tracks.Select((track, index) => (track, index)))
        {
            // Ноль включает автоматический анализ FFmpeg до 7 с, переполняя PCM при тихом видеопотоке.
            arguments.AddRange(["-thread_queue_size", "256", "-probesize", "4096", "-analyzeduration", "1", "-fpsprobesize", "0", "-f", "mpegts", "-i", _native[index].Path]);
        }
        if (_audio is not null)
        {
            string[] allSources = ["system", "discord", "microphone"];
            filters.Add("[0:a]asplit=" + (_audio.Sources.Length * _tracks.Length) + string.Concat(_tracks.SelectMany((_, index) => _audio.Sources.Select(source => $"[raw_{source}{index}]"))));
            foreach (int index in Enumerable.Range(0, _tracks.Length))
            foreach (string source in _audio.Sources)
            {
                int channel = Array.IndexOf(allSources, source) * 2;
                filters.Add($"[raw_{source}{index}]pan=stereo|c0=c{channel}|c1=c{channel + 1}[{source}{index}]");
            }
        }
        if (filters.Count > 0) arguments.AddRange(["-filter_complex", string.Join(';', filters)]);
        foreach (var (track, index) in _tracks.Select((track, index) => (track, index)))
        {
        arguments.AddRange([
            "-map", $"{index + (_audio is null ? 0 : 1)}:v:0", "-c:v", "copy",
            ]);
        if (_audio is not null)
        {
            foreach (var (source, audioIndex) in _audio.Sources.Select((source, audioIndex) => (source, audioIndex)))
                arguments.AddRange(["-map", $"[{source}{index}]", $"-metadata:s:a:{audioIndex}", "title=" + source, $"-metadata:s:a:{audioIndex}", "handler_name=" + source]);
            arguments.AddRange(["-c:a", "aac", "-b:a", "192k", "-ar", "48000"]);
        }
        else arguments.Add("-an");
        arguments.AddRange(ReplayExport.SegmentArguments(track.Root, track.Manifest, settings.HistorySeconds / 2 + 24));
        }
        try { _capture = CaptureProcesses.Start(CaptureProcesses.Ffmpeg, arguments); }
        catch { foreach (NativeCaptureBridge native in _native) native.DisposeAsync().AsTask().GetAwaiter().GetResult(); if (_audio is not null) _audio.DisposeAsync().AsTask().GetAwaiter().GetResult(); throw; }
        _outputReader = ReadProgressAsync();
        _errorReader = ReadErrorsAsync();
        _watcher = WatchAsync();
    }

    public static DesktopReplaySession Start(CaptureSettings settings, CaptureMonitor monitor, uint? applicationProcessId = null, Action<string>? diagnosticWarning = null)
    {
        settings.Validate();
        if (!settings.CaptureVideo) throw new ArgumentException("Запись экрана отключена в настройках.");
        if (monitor.DeviceName != settings.MonitorDeviceName || settings.MonitorIdentity is string identity && monitor.Identity != identity)
            throw new ArgumentException("Выбранный монитор больше не подключён.");
        return Start(settings, new[] { monitor }, applicationProcessId, diagnosticWarning);
    }

    public static DesktopReplaySession Start(CaptureSettings settings, IReadOnlyList<CaptureMonitor> monitors, uint? applicationProcessId = null, Action<string>? diagnosticWarning = null) =>
        Start(settings, monitors, applicationProcessId, diagnosticWarning, null);

    internal static DesktopReplaySession Start(CaptureSettings settings, IReadOnlyList<CaptureMonitor> monitors, uint? applicationProcessId,
        Action<string>? diagnosticWarning, Func<IReadOnlyList<CaptureMonitor>>? readMonitors)
    {
        settings.Validate();
        MonitorRecordingProfile[] profiles = settings.ResolveMonitorProfiles().Where(profile => profile.Enabled).ToArray();
        if (profiles.Length == 0) throw new ArgumentException("Нет экранов с включённой записью.");
        CaptureMonitor[] selected = profiles.Select(profile => monitors.SingleOrDefault(monitor => profile.Identity is null ? monitor.DeviceName == profile.DeviceName : monitor.Identity == profile.Identity)
            ?? throw new ArgumentException("Монитор недоступен: " + profile.DeviceName)).ToArray();
        if (selected.Select(monitor => monitor.AdapterIndex).Distinct().Count() != 1)
            throw new ArgumentException("Пока поддерживаются мониторы, подключённые к одной видеокарте.");
        return new DesktopReplaySession(settings, selected, applicationProcessId, diagnosticWarning, readMonitors);
    }

    public AudioSourceStatus[] AudioStatus => _audio?.Status ?? [];
    public AudioCaptureDiagnostics? AudioDiagnostics => _audio?.Diagnostics;
    public string AudioDiagnosticsFile => Path.Combine(_sessionRoot, "audio-metrics.jsonl");
    public string AudioSummaryFile => Path.Combine(_sessionRoot, "audio-summary.txt");
    public string? AudioDiagnosticsError => _audioMetricsError;
    internal nint[] RecordingMonitorHandles => _native.Select(native => (nint)native.Monitor.Handle).ToArray();

    private async Task RecordAudioMetricsAsync(bool force = false)
    {
        if (_audio is null) return;
        AudioCaptureDiagnostics metrics = _audio.Diagnostics;
        long events = metrics.Transport.PipeWaitEvents + metrics.Transport.WakeDelayEvents +
            metrics.Sources.Sum(source => source.Buffer is null ? 0 : source.Buffer.OverflowEvents + source.Buffer.LateFrames +
                source.Buffer.OverlappingFrames + source.Reconnects + (source.InputTiming?.ClockCorrections ?? 0));
        DateTime now = DateTime.UtcNow;
        if (!force && events == _lastAudioMetricEvents && now - _lastAudioMetricsUtc < TimeSpan.FromSeconds(5)) return;
        try
        {
            // Два файла по 4 МиБ ограничивают размер журнала даже при суточной записи.
            if (File.Exists(AudioDiagnosticsFile) && new FileInfo(AudioDiagnosticsFile).Length >= 4 * 1024 * 1024)
                File.Move(AudioDiagnosticsFile, Path.Combine(_sessionRoot, "audio-metrics.previous.jsonl"), overwrite: true);
            string line = JsonSerializer.Serialize(new { utc = now, final = force, metrics }, AudioLogJson) + "\n";
            await File.AppendAllTextAsync(AudioDiagnosticsFile, line);
            await File.WriteAllTextAsync(AudioSummaryFile, $"Состояние на {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}\n" + metrics.Describe() + "\n");
            _audioMetricsError = null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { _audioMetricsError = "Не записана диагностика звука: " + error.Message; }
        _lastAudioMetricsUtc = now; _lastAudioMetricEvents = events;
    }

    public ReplayStatus Status
    {
        get
        {
            lock (_gate) return new ReplayStatus(!_stopRequested && _error is null && !_capture.HasExited,
                Math.Max(0, Math.Min(_closedEnd - RecordingStartSeconds(_tracks), _settings.HistorySeconds)), _mediaEnd, _bufferBytes,
                _frames, _duplicates, _saves.Count(task => !task.IsCompleted), _error)
                { Recovering = !_stopRequested && _error is null && _native.Any(native => native.Recovering) };
        }
    }

    private double RecordingStartSeconds(IReadOnlyList<Track> tracks)
    {
        double first = 0;
        foreach (Track track in tracks)
        {
            double value = _native[Array.IndexOf(_tracks, track)].FirstFrameSeconds;
            if (double.IsFinite(value)) first = Math.Max(first, value);
        }
        return first;
    }

    public async Task<SavedReplay> SaveAsync(int? seconds = null) => (await SaveScreensAsync(seconds, [_tracks[0].Profile.DeviceName]))[0];

    public Task<SavedReplay[]> SaveScreensAsync(int? seconds = null, IReadOnlyCollection<string>? monitorNames = null, string applicationName = "Desktop", string? sessionId = null, DateTime? sessionStartedUtc = null, long? endTimestamp = null)
    {
        lock (_gate)
        {
            if (_stopRequested || _error is not null) throw new InvalidOperationException(_error ?? "Захват остановлен.");
            if (_native.Any(native => native.Recovering)) throw new InvalidOperationException("Сохранение будет доступно после восстановления видеозахвата.");
            _saves.RemoveAll(task => task.IsCompleted);
            if (_saves.Count >= 2) throw new InvalidOperationException("Уже сохраняются два повтора.");
            double end = ReplayEnd(endTimestamp ?? Stopwatch.GetTimestamp());
            int requested = seconds ?? _settings.ReplaySeconds;
            if (requested < 1 || requested > _settings.HistorySeconds) throw new ArgumentException("Повтор должен помещаться в историю.");
            // Конец фиксируется сейчас. Ожидание закрытия сегмента не двигает момент события.
            Track[] tracks = SelectReplayTracks(monitorNames);
            double duration = Math.Min(requested, end - RecordingStartSeconds(tracks));
            if (duration < 1) throw new InvalidOperationException("Пока недостаточно видео для повтора.");
            // Момент события фиксируется под блокировкой, упаковка и файловые операции идут вне UI.
            string application = ReplayFileName.ApplicationFolder(applicationName);
            DateTime requestedAt = DateTime.Now;
            Task<SavedReplay[]> save = Task.Run(() => SaveGroupAsync(tracks, end, duration, application, requestedAt, sessionId, sessionStartedUtc));
            _saves.Add(save);
            return save;
        }
    }

    internal IDisposable[] HoldReplayHistory(long endTimestamp, int seconds, IReadOnlyCollection<string>? monitorNames)
    {
        lock (_gate)
        {
            if (_stopRequested || _error is not null) throw new InvalidOperationException(_error ?? "Захват остановлен.");
            if (_native.Any(native => native.Recovering)) throw new InvalidOperationException("Сохранение будет доступно после восстановления видеозахвата.");
            Track[] tracks = SelectReplayTracks(monitorNames);
            double end = ReplayEnd(endTimestamp);
            double start = Math.Max(RecordingStartSeconds(tracks), end - seconds);
            if (end - start < 1) throw new InvalidOperationException("Пока недостаточно видео для повтора.");
            return tracks.Select(track => track.Buffer.HoldRange(TimeSpan.FromSeconds(start), TimeSpan.FromSeconds(end))).ToArray();
        }
    }

    private double ReplayEnd(long timestamp) => Math.Max(0, timestamp / (double)Stopwatch.Frequency - _epoch / 10_000_000.0);

    private Track[] SelectReplayTracks(IReadOnlyCollection<string>? monitorNames)
    {
        Track[] tracks = _tracks.Where(track => monitorNames is null || monitorNames.Contains(track.Profile.DeviceName)).ToArray();
        if (tracks.Length == 0 || monitorNames is not null && tracks.Length != monitorNames.Distinct().Count())
            throw new ArgumentException("Выбранный экран не записывается.");
        return tracks;
    }

    private async Task<SavedReplay[]> SaveGroupAsync(Track[] tracks, double end, double duration, string application, DateTime requestedAt, string? sessionId, DateTime? sessionStartedUtc)
    {
        string eventId = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff") + "-" + Guid.NewGuid().ToString("N")[..6];
        string destination = _settings.ResolveReplayFolder(application);
        Directory.CreateDirectory(destination);
        SavedReplay[] saved = await Task.WhenAll(tracks.Select(track => SaveCoreAsync(track, end, duration, eventId, application, destination, requestedAt, tracks.Length > 1, sessionId, sessionStartedUtc)));
        string[] files = saved.Select(replay => replay.File).ToArray();
        for (int index = 0; index < saved.Length; index++)
        {
            saved[index] = saved[index] with { RelatedFiles = files };
        }
        return saved;
    }

    private async Task<SavedReplay> SaveCoreAsync(Track track, double end, double duration, string eventId, string application, string destination, DateTime requestedAt, bool multiple, string? sessionId, DateTime? sessionStartedUtc)
    {
        var timer = Stopwatch.StartNew();
        while (true)
        {
            double closed;
            string? error;
            lock (_gate) { closed = track.ClosedEnd; error = _error; }
            if (closed >= end) break;
            if (error is not null) throw new IOException(error);
            if (timer.Elapsed > TimeSpan.FromSeconds(8)) throw new TimeoutException("Не получен завершённый фрагмент повтора.");
            await Task.Delay(50);
        }
        using ReplayLease lease = track.Buffer.Reserve(TimeSpan.FromSeconds(end), TimeSpan.FromSeconds(duration));
        string id = eventId + "-" + string.Concat(track.Profile.DeviceName.Where(char.IsLetterOrDigit));
        string screen = string.Concat(track.Profile.DeviceName.Where(char.IsLetterOrDigit));
        string partial = Path.Combine(destination, Guid.NewGuid().ToString("N") + ".partial.mp4");
        string? nameReservation = null;
        double start = lease.Segments[0].Start.TotalSeconds;
        double leading = end - duration - start;
        (int width, int height) = track.Profile.OutputSize(track.Monitor.Width, track.Monitor.Height);
        var metadata = new SavedReplay("", "", 0, width, height, track.Profile.Fps, end, duration, leading, 0,
            _settings.CaptureSystemAudio, _audio?.Sources ?? [], track.Profile.DeviceName, eventId,
            ApplicationName: application, SessionId: sessionId, SessionStartedUtc: sessionStartedUtc?.ToUniversalTime(), SavedAtUtc: requestedAt.ToUniversalTime());
        try
        {
            double closeWaitMs = timer.Elapsed.TotalMilliseconds;
            ReplayExportDiagnostics export = await ReplayExport.ExportAsync(lease.Segments, partial,
                new(ReplayMetadata.Comment(metadata), _audio?.Sources ?? [], track.Profile.Fps, end - start));
            double beforeProbeMs = timer.Elapsed.TotalMilliseconds;
            string json = await CaptureProcesses.RunAsync(CaptureProcesses.Ffprobe,
                ["-v", "error", "-show_streams", "-show_format", "-of", "json", partial], TimeSpan.FromSeconds(15));
            using JsonDocument probe = JsonDocument.Parse(json);
            JsonElement video = probe.RootElement.GetProperty("streams").EnumerateArray().Single(stream => stream.GetProperty("codec_type").GetString() == "video");
            int audioCount = probe.RootElement.GetProperty("streams").EnumerateArray().Count(stream => stream.GetProperty("codec_type").GetString() == "audio");
            if (audioCount != (_audio?.Sources.Length ?? 0)) throw new InvalidDataException("В сохранённом повторе отсутствуют ожидаемые звуковые дорожки.");
            double seconds = double.Parse(probe.RootElement.GetProperty("format").GetProperty("duration").GetString()!, CultureInfo.InvariantCulture);
            // r_frame_rate — оценка общей сетки временных меток, а не число кадров в секунду.
            // После миллисекундных MKV-сегментов 30 FPS иногда определяется как 120/1.
            string[] rate = video.GetProperty("avg_frame_rate").GetString()!.Split('/');
            double measuredFps = double.Parse(rate[0], CultureInfo.InvariantCulture) / double.Parse(rate[1], CultureInfo.InvariantCulture);
            // Просадка частоты не должна уничтожать пригодный повтор. Приёмка 120 FPS
            // отдельно проверяет номера кадров; здесь проверяется читаемость файла.
            if (!double.IsFinite(measuredFps) || measuredFps <= 0 || measuredFps > track.Profile.Fps * 1.1)
                throw new InvalidDataException("Некорректная частота сохранённого файла.");
            double fps = measuredFps;
            if (seconds < duration - 0.1 || Math.Abs(seconds - (duration + leading)) > .25) throw new InvalidDataException("Длительность сохранённого повтора не совпала с запросом.");
            // Имя с размером формируется после экспорта; метаданные MP4 не содержат путь файла.
            long bytes = new FileInfo(partial).Length;
            string final = ReplayFileName.Reserve(destination, _settings.EffectiveFileNameFormat, application, requestedAt, duration, screen, multiple, bytes);
            nameReservation = Path.ChangeExtension(final, ".partial.mp4");
            File.Move(partial, final);
            var replay = metadata with { File = final, Duration = seconds, Width = video.GetProperty("width").GetInt32(), Height = video.GetProperty("height").GetInt32(),
                Fps = fps, Bytes = bytes };
            await File.WriteAllTextAsync(Path.Combine(track.Root, "save-" + id + ".json"), JsonSerializer.Serialize(new
                { closeWaitMs, export, probeMs = timer.Elapsed.TotalMilliseconds - beforeProbeMs, totalMs = timer.Elapsed.TotalMilliseconds,
                    segments = lease.Segments.Count, replay.Bytes, replay.Duration, replay.Fps }));
            return replay;
        }
        finally
        {
            if (File.Exists(partial)) File.Delete(partial);
            if (nameReservation is not null && File.Exists(nameReservation)) File.Delete(nameReservation);
        }
    }

    private async Task ReadProgressAsync()
    {
        while (await _capture.StandardOutput.ReadLineAsync() is string line)
        {
            int separator = line.IndexOf('=');
            if (separator < 0 || !long.TryParse(line.AsSpan(separator + 1), out long value)) continue;
            lock (_gate)
            {
                if (line.StartsWith("out_time_us=", StringComparison.Ordinal)) _mediaEnd = Math.Max(_mediaEnd, value / 1_000_000.0);
                if (line.StartsWith("frame=", StringComparison.Ordinal)) _frames = value;
                if (line.StartsWith("dup_frames=", StringComparison.Ordinal)) _duplicates = value;
            }
        }
    }

    private async Task ReadErrorsAsync()
    {
        while (await _capture.StandardError.ReadLineAsync() is string line)
        {
            lock (_gate)
            {
                _errors.Enqueue(line.Length > 1000 ? line[^1000..] : line);
                while (_errors.Count > 200) _errors.Dequeue();
            }
        }
    }

    private async Task WatchAsync()
    {
        try
        {
            do
            {
                await RecordAudioMetricsAsync();
                if (!_stopRequested && _native.FirstOrDefault(native => native.Error is not null)?.Error is string videoError) throw new IOException(videoError);
                if (!_stopRequested && _audio?.Error is string audioError)
                {
                    string detail; lock (_gate) detail = string.Join("\n", _errors);
                    throw new IOException(audioError + "\n" + detail);
                }
                RegisterClosedSegments();
                long bytes = MeasureBufferBytes();
                lock (_gate) _bufferBytes = bytes;
                if (bytes > _maximumBufferBytes) throw new IOException("Достигнут защитный предел дискового буфера. Захват остановлен.");
                if (new DriveInfo(Path.GetPathRoot(_sessionRoot)!).AvailableFreeSpace < 512L * 1024 * 1024)
                    throw new IOException("На диске осталось менее 512 МиБ. Захват остановлен; сохранённые клипы не удалялись.");
                if (_capture.HasExited) break;
                await Task.Delay(250);
            } while (true);
            await _capture.WaitForExitAsync();
            await Task.WhenAll(_outputReader, _errorReader);
            RegisterClosedSegments();
            if (!_stopRequested && _native.FirstOrDefault(native => native.Error is not null)?.Error is string finalVideoError)
                throw new IOException(finalVideoError);
            if (!_stopRequested || _capture.ExitCode != 0)
                throw new IOException("Захват завершился: " + string.Join("\n", _errors));
        }
        catch (Exception error)
        {
            lock (_gate) _error = error.Message;
            if (!_capture.HasExited) { _capture.Kill(entireProcessTree: true); await _capture.WaitForExitAsync(); }
        }
    }

    private void RegisterClosedSegments()
    {
        foreach (Track track in _tracks) RegisterClosedSegments(track);
        lock (_gate) _closedEnd = _tracks.Min(track => track.ClosedEnd);
    }

    private void RegisterClosedSegments(Track track)
    {
        if (!File.Exists(track.Manifest)) return;
        string text;
        try
        {
            using var file = new FileStream(track.Manifest, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(file);
            text = reader.ReadToEnd();
        }
        catch (IOException error) when ((error.HResult & 0xFFFF) is 2 or 32 or 33) { return; }
        string[] lines = text.Split('\n');
        foreach (string line in lines[..^1])
        {
            string[] values = line.TrimEnd('\r').Split(',');
            if (values.Length != 3) continue;
            string name = values[0].Trim('"');
            if (Path.GetFileName(name) != name || !name.StartsWith("part", StringComparison.Ordinal) ||
                !int.TryParse(Path.GetFileNameWithoutExtension(name).AsSpan(4), out int sequence))
                throw new InvalidDataException("Некорректный список фрагментов.");
            if (sequence <= track.LastSequence) continue;
            if (sequence != track.LastSequence + 1) throw new InvalidDataException("В истории отсутствует файл фрагмента.");
            double start = double.Parse(values[1], CultureInfo.InvariantCulture);
            double end = double.Parse(values[2], CultureInfo.InvariantCulture);
            if (sequence == 0)
            {
                double first = _native[Array.IndexOf(_tracks, track)].FirstFrameSeconds;
                if (double.IsFinite(first)) start = Math.Max(start, first);
            }
            track.Buffer.RegisterClosedSegment(new MediaSegment(Path.Combine(track.Root, name), TimeSpan.FromSeconds(start), TimeSpan.FromSeconds(end)));
            track.LastSequence = sequence;
            lock (_gate) track.ClosedEnd = end;
        }
    }

    public async Task StopAsync()
    {
        await _stopGate.WaitAsync();
        try
        {
            if (_stopped) return;
            bool preserveBuffer;
            lock (_gate) preserveBuffer = _error is not null;
            _stopRequested = true;
            await Task.WhenAll(_native.Select(native => native.StopAsync()));
            if (!_capture.HasExited)
            {
                try { await _capture.StandardInput.WriteLineAsync("q"); await _capture.StandardInput.FlushAsync(); }
                catch (IOException) { }
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                try { await _capture.WaitForExitAsync(deadline.Token); }
                catch (OperationCanceledException) { _capture.Kill(entireProcessTree: true); await _capture.WaitForExitAsync(); }
            }
            await _watcher;
            if (_audio is not null) await _audio.DisposeAsync();
            await RecordAudioMetricsAsync(force: true);
            Task[] saves;
            lock (_gate) saves = _saves.ToArray();
            try { await Task.WhenAll(saves); }
            catch (Exception error) { lock (_gate) _error ??= error.Message; }
            await Task.WhenAll(_outputReader, _errorReader);
            try { await File.WriteAllLinesAsync(Path.Combine(_sessionRoot, "capture.log"), _errors); }
            catch (IOException error) { lock (_gate) _error ??= error.Message; }
            // После обрыва закрытые сегменты остаются доступны для восстановления записи.
            if (!preserveBuffer)
            {
                foreach (Track track in _tracks)
                foreach (string file in Directory.EnumerateFiles(track.Root, "part*." + ReplayExport.Extension))
                {
                    string relative = Path.GetRelativePath(track.Root, Path.GetFullPath(file));
                    if (Path.GetFileName(relative) != relative) throw new IOException("Путь вышел за пределы сеанса.");
                    try { File.Delete(file); }
                    catch (IOException) { }
                }
            }
            lock (_gate)
            {
                _bufferBytes = MeasureBufferBytes();
                _closedEnd = 0;
            }
            _stopped = true;
        }
        finally { _stopGate.Release(); }
    }

    public async ValueTask DisposeAsync() { try { await StopAsync(); } finally { foreach (NativeCaptureBridge native in _native) await native.DisposeAsync(); _capture.Dispose(); } }

    private long MeasureBufferBytes()
    {
        long bytes = 0;
        foreach (Track track in _tracks)
        foreach (string file in Directory.EnumerateFiles(track.Root, "part*." + ReplayExport.Extension))
        {
            try { bytes += new FileInfo(file).Length; }
            // Снятие reservation может удалить старый фрагмент между перечислением и чтением размера.
            catch (FileNotFoundException) { }
        }
        return bytes;
    }
}
