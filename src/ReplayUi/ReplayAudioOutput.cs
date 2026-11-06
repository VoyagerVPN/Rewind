using NAudio.Wave;

namespace Rewind.Preview;

// Один нативный клиент на окно: создание/удаление WASAPI во время process-loopback может терять системный звук.
internal sealed class ReplayAudioOutput : IDisposable
{
    private readonly object _sync = new();
    private readonly SemaphoreSlim _initialization = new(1);
    private State? _state;
    private Lease? _owner;
    private bool _disposed;

    internal sealed class SwitchingSource : ISampleProvider
    {
        public ISampleProvider? Source;
        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);
        public int Read(Span<float> buffer)
        {
            ISampleProvider? source = Volatile.Read(ref Source);
            if (source is null) { buffer.Clear(); return buffer.Length; }
            return source.Read(buffer);
        }
    }
    internal sealed class State(WasapiPlayer player)
    {
        public readonly WasapiPlayer Player = player;
        public readonly SwitchingSource Input = new();
        public Exception? Failure;
    }
    public sealed class Lease : IDisposable
    {
        private readonly ReplayAudioOutput _output;
        private readonly State _state;
        private bool _released;
        internal Lease(ReplayAudioOutput output, State state) { _output = output; _state = state; }
        public void Play() => Run(player => player.Play());
        public void Pause() => Run(player => player.Pause());
        public void Stop() => Run(player => player.Stop());
        private void Run(Action<WasapiPlayer> action)
        {
            lock (_output._sync)
                if (!_released && !_output._disposed && ReferenceEquals(_output._owner, this))
                    try { action(_state.Player); }
                    catch (Exception error) { Volatile.Write(ref _state.Failure, error); throw; }
        }
        public void Dispose()
        {
            lock (_output._sync)
            {
                if (_released) return; _released = true;
                // Поздняя очистка предыдущего клипа не останавливает уже открытый следующий.
                if (!ReferenceEquals(_output._owner, this)) return;
                _output._owner = null;
                try { _state.Player.Stop(); } finally { Volatile.Write(ref _state.Input.Source, null); }
            }
        }
    }
    private async Task EnsureAsync()
    {
        State? previous;
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_state is not null && Volatile.Read(ref _state.Failure) is null) return;
            previous = _state; _state = null; _owner = null;
        }
        previous?.Player.Dispose();
        WasapiPlayer player = await new WasapiPlayerBuilder().WithDefaultDeviceStreamRouting().WithLatency(40).BuildAsync().ConfigureAwait(false);
        var state = new State(player);
        try
        {
            // Обработчик не берёт _sync: Stop/Dispose ждут завершения аудиопотока.
            player.PlaybackStopped += (_, args) => { if (args.Exception is not null) Volatile.Write(ref state.Failure, args.Exception); };
            player.Init(state.Input.ToWaveProvider());
            lock (_sync) { ObjectDisposedException.ThrowIf(_disposed, this); _state = state; }
            return;
        }
        catch { player.Dispose(); throw; }
    }
    public async Task<Lease> AcquireAsync(ISampleProvider source, CancellationToken cancellation)
    {
        await _initialization.WaitAsync(cancellation).ConfigureAwait(false);
        try
        {
            await EnsureAsync().ConfigureAwait(false); cancellation.ThrowIfCancellationRequested();
            lock (_sync)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                State state = _state!; state.Player.Stop();
                Volatile.Write(ref state.Input.Source, source);
                return _owner = new Lease(this, state);
            }
        }
        finally { _initialization.Release(); }
    }
    public async Task WarmAsync()
    {
        await _initialization.WaitAsync().ConfigureAwait(false);
        try
        {
            await EnsureAsync().ConfigureAwait(false);
        }
        finally { _initialization.Release(); }
    }
    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return; _disposed = true; _owner = null;
            _state?.Player.Dispose(); _state = null;
        }
    }
}
