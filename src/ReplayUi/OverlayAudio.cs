using System.Diagnostics;
using System.Windows;

namespace Rewind.Preview;

public partial class OverlayWindow
{
    private DateTime _lastOutputRefresh;
    private void RefreshAudioDevices()
    {
        if (_capture is not null) return;
        string? selected = MicrophonePicker.SelectedItem is AudioDeviceChoice choice ? choice.Id : _captureSettings?.MicrophoneDeviceId;
        try
        {
            AudioDeviceChoice[] devices = AudioDeviceCatalog.Microphones();
            MicrophonePicker.ItemsSource = devices;
            MicrophonePicker.SelectedItem = devices.FirstOrDefault(device => device.Id == selected);
            if (MicrophonePicker.SelectedItem is null)
            {
                var missing = new AudioDeviceChoice(selected, "Выбранный микрофон недоступен · переподключи устройство");
                MicrophonePicker.ItemsSource = devices.Append(missing).ToArray();
                MicrophonePicker.SelectedItem = missing;
            }
        }
        catch (Exception error) { SettingsMessageText.Text = "Не удалось получить устройства звука: " + error.Message; }
    }

    private void OnRefreshAudio(object sender, RoutedEventArgs args) => RefreshAudioDevices();

    private void OnWindowsSound(object sender, RoutedEventArgs args)
    {
        try { Process.Start(new ProcessStartInfo("ms-settings:sound") { UseShellExecute = true }); }
        catch (Exception error) { SettingsMessageText.Text = error.Message; }
    }

    private void UpdateAudioStatus()
    {
        if ((DateTime.UtcNow - _lastOutputRefresh).TotalSeconds >= 2)
        {
            try { WindowsSoundButton.ToolTip = "Звук Windows\nТекущий выход: " + AudioDeviceCatalog.DefaultOutput(); }
            catch (Exception) { WindowsSoundButton.ToolTip = "Звук Windows\nВыход недоступен; захват приложений не привязан к устройству."; }
            _lastOutputRefresh = DateTime.UtcNow;
        }
        bool recording = _capture?.Status is { Running: true, Recovering: false };
        AudioSourceStatus[] sources = recording ? _capture!.AudioStatus : _audioPreview?.Status ?? [];
        string[] names = ["system", "discord", "microphone"];
        System.Windows.Controls.TextBlock[] texts = [SystemSourceText, DiscordSourceText, MicrophoneSourceText];
        SystemAudioEnabled.ToolTip = DiscordAudioEnabled.IsChecked == true ? "Все приложения и выходы Windows, кроме Discord" : "Все приложения и выходы Windows, включая Discord";
        MicrophonePicker.IsEnabled = _capture is null && !_captureBusy && MicrophoneEnabled.IsChecked == true;
        for (int index = 0; index < names.Length; index++)
        {
            AudioSourceStatus? source = sources.FirstOrDefault(source => source.Source == names[index]);
            texts[index].Text = source?.Warning ?? "";
            texts[index].Visibility = source?.Warning is null ? Visibility.Collapsed : Visibility.Visible;
        }
        string[] warnings = (recording ? sources : []).Where(source => source.Warning is not null)
            .Select(source => AudioCaptureDiagnostics.SourceName(source.Source) + ": " + source.Warning).ToArray();
        AudioCaptureStatusText.Text = warnings.Length > 0 ? string.Join("\n", warnings) : "";
        AudioCaptureStatusText.Visibility = warnings.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateAudioMeters();
    }

    private void UpdateAudioMeters()
    {
        AudioSourceStatus[] sources = _capture?.AudioStatus ?? _audioPreview?.Status ?? [];
        string[] names = ["system", "discord", "microphone"];
        AudioLevelBar[] levels = [SystemLevel, DiscordLevel, MicrophoneLevel];
        bool[] enabled = [SystemAudioEnabled.IsChecked == true, DiscordAudioEnabled.IsChecked == true, MicrophoneEnabled.IsChecked == true];
        for (int index = 0; index < names.Length; index++)
        {
            AudioSourceStatus? source = sources.FirstOrDefault(source => source.Source == names[index]);
            levels[index].Peak = source?.Peak ?? 0;
            levels[index].Visibility = enabled[index] ? Visibility.Visible : Visibility.Collapsed;
            string decibels = source is { Peak: > 0 } ? $"{20 * Math.Log10(source.Peak):F0} dBFS" : "−∞ dBFS";
            levels[index].ToolTip = (source?.Device ?? "Источник выключен") + " · " + decibels;
        }
    }
}
