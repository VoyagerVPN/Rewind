using System.Windows;
using System.Windows.Threading;

namespace Rewind.Preview;

public partial class OverlayWindow
{
    private AudioMonitorSession? _audioPreview;
    private CaptureSettings? _previewSettings, _desiredPreviewSettings;
    private Task _previewRefresh = Task.CompletedTask;
    private DispatcherTimer? _audioMeterTimer;

    private void InitializeAudioMonitoring()
    {
        _audioMeterTimer = new DispatcherTimer(DispatcherPriority.Render, Dispatcher) { Interval = TimeSpan.FromMilliseconds(1000d / 30) };
        _audioMeterTimer.Tick += (_, _) => UpdateAudioMeters();
        IsVisibleChanged += (_, _) => QueueAudioMonitoring();
        QueueAudioMonitoring();
    }

    private void QueueAudioMonitoring()
    {
        if (!_captureUiReady || _audioMeterTimer is null) return;
        bool visible = IsVisible && AudioSettings.Visibility == Visibility.Visible && !_closing;
        if (visible) _audioMeterTimer.Start(); else _audioMeterTimer.Stop();
        bool enabled = SystemAudioEnabled.IsChecked == true || DiscordAudioEnabled.IsChecked == true || MicrophoneEnabled.IsChecked == true;
        _desiredPreviewSettings = visible && enabled && _capture is null && !_captureBusy
            ? new CaptureSettings("audio-preview", 30, 4, 1, _diagnostics,
                CaptureSystemAudio: SystemAudioEnabled.IsChecked == true, SeparateDiscord: DiscordAudioEnabled.IsChecked == true,
                CaptureMicrophone: MicrophoneEnabled.IsChecked == true, MicrophoneDeviceId: (MicrophonePicker.SelectedItem as AudioDeviceChoice)?.Id)
            : null;
        if (_previewRefresh.IsCompleted) _previewRefresh = RefreshAudioPreviewAsync();
    }

    private async Task RefreshAudioPreviewAsync()
    {
        try
        {
            while (_previewSettings != _desiredPreviewSettings)
            {
                AudioMonitorSession? previous = _audioPreview;
                _audioPreview = null; _previewSettings = null;
                if (previous is not null) await previous.DisposeAsync();
                CaptureSettings? desired = _desiredPreviewSettings;
                if (desired is not null)
                {
                    _audioPreview = new AudioMonitorSession(desired); _previewSettings = desired;
                }
            }
        }
        catch (Exception error)
        {
            _previewSettings = _desiredPreviewSettings;
            SettingsMessageText.Text = Localization.Text("Message.AudioMetersAreUnavailable") + error.Message;
            _telemetry.Event("audio-monitor-error", new { error.Message });
        }
        UpdateAudioStatus();
    }

    private async Task StopAudioMonitoringAsync()
    {
        _desiredPreviewSettings = null;
        if (_previewRefresh.IsCompleted) _previewRefresh = RefreshAudioPreviewAsync();
        await _previewRefresh;
    }
}
