namespace Rewind.Preview;

// QPC задаёт общую точку отсчёта, счётчик устройства сохраняет непрерывность соседних пакетов.
public sealed class AudioPacketTimeline(long epochQpc100ns)
{
    private bool _initialized;
    private long _deviceOrigin, _outputOrigin, _lastDevice, _next;
    private int _mismatchPackets, _mismatchDirection;
    public long Corrections { get; private set; }

    public long Position(long deviceFrame, long qpc100ns, int frames, bool discontinuity)
    {
        if (frames == 0) return _next;
        long timestampFrame = (long)Math.Round((qpc100ns - epochQpc100ns) * (48000.0 / 10_000_000));
        long expected = _initialized && deviceFrame == _lastDevice ? _next : _outputOrigin + deviceFrame - _deviceOrigin;
        long difference = timestampFrame - expected;
        if (Math.Abs(difference) > 240)
        {
            int direction = Math.Sign(difference);
            _mismatchPackets = direction == _mismatchDirection ? _mismatchPackets + 1 : 1;
            _mismatchDirection = direction;
        }
        else _mismatchPackets = 0;
        // Единичный выброс QPC не должен разрывать звук; устойчивый сдвиг возвращаем к общей шкале.
        if (!_initialized || discontinuity || deviceFrame < _lastDevice || _mismatchPackets >= 8)
        {
            if (_initialized) Corrections++;
            _deviceOrigin = deviceFrame; _outputOrigin = timestampFrame;
            expected = timestampFrame; _initialized = true;
            _mismatchPackets = 0;
        }
        _lastDevice = deviceFrame; _next = expected + frames;
        return expected;
    }
}
