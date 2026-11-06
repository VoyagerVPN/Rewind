using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Rewind.Preview;

public partial class OverlayWindow
{
    private bool _syncingMonitorSwitches;
    private static string MonitorNumber(string name) => name.Replace(@"\\.\DISPLAY", "", StringComparison.OrdinalIgnoreCase);

    private string? RecordingMonitorName()
    {
        ReplayStatus? status = _capture?.Status;
        return status is { Running: true, Recovering: false, EncodedFrames: > 0 } ? _captureSettings?.MonitorDeviceName : null;
    }

    private string MonitorDescription(string name)
    {
        CaptureMonitor? monitor = _monitors.FirstOrDefault(monitor => monitor.DeviceName == name);
        return $"экран {MonitorNumber(name)}" + (monitor is null ? " · недоступен" : " · " + monitor.Name);
    }

    private CaptureSettings? AppliedSettingsForComparison()
    {
        if (_captureSettings is not CaptureSettings settings) return null;
        int[] markers = settings.ReplayMarkers ?? new ReplayIntervals(settings.HistorySeconds,
            new[] { 30, 60, 90, settings.ReplaySeconds }.Where(seconds => seconds <= settings.HistorySeconds), settings.ReplaySeconds).Markers.ToArray();
        return settings with { ReplayMarkers = markers,
            PreferredReplaySeconds = settings.PreferredReplaySeconds ?? settings.ReplaySeconds,
            MonitorIdentity = settings.MonitorIdentity ?? _monitors.FirstOrDefault(monitor => monitor.DeviceName == settings.MonitorDeviceName)?.Identity };
    }

    private void OnDraftSelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        RefreshSettingsPresentation();
    }

    private void RenderMonitorSwitches() => UpdateMonitorRows();

    private void RefreshSettingsPresentation()
    {
        if (!_captureUiReady) return;
        string state = MonitorStateKey();
        if (_lastMonitorLayoutState != state) RenderMonitorLayout();

        CaptureSettings? candidate = null;
        string? invalid = null;
        try { candidate = ReadCaptureSettings(); }
        catch (ArgumentException error) { invalid = error.Message; }
        QueueAutomaticSettings(candidate);
        SettingsChangesText.Text = _capture is not null ? "Для изменения настроек останови повтор" : _settingsSaveError ?? invalid ?? "";
        SettingsChangesText.Visibility = string.IsNullOrEmpty(SettingsChangesText.Text) || HotkeysSettings.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
        SettingsSaveGlyph.Symbol = _settingsSaveError is not null || invalid is not null ? Wpf.Ui.Controls.SymbolRegular.Dismiss24 :
            _pendingAutoSettings is not null ? Wpf.Ui.Controls.SymbolRegular.ArrowClockwise24 : Wpf.Ui.Controls.SymbolRegular.Checkmark24;
        SettingsSaveStatus.ToolTip = _settingsSaveError ?? invalid ?? (_pendingAutoSettings is not null ? "Сохранение…" : "Настройки сохраняются автоматически");
        SettingsSaveStatus.Cursor = _settingsSaveError is not null ? System.Windows.Input.Cursors.Hand : System.Windows.Input.Cursors.Arrow;
        SettingsSaveStatus.IsEnabled = _settingsSaveError is not null;
        SettingsSaveStatus.Visibility = _settingsSaveError is not null || invalid is not null ? Visibility.Visible : Visibility.Hidden;
        QueueAudioMonitoring();
    }
}
