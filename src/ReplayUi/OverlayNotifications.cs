using System.Runtime.InteropServices;
using Wpf.Ui.Controls;

namespace Rewind.Preview;

public partial class OverlayWindow
{
    private ActionToastPresenter? _actionToasts;
    private bool _captureStartNoticePending;
    private bool _captureFailureNotified;

    internal void ShowActionToast(string key, string title, string detail, SymbolRegular symbol, string? file = null,
        bool error = false, bool persistent = false, IReadOnlyList<nint>? recordingMonitors = null)
    {
        if (_closing) return;
        try
        {
            _actionToasts ??= new ActionToastPresenter(OpenToastFile);
            nint[] monitors = recordingMonitors is { Count: > 1 } ? recordingMonitors.Where(IsConnectedMonitor).Distinct().ToArray() : [];
            if (monitors.Length == 0) monitors = [Native.PrimaryMonitor()];
            _actionToasts.Show(key, title, detail, symbol, monitors, file, error, persistent);
            _telemetry.Event("action-toast", new { key, title, error, persistent });
        }
        catch (Exception failure) { _telemetry.Event("action-toast-error", new { failure.Message }); }
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
            CaptureMessageText.Text = "Не удалось показать повтор: " + error.Message;
            ShowActionToast("file-error", "Не удалось показать повтор", error.Message, SymbolRegular.ErrorCircle24, error: true);
            _telemetry.Event("notification-open-error", new { error.Message });
        }
    }

    private void ObserveCaptureNotification(ReplayStatus? status)
    {
        if (_capture is null || status is null || _captureBusy) return;
        if (_captureStartNoticePending && status is { Running: true, Recovering: false, EncodedFrames: > 0 })
        {
            _captureStartNoticePending = false;
            ShowActionToast("capture", "Запись включена", "", SymbolRegular.Video24);
        }
        if (status is { Running: false, Error: string error } && !_captureFailureNotified)
        {
            _captureStartNoticePending = false; _captureFailureNotified = true;
            ShowActionToast("capture", status.EncodedFrames > 0 ? "Запись прервалась" : "Запись не запустилась", error,
                SymbolRegular.ErrorCircle24, error: true);
        }
    }
}
