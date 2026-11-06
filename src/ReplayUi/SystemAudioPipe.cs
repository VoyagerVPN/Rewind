using System.IO;
using System.IO.Pipes;
using System.Diagnostics;
using System.Runtime.InteropServices;
using NAudio.Wave;
using NAudio.CoreAudioApi;

namespace Rewind.Preview;

internal sealed class SystemAudioPipe : IAsyncDisposable
{
    private sealed class Source(string name)
    {
        public readonly object Gate = new();
        public readonly AudioTimelineBuffer Buffer = new();
        public readonly string Name = name;
        public WasapiRecorder? Recorder;
        public string Device = "Подключение…";
        public string? Warning, Failure;
        public readonly AudioLevelEnvelope Level = new();
        public long Packets, SignalPackets;
        public long Discontinuities, Attachments;
        public long LastLoss;
        public long EmptyCallbacks, TimestampFallbacks, ClockCorrections;
        public double PacketAge, MaxPacketAge, ClockOffset;
    }
    private readonly Source[] _sources = [new("system"), new("discord"), new("microphone")];
    private readonly CaptureSettings _settings;
    private readonly uint? _applicationOverride;
    private readonly long? _sharedEpoch;
    private readonly NamedPipeServerStream? _pipe;
    private readonly bool _meterOnly;
    private readonly CancellationTokenSource _cancel = new();
    private readonly TaskCompletionSource _connected = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Task _writer, _routing;
    private int _disposed;
    private string? _error;
    private Process? _silentAnchor;
    private readonly object _metricsGate = new();
    private long _epochQpc100ns, _writeStarted, _writtenFrames, _writes, _pipeWaitEvents, _wakeDelayEvents;
    private double _maxPipeWait, _maxWakeDelay;
    public string Path { get; }
    public string? Error => Volatile.Read(ref _error);
    public string[] Sources { get; }
    public AudioSourceStatus[] Status => _sources.Where(source => Sources.Contains(source.Name)).Select(source =>
    {
        lock (source.Gate) return new AudioSourceStatus(source.Name, source.Device,
            source.Level.Read(ClockNow()), source.Warning ?? source.Failure ??
                (source.LastLoss != 0 && Stopwatch.GetElapsedTime(source.LastLoss).TotalSeconds < 5
                    ? "Краткий пропуск звука · запись продолжается" : null),
            source.Recorder is not null && source.Failure is null, source.Packets, source.SignalPackets,
            source.Buffer.Metrics, source.Discontinuities, Math.Max(0, source.Attachments - 1),
            new(source.PacketAge, source.MaxPacketAge, source.ClockOffset, Interlocked.Read(ref source.EmptyCallbacks), source.TimestampFallbacks, source.ClockCorrections));
    }).ToArray();

    public AudioCaptureDiagnostics Diagnostics
    {
        get
        {
            AudioTransportMetrics transport;
            lock (_metricsGate)
            {
                double pending = _writeStarted == 0 ? 0 : Stopwatch.GetElapsedTime(_writeStarted).TotalMilliseconds;
                transport = new(_writtenFrames, _writes, pending,
                    Math.Max(_maxPipeWait, pending), _pipeWaitEvents, _maxWakeDelay, _wakeDelayEvents);
            }
            return new(transport, Status);
        }
    }

    private static TimeSpan ClockNow() => TimeSpan.FromSeconds(Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency);

    public SystemAudioPipe(CaptureSettings settings, uint? applicationOverride = null, bool meterOnly = false, long? epochQpc100ns = null)
    {
        _settings = settings;
        _applicationOverride = applicationOverride;
        _sharedEpoch = epochQpc100ns;
        _meterOnly = meterOnly;
        Sources = new[] { settings.CaptureSystemAudio ? "system" : null,
            settings.SeparateDiscord ? "discord" : null,
            settings.CaptureMicrophone ? "microphone" : null }.OfType<string>().ToArray();
        string name = "rewind-audio-" + Guid.NewGuid().ToString("N");
        Path = @"\\.\pipe\" + name;
        if (!meterOnly) _pipe = new NamedPipeServerStream(name, PipeDirection.Out, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 4096, 262144);
        else _connected.TrySetResult();
        _routing = Task.Run(RouteAsync);
        _writer = meterOnly ? Task.CompletedTask : Task.Run(WriteAsync);
    }

    private static WasapiRecorderBuilder Builder() => new WasapiRecorderBuilder()
        .WithFormat(WaveFormat.CreateIeeeFloatWaveFormat(48000, 2)).WithBufferLength(20).WithMmcssThreadPriority("Audio");

