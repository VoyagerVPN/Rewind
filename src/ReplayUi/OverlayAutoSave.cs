using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace Rewind.Preview;

public partial class OverlayWindow
{
    private DispatcherTimer? _settingsSaveTimer;
    private CaptureSettings? _pendingAutoSettings;
    private CaptureSettings? _failedAutoSettings;
    private string? _settingsSaveError;

    private void InitializeAutomaticSettings()
    {
        _settingsSaveTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
            { Interval = TimeSpan.FromMilliseconds(450) };
        _settingsSaveTimer.Tick += (_, _) => SaveAutomaticSettings();
    }

    private void QueueAutomaticSettings(CaptureSettings? settings)
    {
        if (settings is null || settings.HasSameValues(AppliedSettingsForComparison()))
        {
            _pendingAutoSettings = null; _settingsSaveTimer?.Stop();
            _failedAutoSettings = null; _settingsSaveError = null;
            return;
        }
        if (_capture is not null || _captureBusy || settings.HasSameValues(_pendingAutoSettings) || settings.HasSameValues(_failedAutoSettings)) return;
        _pendingAutoSettings = settings; _settingsSaveError = null;
        _settingsSaveTimer?.Stop(); _settingsSaveTimer?.Start();
    }

    private void SaveAutomaticSettings()
    {
        _settingsSaveTimer?.Stop();
        if (!_captureUiReady || _capture is not null || _captureBusy) return;
        if (Mouse.Captured is not null || StorageInput.IsKeyboardFocusWithin || ShadowPlayPath.IsKeyboardFocusWithin)
        { _settingsSaveTimer?.Start(); return; }
        CaptureSettings? settings = null;
        try
        {
            settings = ReadCaptureSettings();
            if (!settings.HasSameValues(AppliedSettingsForComparison()))
            {
                bool storageChanged = _captureSettings?.StorageRoot != settings.StorageRoot;
                _settingsFile.Save(settings);
                _captureSettings = settings;
                if (storageChanged) { LoadSavedReplays(settings.StorageRoot); RenderClipCards(); }
            }
            _pendingAutoSettings = null; _failedAutoSettings = null; _settingsSaveError = null;
        }
        catch (ArgumentException) { _pendingAutoSettings = null; }
        catch (Exception error)
        {
            _failedAutoSettings = settings; _pendingAutoSettings = null;
            _settingsSaveError = "Не удалось сохранить настройки: " + error.Message;
            _telemetry.Event("settings-save-error", new { error.Message });
        }
        UpdateCaptureStatus();
    }

    private void OnRetrySettingsSave(object sender, RoutedEventArgs args)
    { _failedAutoSettings = null; SaveAutomaticSettings(); }

    private void OnSettingsValueChanged(object sender, RoutedEventArgs args) => RefreshSettingsPresentation();
    private void OnSettingsTextChanged(object sender, TextChangedEventArgs args) => RefreshSettingsPresentation();
    private void OnStoragePathCommitted(object sender, KeyboardFocusChangedEventArgs args) => RefreshSettingsPresentation();
    private void OnStoragePathKeyDown(object sender, KeyEventArgs args)
    {
        if (args.Key != Key.Enter) return;
        StorageInput.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next)); args.Handled = true;
    }
}
