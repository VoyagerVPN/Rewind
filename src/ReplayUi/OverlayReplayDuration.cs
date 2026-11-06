using System.Diagnostics;
using System.Windows.Threading;

namespace Rewind.Preview;

public partial class OverlayWindow
{
    private const uint ReplayCancelKey = 0x43;

    private sealed record PendingReplayDuration(DesktopReplaySession Capture, ReplayDurationSelection Selection,
        long EndTimestamp, nint Foreground, string[]? Screens, IDisposable[] History, ReplayDurationWindow Window);

    private PendingReplayDuration? _pendingReplayDuration;
    private LowLevelKeyboardHook? _replayDurationHook;

    private async void BeginReplayHotkey(HotkeyChord chord, bool allScreens, bool nativeMessage)
    {
        if (_pendingReplayDuration is not null || _closing) return;
        uint delay = nativeMessage ? unchecked((uint)Environment.TickCount - (uint)Native.GetMessageTime()) : 0;
        long endTimestamp = Stopwatch.GetTimestamp() - (long)(delay * (Stopwatch.Frequency / 1000d));
        nint foreground = Native.GetForegroundWindow();
        nint primary = Native.PrimaryMonitor();
        string[]? screens = null;
        var selection = new ReplayDurationSelection(_intervals);
        try
        {
            DesktopReplaySession capture = _capture ?? throw new InvalidOperationException("Запись сейчас выключена.");
            if (!allScreens)
            {
                CaptureMonitor? monitor = _monitors.FirstOrDefault(candidate => candidate.Handle == primary) ??
                    MonitorCatalog.Read().FirstOrDefault(candidate => candidate.Handle == primary);
                if (monitor is null) throw new InvalidOperationException("Не удалось определить основной экран Windows.");
                screens = [monitor.DeviceName];
            }
            if ((chord.Modifiers & 1) == 0 || !LowLevelKeyboardHook.Down(0x12))
            {
                await SaveReplayAsync(selection.Seconds, allScreens, screens, endTimestamp, foreground);
                return;
            }

            var window = new ReplayDurationWindow(selection);
            IDisposable[] history;
            try { history = capture.HoldReplayHistory(endTimestamp, selection.Markers[^1], screens); }
            catch { window.Close(); throw; }
            var pending = new PendingReplayDuration(capture, selection, endTimestamp, foreground, screens, history, window);
            _pendingReplayDuration = pending;
            var pressed = new HashSet<uint>(new uint[] { 0x78, 0x7a, ReplayCancelKey }.Where(LowLevelKeyboardHook.Down));
            _replayDurationHook = new LowLevelKeyboardHook((key, down) =>
            {
                if (!down && (key == 0xa4 && !LowLevelKeyboardHook.Down(0xa5) ||
                    key == 0xa5 && !LowLevelKeyboardHook.Down(0xa4) || key == 0x12))
                {
                    Dispatcher.BeginInvoke(DispatcherPriority.Input, () => FinishReplayDurationSelection(pending));
                    return false;
                }
                if (key is not (0x78 or 0x7a or ReplayCancelKey) || key == ReplayCancelKey && !LowLevelKeyboardHook.Down(0x12))
                    return false;
                if (!down) pressed.Remove(key);
                else if (key == ReplayCancelKey)
                {
                    if (pressed.Add(key))
                    {
                        selection.Cancel();
                        Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
                        {
                            if (ReferenceEquals(pending, _pendingReplayDuration)) CancelReplayDurationSelection();
                        });
                    }
                }
                else if (pressed.Add(key)) Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
                {
                    if (ReferenceEquals(pending, _pendingReplayDuration) && selection.Step(key == 0x78 ? -1 : 1))
                        window.UpdateSelection();
                });
                return true;
            });
            // Alt мог быть отпущен между WM_HOTKEY и установкой перехватчика.
            if (!LowLevelKeyboardHook.Down(0x12))
            {
                FinishReplayDurationSelection(pending);
                return;
            }
            window.ShowOnMonitor(foreground == 0 ? primary : Native.MonitorFromWindow(foreground, 2));
        }
        catch (Exception error)
        {
            CancelReplayDurationSelection();
            ReportReplaySaveError(error, "save-" + Guid.NewGuid().ToString("N"), allScreens, selection.Seconds, screens);
        }
    }

    private async void FinishReplayDurationSelection(PendingReplayDuration pending)
    {
        if (!ReferenceEquals(pending, _pendingReplayDuration)) return;
        int? seconds = pending.Selection.Complete();
        CloseReplayDurationSelection(pending);
        try
        {
            if (seconds is int duration && ReferenceEquals(pending.Capture, _capture))
                await SaveReplayAsync(duration, pending.Screens is null, pending.Screens, pending.EndTimestamp, pending.Foreground);
        }
        finally { ReleaseReplayDurationHistory(pending); }
    }

    private void CancelReplayDurationSelection()
    {
        if (_pendingReplayDuration is not PendingReplayDuration pending) return;
        CloseReplayDurationSelection(pending);
        ReleaseReplayDurationHistory(pending);
    }

    private void CloseReplayDurationSelection(PendingReplayDuration pending)
    {
        _pendingReplayDuration = null;
        _replayDurationHook?.Dispose();
        _replayDurationHook = null;
        pending.Window.Close();
    }

    private void ReleaseReplayDurationHistory(PendingReplayDuration pending)
    {
        foreach (IDisposable hold in pending.History)
        {
            try { hold.Dispose(); }
            catch (System.IO.IOException error) { _telemetry.Event("replay-history-release-error", new { error.Message }); }
            catch (UnauthorizedAccessException error) { _telemetry.Event("replay-history-release-error", new { error.Message }); }
        }
    }
}
