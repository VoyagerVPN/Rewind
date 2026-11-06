namespace Rewind.Preview;

public sealed class AudioMonitorSession : IAsyncDisposable
{
    private readonly SystemAudioPipe _audio;
    public AudioMonitorSession(CaptureSettings settings, uint? applicationOverride = null)
    { _audio = new SystemAudioPipe(settings, applicationOverride, meterOnly: true); }
    public AudioSourceStatus[] Status => _audio.Status;
    public ValueTask DisposeAsync() => _audio.DisposeAsync();
}
