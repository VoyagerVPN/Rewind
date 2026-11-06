namespace Rewind.Preview;

public sealed record AudioBufferMetrics(int CapacityFrames, int BufferedFrames, int PeakBufferedFrames,
    long ReceivedFrames, long DeliveredFrames, long MissingFrames, long DroppedFrames, long LateFrames, long OverflowEvents,
    long OverlappingFrames = 0)
{
    public double BufferedMilliseconds => BufferedFrames / 48.0;
    public double CapacityMilliseconds => CapacityFrames / 48.0;
    public double PeakBufferedMilliseconds => PeakBufferedFrames / 48.0;
    public double DroppedMilliseconds => DroppedFrames / 48.0;
    public double MissingMilliseconds => MissingFrames / 48.0;
}

// Номера кадров сохраняют место пропуска: следующий звук не сдвигается при заполнении очереди.
public sealed class AudioTimelineBuffer
{
    public const int SampleRate = 48000;
    public const int FrameBytes = 8;
    private readonly byte[] _data;
    private readonly long[] _positions;
    private long _readFrame, _received, _delivered, _missing, _dropped, _late, _overflows, _overlapping;
    private int _buffered, _peak;

    public AudioTimelineBuffer(int capacityFrames = SampleRate * 2)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacityFrames);
        _data = new byte[checked(capacityFrames * FrameBytes)];
        _positions = new long[capacityFrames];
        Array.Fill(_positions, -1);
    }

    public AudioBufferMetrics Metrics => new(_positions.Length, _buffered, _peak,
        _received, _delivered, _missing, _dropped, _late, _overflows, _overlapping);
    public long DroppedFrames => _dropped;

    public void Write(long startFrame, ReadOnlySpan<byte> samples, bool silent = false)
    {
        if (samples.Length % FrameBytes != 0) throw new ArgumentException("Нужны полные стереокадры float PCM.", nameof(samples));
        int frames = samples.Length / FrameBytes;
        _received += frames;
        bool overflow = false;
        for (int index = 0; index < frames; index++)
        {
            long frame = startFrame + index;
            if (frame < 0) continue;
            if (frame < _readFrame) { _dropped++; _late++; continue; }
            if (frame - _readFrame >= _positions.Length) { _dropped++; overflow = true; continue; }
            int slot = (int)(frame % _positions.Length);
            if (_positions[slot] != frame) _buffered++;
            else { _dropped++; _overlapping++; }
            Span<byte> destination = _data.AsSpan(slot * FrameBytes, FrameBytes);
            if (silent) destination.Clear(); else samples.Slice(index * FrameBytes, FrameBytes).CopyTo(destination);
            _positions[slot] = frame;
        }
        if (overflow) _overflows++;
        _peak = Math.Max(_peak, _buffered);
    }

    public void Read(long startFrame, Span<byte> destination)
    {
        if (startFrame != _readFrame || destination.Length % FrameBytes != 0)
            throw new ArgumentException("Выход должен читать последовательные полные PCM-кадры.");
        destination.Clear();
        int frames = destination.Length / FrameBytes;
        for (int index = 0; index < frames; index++)
        {
            long frame = startFrame + index;
            int slot = (int)(frame % _positions.Length);
            if (_positions[slot] != frame) { _missing++; continue; }
            _data.AsSpan(slot * FrameBytes, FrameBytes).CopyTo(destination.Slice(index * FrameBytes, FrameBytes));
            _positions[slot] = -1;
            _buffered--; _delivered++;
        }
        _readFrame += frames;
    }

    public void Clear()
    {
        Array.Fill(_positions, -1);
        _buffered = 0;
    }
}
