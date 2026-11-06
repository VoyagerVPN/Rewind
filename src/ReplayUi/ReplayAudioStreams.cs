using System.Diagnostics;
using System.Globalization;
using System.IO;
using NAudio.Wave;

namespace Rewind.Preview;

// FFmpeg декодирует небольшой движущийся участок аудио; видео обрабатывает отдельный mpv-процесс.
internal sealed class ReplayAudioStreams : IDisposable
{
    private readonly BufferedWaveProvider _buffer = new(WaveFormat.CreateIeeeFloatWaveFormat(48000, 6), TimeSpan.FromSeconds(.3));
    private readonly double[] _gains = new double[3];
    private readonly bool[] _active = new bool[3];
    private readonly SemaphoreSlim _gate = new(1);
    private readonly ReplayAudioOutput _audioOutput;
    private readonly bool _ownsOutput;
    private ReplayAudioOutput.Lease? _output;
    public ReplayAudioStreams(ReplayAudioOutput? output = null)
    { _audioOutput = output ?? new ReplayAudioOutput(); _ownsOutput = output is null; }
    private Process? _decoder;
    private Task? _pump;
    private Task<string>? _errors;
    private CancellationTokenSource? _decodeCancellation, _requestCancellation;
    private Clip? _clip;
    private bool _playing;
    private double _target, _pausedPosition;
    private long _playStamp;
    private int _generation;
    public int Count => _active.Count(active => active);
    public bool HasSource(int source) => _active[source];
    public double Position => _playing ? _target + Stopwatch.GetElapsedTime(_playStamp).TotalSeconds : _pausedPosition;