    private async Task RouteAsync()
    {
        try { await _connected.Task.WaitAsync(_cancel.Token); }
        catch (OperationCanceledException) { return; }
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 20348))
        { _error = "Разделение звука требует Windows build 20348 или новее."; return; }
        uint? activeProcess = null;
        bool processReady = false;
        string? activeMicrophone = null;
        while (!_cancel.IsCancellationRequested)
        {
            if (_settings.CaptureSystemAudio || _settings.SeparateDiscord)
            {
                try
                {
                    uint? discord = _settings.SeparateDiscord ? _applicationOverride ?? AudioDeviceCatalog.DiscordProcess() : null;
                    if (!processReady || discord != activeProcess || _sources.Take(2).Any(source => source.Failure is not null) ||
                        discord is null && _silentAnchor?.HasExited == true)
                    {
                        processReady = false;
                        await ReleaseAsync(_sources[0]); await ReleaseAsync(_sources[1]);
                        if (_settings.CaptureSystemAudio && discord is null && (_silentAnchor is null || _silentAnchor.HasExited))
                        {
                            _silentAnchor?.Dispose();
                            // Без Discord исключаем собственный беззвучный процесс, чтобы захват оставался независимым от устройств.
                            _silentAnchor = CaptureProcesses.Start(Environment.ProcessPath!, ["--audio-anchor"]);
                        }
                        if (_settings.CaptureSystemAudio)
                        {
                            uint excluded = discord ?? (uint)_silentAnchor!.Id;
                            var system = await Builder().WithProcessLoopback(excluded, ProcessLoopbackMode.ExcludeTargetProcessTree).BuildAsync();
                            Attach(_sources[0], system, discord is null ? "Все выходы Windows" : "Все выходы Windows · без Discord");
                        }
                        if (discord is uint pid)
                            Attach(_sources[1], await Builder().WithProcessLoopback(pid, ProcessLoopbackMode.IncludeTargetProcessTree).BuildAsync(), $"Discord · PID {pid}");
                        else lock (_sources[1].Gate) { _sources[1].Device = "Discord не запущен"; _sources[1].Warning = null; }
                        activeProcess = discord; processReady = true;
                    }
                }
                catch (Exception error)
                {
                    await ReleaseAsync(_sources[0]); await ReleaseAsync(_sources[1]);
                    lock (_sources[0].Gate) _sources[0].Warning = "Нет системного звука: " + error.Message;
                    lock (_sources[1].Gate) _sources[1].Warning = "Discord не подключён: " + error.Message;
                    processReady = false;
                }
            }
            if (_settings.CaptureMicrophone)
            {
                Source source = _sources[2];
                try
                {
                    var device = AudioDeviceCatalog.ResolveMicrophone(_settings.MicrophoneDeviceId);
                    if (source.Recorder is null || activeMicrophone != device.Id || source.Failure is not null)
                    {
                        await ReleaseAsync(source);
                        using var enumerator = new MMDeviceEnumerator();
                        using MMDevice endpoint = enumerator.GetDevice(device.Id);
                        Attach(source, await Builder().WithDevice(endpoint).BuildAsync(), device.Name);
                        activeMicrophone = device.Id;
                    }
                }
                catch (Exception error)
                {
                    await ReleaseAsync(source);
                    lock (source.Gate) { source.Device = "Микрофон недоступен"; source.Warning = error.Message; }
                    activeMicrophone = null;
                }
            }
            try { await Task.Delay(1000, _cancel.Token); }
            catch (OperationCanceledException) { break; }
        }
        foreach (Source source in _sources) await ReleaseAsync(source);
    }

    private void Attach(Source source, WasapiRecorder recorder, string device)
    {
        lock (source.Gate) { source.Recorder = recorder; source.Device = device; source.Warning = null; source.Failure = null; source.Buffer.Clear(); source.Attachments++; }
        var timeline = new AudioPacketTimeline(_epochQpc100ns);
        recorder.DataAvailable += (bytes, flags, devicePosition, qpcPosition) =>
        {
            if (bytes.IsEmpty) { Interlocked.Increment(ref source.EmptyCallbacks); return; }
            lock (source.Gate)
            {
                if (_cancel.IsCancellationRequested || !ReferenceEquals(source.Recorder, recorder)) return;
                try
                {
                    bool silent = ((int)flags & 2) != 0;
                    if (!_meterOnly)
                    {
                        long lost = source.Buffer.DroppedFrames;
                        double packetQpc = (double)qpcPosition;
                        double nowQpc = Stopwatch.GetTimestamp() * (10_000_000.0 / Stopwatch.Frequency);
                        // При недостоверной метке используем время получения за вычетом длины пакета.
                        if (((int)flags & 4) != 0 || packetQpc <= 0 || Math.Abs(packetQpc - nowQpc) > 10_000_000)
                        {
                            packetQpc = nowQpc - bytes.Length / 8.0 / 48000 * 10_000_000;
                            source.TimestampFallbacks++;
                        }
                        long corrections = timeline.Corrections;
                        long firstFrame = timeline.Position((long)devicePosition, (long)packetQpc, bytes.Length / 8, ((int)flags & 1) != 0);
                        source.ClockCorrections += timeline.Corrections - corrections;
                        source.PacketAge = (nowQpc - packetQpc) / 10000;
                        source.MaxPacketAge = Math.Max(source.MaxPacketAge, source.PacketAge);
                        source.ClockOffset = firstFrame / 48.0 - (packetQpc - _epochQpc100ns) / 10000;
                        source.Buffer.Write(firstFrame, bytes, silent);
                        if (source.Buffer.DroppedFrames != lost) source.LastLoss = Stopwatch.GetTimestamp();
                    }
                    if (((int)flags & 1) != 0) source.Discontinuities++;
                    float peak = 0;
                    if (!silent) foreach (float sample in MemoryMarshal.Cast<byte, float>(bytes)) peak = Math.Max(peak, Math.Abs(sample));
                    source.Level.Observe(peak, ClockNow()); source.Packets++;
                    if (peak > 0) source.SignalPackets++;
                }
                catch (Exception error) { source.Failure = "Буфер звука: " + error.Message; }
            }
        };
        recorder.RecordingStopped += (_, args) =>
        {
            lock (source.Gate) if (ReferenceEquals(source.Recorder, recorder)) source.Failure = args.Exception?.Message ?? "Источник остановлен";
        };
        recorder.StartRecording();
    }

    private static async Task ReleaseAsync(Source source)
    {
        WasapiRecorder? recorder;
        lock (source.Gate) { recorder = source.Recorder; source.Recorder = null; source.Buffer.Clear(); source.Level.Reset(); source.Failure = null; }
        if (recorder is not null) await recorder.DisposeAsync();
    }

    private async Task WriteAsync()
    {
        try
        {
            await _pipe!.WaitForConnectionAsync(_cancel.Token);
            long connected = (long)(Stopwatch.GetTimestamp() * (10_000_000.0 / Stopwatch.Frequency));
            _epochQpc100ns = _sharedEpoch ?? connected;
            double initialMilliseconds = (connected - _epochQpc100ns) / 10000.0;
            var clock = Stopwatch.StartNew();
            _connected.TrySetResult();
            byte[][] blocks = [new byte[480 * 2 * 4], new byte[480 * 2 * 4], new byte[480 * 2 * 4]];
            byte[] packet = new byte[480 * 6 * 4];
            long frames = 0;
            // Один PCM-поток даёт трём дорожкам общую шкалу времени и ограниченный буфер.
            while (!_cancel.IsCancellationRequested)
            {
                // Запас 60 мс позволяет пакетам WASAPI прийти до чтения их позиции на общей шкале.
                double due = frames / 48.0 + 60;
                double wait = due - clock.Elapsed.TotalMilliseconds - initialMilliseconds;
                if (wait > 0)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(wait), _cancel.Token);
                    double late = Math.Max(0, clock.Elapsed.TotalMilliseconds + initialMilliseconds - due);
                    lock (_metricsGate) { _maxWakeDelay = Math.Max(_maxWakeDelay, late); if (late >= 100) _wakeDelayEvents++; }
                }
                for (int source = 0; source < 3; source++)
                {
                    lock (_sources[source].Gate) _sources[source].Buffer.Read(frames, blocks[source]);
                }
                {
                    ReadOnlySpan<long> system = MemoryMarshal.Cast<byte, long>(blocks[0]);
                    ReadOnlySpan<long> discord = MemoryMarshal.Cast<byte, long>(blocks[1]);
                    ReadOnlySpan<long> microphone = MemoryMarshal.Cast<byte, long>(blocks[2]);
                    Span<long> output = MemoryMarshal.Cast<byte, long>(packet.AsSpan());
                    for (int frame = 0; frame < 480; frame++)
                    {
                        output[frame * 3] = system[frame];
                        output[frame * 3 + 1] = discord[frame];
                        output[frame * 3 + 2] = microphone[frame];
                    }
                }
                long started = Stopwatch.GetTimestamp();
                lock (_metricsGate) _writeStarted = started;
                await _pipe.WriteAsync(packet, _cancel.Token);
                double elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                lock (_metricsGate)
                {
                    _writeStarted = 0; _writes++; _writtenFrames += 480;
                    _maxPipeWait = Math.Max(_maxPipeWait, elapsed);
                    if (elapsed >= 100) _pipeWaitEvents++;
                }
                frames += 480;
            }
        }
        catch (Exception error) when (_cancel.IsCancellationRequested && error is OperationCanceledException or IOException or ObjectDisposedException) { }
        catch (Exception error) { Interlocked.CompareExchange(ref _error, "Ошибка передачи звука: " + error.Message, null); }
        finally { lock (_metricsGate) _writeStarted = 0; }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _cancel.Cancel(); _pipe?.Dispose();
        await Task.WhenAll(_writer, _routing);
        if (_silentAnchor is not null)
        {
            if (!_silentAnchor.HasExited)
            {
                _silentAnchor.StandardInput.Close();
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                try { await _silentAnchor.WaitForExitAsync(timeout.Token); }
                catch (OperationCanceledException) { _silentAnchor.Kill(); await _silentAnchor.WaitForExitAsync(); }
            }
            _silentAnchor.Dispose();
        }
        _cancel.Dispose();
    }
}
