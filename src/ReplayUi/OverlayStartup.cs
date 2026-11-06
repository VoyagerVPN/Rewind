using System.IO;
using System.Text.Json;
using System.Windows;

namespace Rewind.Preview;

public partial class OverlayWindow
{
    private StartupPreferences _startupPreferences = new();
    private StartupPreferencesFile _startupPreferencesFile = null!;
    private bool _startupPreferencesReady;

    private void InitializeStartupPreferences()
    {
        _startupPreferencesFile = new(Path.Combine(_diagnostics, "startup.json"));
        try { _startupPreferences = _startupPreferencesFile.Load(); }
        catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException)
        { SettingsMessageText.Text = "Не удалось прочитать параметры запуска: " + error.Message; }
        AlwaysRecordAtStartup.IsChecked = _startupPreferences.AlwaysStartRecording;
        _startupPreferencesReady = true;
    }

    private void OnAlwaysRecordAtStartupChanged(object sender, RoutedEventArgs args)
    {
        if (!_startupPreferencesReady) return;
        SaveStartupPreferences(_startupPreferences with { AlwaysStartRecording = AlwaysRecordAtStartup.IsChecked == true });
        _startupPreferencesReady = false;
        AlwaysRecordAtStartup.IsChecked = _startupPreferences.AlwaysStartRecording;
        _startupPreferencesReady = true;
    }

    private void RememberRecordingState(bool enabled)
    {
        if (_startupPreferencesReady && _startupPreferences.LastRecordingEnabled != enabled)
            SaveStartupPreferences(_startupPreferences with { LastRecordingEnabled = enabled });
    }

    private void SaveStartupPreferences(StartupPreferences preferences)
    {
        try { _startupPreferencesFile.Save(preferences); _startupPreferences = preferences; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            SettingsMessageText.Text = "Не удалось сохранить параметры запуска: " + error.Message;
            _telemetry.Event("startup-settings-error", new { error.Message });
        }
    }

    public async Task RestoreStartupRecordingAsync()
    {
        if (_startupPreferences.ShouldStartRecording && _capture is null) await ToggleCaptureAsync();
    }
}