    private sealed class Mixer(BufferedWaveProvider buffer, double[] gains) : ISampleProvider
    {
        private readonly ISampleProvider _source = buffer.ToSampleProvider();
        private float[] _scratch = [];
        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);
        public int Read(Span<float> output)
        {
            int count = output.Length;
            int frames = count / 2, required = frames * 6;
            if (_scratch.Length < required) _scratch = new float[required];
            _source.Read(_scratch.AsSpan(0, required));
            for (int frame = 0; frame < frames; frame++)
            {
                int input = frame * 6, target = frame * 2;
                output[target] = (float)Math.Clamp(_scratch[input] * gains[0] + _scratch[input + 2] * gains[1] + _scratch[input + 4] * gains[2], -1, 1);
                output[target + 1] = (float)Math.Clamp(_scratch[input + 1] * gains[0] + _scratch[input + 3] * gains[1] + _scratch[input + 5] * gains[2], -1, 1);
            }
            return count;
        }
    }

    public async Task StartAsync(Clip clip, double position, bool playing, CancellationToken cancellation)
    {
        _clip = clip;
        for (int source = 0; source < 3; source++)
            _active[source] = clip.Audio.ElementAtOrDefault(source) is not null || clip.EmbeddedAudioSources?.Contains(new[] { "system", "discord", "microphone" }[source]) == true || source == 0 && clip.EmbeddedSystemAudio;
        ReplayAudioOutput.Lease? previous = _output;
        _output = Count > 0 ? await _audioOutput.AcquireAsync(new Mixer(_buffer, _gains), cancellation) : null;
        previous?.Dispose();
        await SeekAsync(position, playing, cancellation);
    }
    public void SetVolume(int source, double gain) => _gains[source] = gain;
    public void Pause()
    { if (!_playing) return; _pausedPosition = Position; _playing = false; _output?.Pause(); }
    public void Resume()
    { if (_playing) return; _target = _pausedPosition; _playStamp = Stopwatch.GetTimestamp(); _playing = true; if (Count > 0) _output?.Play(); }

    public async Task SeekAsync(double position, bool resume, CancellationToken cancellation = default)
    {
        int generation = ++_generation; _requestCancellation?.Cancel();
        using var request = CancellationTokenSource.CreateLinkedTokenSource(cancellation); _requestCancellation = request;
        bool entered = false;
        try
        {
            await _gate.WaitAsync(request.Token); entered = true;
            await StopDecoderAsync(); request.Token.ThrowIfCancellationRequested();
            _target = _pausedPosition = Math.Max(0, position); _playing = false; _output?.Stop(); _buffer.ClearBuffer();
            if (_clip is null || Count == 0) { if (resume) Resume(); return; }
            _decodeCancellation = CancellationTokenSource.CreateLinkedTokenSource(request.Token);
            _decoder = CaptureProcesses.Start(CaptureProcesses.Ffmpeg, Arguments(_clip, position));
            Process decoder = _decoder; CancellationToken token = _decodeCancellation.Token;
            _errors = decoder.StandardError.ReadToEndAsync(token);
            _pump = Task.Run(async () =>
            {
                byte[] bytes = new byte[12288];
                while (!token.IsCancellationRequested)
                {
                    while (_buffer.BufferLength - _buffer.BufferedBytes < bytes.Length) await Task.Delay(4, token);
                    int count = await decoder.StandardOutput.BaseStream.ReadAsync(bytes, token);
                    if (count == 0) break;
                    _buffer.AddSamples(bytes, 0, count);
                }
            }, token);
            var timer = Stopwatch.StartNew();
            while (_buffer.BufferedDuration.TotalMilliseconds < 60)
            {
                request.Token.ThrowIfCancellationRequested();
                if (_pump.IsFaulted) await _pump;
                if (_pump.IsCompleted || decoder.HasExited)
                {
                    if (decoder.HasExited && decoder.ExitCode != 0) throw new InvalidOperationException(Localization.Text("Message.CouldNotReadClipAudio") + await _errors);
                    break;
                }
                if (timer.Elapsed > TimeSpan.FromSeconds(10)) throw new TimeoutException(Localization.Text("Message.ClipAudioDidNotRespond"));
                await Task.Delay(5, request.Token);
            }
            if (generation == _generation && resume) Resume();
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested) { }
        finally { if (entered) _gate.Release(); if (ReferenceEquals(_requestCancellation, request)) _requestCancellation = null; }
    }

    private static List<string> Arguments(Clip clip, double seconds)
    {
        var args = new List<string> { "-hide_banner", "-v", "error", "-nostdin", "-filter_complex_threads", "1" };
        var filters = new List<string>(); int inputs = 0; int videoInput = -1;
        for (int source = 0; source < 3; source++)
        {
            int ordinal = clip.EmbeddedAudioSources is string[] sources ? Array.IndexOf(sources, new[] { "system", "discord", "microphone" }[source]) : source == 0 && clip.EmbeddedSystemAudio ? 0 : -1;
            string? external = clip.Audio.ElementAtOrDefault(source); string? input = null;
            if (ordinal >= 0)
            {
                if (videoInput < 0) { videoInput = inputs++; args.AddRange(["-ss", seconds.ToString("F6", CultureInfo.InvariantCulture), "-i", clip.Video]); }
                input = $"[{videoInput}:a:{ordinal}]";
            }
            else if (external is not null)
            { int index = inputs++; args.AddRange(["-ss", seconds.ToString("F6", CultureInfo.InvariantCulture), "-i", external]); input = $"[{index}:a:0]"; }
            filters.Add(input is null ? $"anullsrc=r=48000:cl=stereo[a{source}]" : input + $"aresample=48000,aformat=sample_fmts=flt:channel_layouts=stereo[a{source}]");
        }
        filters.Add("[a0][a1][a2]join=inputs=3:channel_layout=5.1:map=0.0-FL|0.1-FR|1.0-FC|1.1-LFE|2.0-BL|2.1-BR[mix]");
        args.AddRange(["-filter_complex", string.Join(';', filters), "-map", "[mix]", "-t", Math.Max(.01, clip.Duration - seconds).ToString("F6", CultureInfo.InvariantCulture), "-ar", "48000", "-ac", "6", "-c:a", "pcm_f32le", "-f", "f32le", "pipe:1"]);
        return args;
    }
    private async Task StopDecoderAsync()
    {
        _decodeCancellation?.Cancel();
        if (_decoder is Process decoder)
        {
            if (!decoder.HasExited) decoder.Kill(entireProcessTree: true);
            await decoder.WaitForExitAsync();
            try { if (_pump is not null) await _pump; } catch (OperationCanceledException) { }
            try { if (_errors is not null) await _errors; } catch (OperationCanceledException) { }
            decoder.Dispose(); _decoder = null;
        }
        _decodeCancellation?.Dispose(); _decodeCancellation = null; _pump = null; _errors = null;
    }
    public async Task StopAsync()
    {
        ++_generation; _requestCancellation?.Cancel();
        await _gate.WaitAsync();
        try { _playing = false; _output?.Stop(); await StopDecoderAsync(); _buffer.ClearBuffer(); }
        finally { _gate.Release(); }
    }
    public void Dispose() { try { _output?.Dispose(); } finally { if (_ownsOutput) _audioOutput.Dispose(); } }
}
