using System.Runtime.InteropServices;
using Wpf.Ui.Controls;

namespace Rewind.Preview;

public partial class OverlayWindow
{
    private ActionToastPresenter? _actionToasts;
    private bool _captureStartNoticePending;
    private bool _captureFailureNotified;

    internal void ShowActionToast(string key, string title, string detail, SymbolRegular symbol, string? file = null,
        bool error = false, bool persistent = false, IReadOnlyList<nint>? recordingMonitors = null,
        Func<string>? titleProvider = null, Func<string>? detailProvider = null)
    {
        if (_closing) return;
        try
        {
            _actionToasts ??= new ActionToastPresenter(OpenToastFile);
            nint[] monitors = recordingMonitors is { Count: > 1 } ? recordingMonitors.Where(IsConnectedMonitor).Distinct().ToArray() : [];
            if (monitors.Length == 0) monitors = [Native.PrimaryMonitor()];
            _actionToasts.Show(key, title, detail, symbol, monitors, file, error, persistent, titleProvider, detailProvider);
            _telemetry.Event("action-toast", new { key, title, error, persistent });
        }
        catch (Exception failure) { _telemetry.Event("action-toast-error", new { failure.Message }); }
    }

    private void ShowLocalizedActionToast(string key, string titleKey, Func<string> detail, SymbolRegular symbol, string? file = null,
        bool error = false, bool persistent = false, IReadOnlyList<nint>? recordingMonitors = null)
    {
        string Title() => Localization.Text(titleKey);
        ShowActionToast(key, Title(), detail(), symbol, file, error, persistent, recordingMonitors, Title, detail);
    }

    private static bool IsConnectedMonitor(nint monitor)
    {
        var info = new Native.MonitorInfo { Size = Marshal.SizeOf<Native.MonitorInfo>() };
        return Native.GetMonitorInfo(monitor, ref info);
    }

    private void OpenToastFile(string file)
    {
        try { ExplorerSelection.SelectFile(file); HideOverlay(restoreFocus: false); }
        catch (Exception error)
        {
            SetCaptureMessage(() => Localization.Text("Message.CouldNotShowReplay") + error.Message);
            ShowLocalizedActionToast("file-error", "Message.CouldNotShowReplaycde3da3b", () => error.Message, SymbolRegular.ErrorCircle24, error: true);
            _telemetry.Event("notification-open-error", new { error.Message });
        }
    }

    private void ObserveCaptureNotification(ReplayStatus? status)
    {
        if (_capture is null || status is null || _captureBusy) return;
        if (_captureStartNoticePending && status is { Running: true, Recovering: false, EncodedFrames: > 0 })
        {
            _captureStartNoticePending = false;
            ShowLocalizedActionToast("capture", "Message.RecordingStarted", () => "", SymbolRegular.Video24);
        }
        if (status is { Running: false, Error: string error } && !_captureFailureNotified)
        {
            _captureStartNoticePending = false; _captureFailureNotified = true;
            ShowLocalizedActionToast("capture", status.EncodedFrames > 0 ? "Message.RecordingInterrupted" : "Message.RecordingDidNotStart", () => error,
                SymbolRegular.ErrorCircle24, error: true);
        }
    }
}
