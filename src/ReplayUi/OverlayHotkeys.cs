using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Input;

namespace Rewind.Preview;

public partial class OverlayWindow
{
    private HotkeySettings _hotkeys = HotkeySettings.Default;
    private HotkeySettingsFile _hotkeyFile = null!;
    private readonly Dictionary<HotkeyAction, (int Id, HotkeyChord Chord, HeldKeyHotkey? Watcher)> _activeHotkeys = [];
    private readonly Dictionary<HotkeyAction, string> _hotkeyIssues = [];
    private nint _hotkeyHandle;
    private bool _syncingHotkeys;
    private bool _capturingHotkey;

    private void InitializeHotkeys()
    {
        _hotkeyFile = new HotkeySettingsFile(Path.Combine(_diagnostics, "hotkeys.json"));
        try { _hotkeys = _hotkeyFile.Load(); }
        catch (Exception error) when (error is IOException or JsonException or ArgumentException or UnauthorizedAccessException)
        { HotkeysMessage.Text = "Не удалось загрузить сочетания: " + error.Message; }
        foreach (HotkeyAction action in Enum.GetValues<HotkeyAction>())
        {
            EditorFor(action).CaptureStarted += (_, _) => SuspendHotkeysForInput();
            EditorFor(action).CaptureEnded += (_, _) => ResumeHotkeysAfterInput();
        }
        foreach (HotkeyEditor editor in PlayerHotkeyEditors.Children.OfType<Grid>().SelectMany(row => row.Children.OfType<HotkeyEditor>()))
        {
            editor.CaptureStarted += (_, _) => SuspendHotkeysForInput();
            editor.CaptureEnded += (_, _) => ResumeHotkeysAfterInput();
        }
        SettingsHotkey.CaptureStarted += (_, _) => SuspendHotkeysForInput();
        SettingsHotkey.CaptureEnded += (_, _) => ResumeHotkeysAfterInput();
        RenderHotkeys();
    }

    private void RegisterHotkeys(nint handle)
    {
        _hotkeyHandle = handle;
        foreach (HotkeyAction action in Enum.GetValues<HotkeyAction>())
        {
            HotkeyChord? chord = _hotkeys.Get(action);
            if (chord is null) continue;
            try { RegisterChord(action, (int)action, chord); }
            catch (Win32Exception error) { SetHotkeyError(action, error); }
        }
        RenderHotkeys();
    }

    private void RegisterChord(HotkeyAction action, int id, HotkeyChord chord)
    {
        _activeHotkeys[action] = (id, chord, ReserveChord(id, chord));
    }

    private HeldKeyHotkey? ReserveChord(int id, HotkeyChord chord)
    {
        if (chord.HeldKey is not null)
        {
            HeldKeyHotkey? watcher = null;
            watcher = new HeldKeyHotkey(chord, () => Dispatcher.BeginInvoke(new Action(() =>
            {
                if (watcher is { IsDisposed: false }) DispatchHotkey(id);
            })));
            return watcher;
        }
        if (!Native.RegisterHotKey(_hotkeyHandle, id, chord.Modifiers | 0x4000, chord.Key))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        return null;
    }

    private void ReleaseChord((int Id, HotkeyChord Chord, HeldKeyHotkey? Watcher) registration)
    {
        if (registration.Watcher is not null) registration.Watcher.Dispose();
        else Native.UnregisterHotKey(_hotkeyHandle, registration.Id);
    }

    private void UnregisterHotkeys()
    {
        CancelReplayDurationSelection();
        foreach (var registration in _activeHotkeys.Values) ReleaseChord(registration);
        _activeHotkeys.Clear(); _hotkeyHandle = 0;
    }

    private void SetHotkeyError(HotkeyAction action, Win32Exception error, HotkeyChord? attempted = null)
    {
        _hotkeyIssues[action] = error.NativeErrorCode == 1409
            ? "Сочетание уже зарегистрировано в Windows (1409). Выбери другое или повтори попытку."
            : $"Windows не зарегистрировала сочетание ({error.NativeErrorCode}): {error.Message}";
        _telemetry.Event("hotkey-register-error", new { action = action.ToString(), combination = (attempted ?? _hotkeys.Get(action))?.Label,
            code = error.NativeErrorCode, error.Message });
    }

    private HotkeyEditor EditorFor(HotkeyAction action) => action switch
    { HotkeyAction.Panel => PanelHotkey, HotkeyAction.Save => SaveHotkey, HotkeyAction.SaveAll => SaveAllHotkey, HotkeyAction.CaptureToggle => CaptureToggleHotkey, _ => throw new ArgumentOutOfRangeException(nameof(action)) };
    private TextBlock StatusFor(HotkeyAction action) => action switch
    { HotkeyAction.Panel => PanelHotkeyStatus, HotkeyAction.Save => SaveHotkeyStatus, HotkeyAction.SaveAll => SaveAllHotkeyStatus, HotkeyAction.CaptureToggle => CaptureToggleHotkeyStatus, _ => throw new ArgumentOutOfRangeException(nameof(action)) };

    private void RenderHotkeys()
    {
        _syncingHotkeys = true;
        try
        {
            foreach (HotkeyAction action in Enum.GetValues<HotkeyAction>())
            {
                EditorFor(action).Chord = _hotkeys.Get(action);
                TextBlock status = StatusFor(action);
                status.Text = _hotkeyIssues.GetValueOrDefault(action) ?? "";
                status.Visibility = status.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
            }
            SaveReplayButton.ToolTip = "Экран под курсором";
            SaveAllScreensButton.ToolTip = "Все записываемые экраны" + (_hotkeys.SaveAll is null ? "" : " · " + _hotkeys.SaveAll.Label);
            HidePanelButton.ToolTip = "Скрыть панель" + (_hotkeys.Panel is null ? "" : " · " + _hotkeys.Panel.Label);
            SettingsHotkey.Chord = _hotkeys.Settings;
            SettingsButton.ToolTip = "Настройки" + (_hotkeys.Settings is null ? "" : " · " + _hotkeys.Settings.Label);
            CaptureToggle.ToolTip = CaptureToggleText.Text + (_hotkeys.CaptureToggle is null ? "" : " · " + _hotkeys.CaptureToggle.Label);
            HotkeysMessage.Visibility = string.IsNullOrEmpty(HotkeysMessage.Text) ? Visibility.Collapsed : Visibility.Visible;
            foreach (HotkeyEditor editor in PlayerHotkeyEditors.Children.OfType<Grid>().SelectMany(row => row.Children.OfType<HotkeyEditor>()))
                editor.Chord = _hotkeys.PlayerBindings.Get(Enum.Parse<PlayerHotkeyAction>((string)editor.Tag));
        }
        finally { _syncingHotkeys = false; }
    }

    private void OnHotkeyChanged(object? sender, EventArgs args)
    {
        if (_syncingHotkeys || sender is not HotkeyEditor editor) return;
        ApplyHotkey(Enum.Parse<HotkeyAction>((string)editor.Tag), editor.Chord);
    }

    private void OnPlayerHotkeyChanged(object? sender, EventArgs args)
    {
        if (_syncingHotkeys || sender is not HotkeyEditor editor) return;
        try
        {
            var candidate = _hotkeys with { Player = _hotkeys.PlayerBindings.With(Enum.Parse<PlayerHotkeyAction>((string)editor.Tag), editor.Chord) };
            _hotkeyFile.Save(candidate); _hotkeys = candidate; _player?.SetHotkeys(candidate.PlayerBindings); HotkeysMessage.Text = "";
        }
        catch (Exception error) when (error is ArgumentException or IOException or UnauthorizedAccessException) { HotkeysMessage.Text = error.Message; }
        RenderHotkeys();
    }

    private void OnSettingsHotkeyChanged(object? sender, EventArgs args)
    {
        if (_syncingHotkeys) return;
        try
        {
            var candidate = _hotkeys with { Settings = SettingsHotkey.Chord };
            _hotkeyFile.Save(candidate); _hotkeys = candidate; HotkeysMessage.Text = "";
        }
        catch (Exception error) when (error is ArgumentException or IOException or UnauthorizedAccessException) { HotkeysMessage.Text = error.Message; }
        RenderHotkeys();
    }

    private void OnResetSettingsHotkey(object sender, RoutedEventArgs args) => SettingsHotkey.Chord = HotkeySettings.Default.Settings;

    private void OnPreviewKeyDown(object sender, KeyEventArgs args)
    {
        if (args.IsRepeat || (Keyboard.Modifiers & ModifierKeys.Windows) != 0) return;
        Key key = args.Key == Key.System ? args.SystemKey : args.Key;
        uint mask = ((Keyboard.Modifiers & ModifierKeys.Control) != 0 ? 2u : 0) |
            ((Keyboard.Modifiers & ModifierKeys.Alt) != 0 ? 1u : 0) | ((Keyboard.Modifiers & ModifierKeys.Shift) != 0 ? 4u : 0);
        if (HandlePanelShortcut(new HotkeyChord(mask, (uint)KeyInterop.VirtualKeyFromKey(key)))) args.Handled = true;
    }

    public bool HandlePanelShortcut(HotkeyChord chord)
    {
        if (_capturingHotkey || _closing || chord != _hotkeys.Settings) return false;
        if (_libraryVisible) CloseLibrary();
        if (SettingsView.Visibility != Visibility.Visible) OnSettings(this, new RoutedEventArgs());
        return true;
    }

    private void CancelPlayerKeyCapture()
    {
        SettingsHotkey.CancelCapture();
        foreach (HotkeyEditor editor in PlayerHotkeyEditors.Children.OfType<Grid>().SelectMany(row => row.Children.OfType<HotkeyEditor>())) editor.CancelCapture();
    }

    private void OnResetPlayerHotkey(object sender, RoutedEventArgs args)
    {
        string action = (string)((Button)sender).Tag;
        HotkeyEditor editor = PlayerHotkeyEditors.Children.OfType<Grid>().SelectMany(row => row.Children.OfType<HotkeyEditor>()).Single(editor => (string)editor.Tag == action);
        editor.Chord = PlayerHotkeySettings.Default.Get(Enum.Parse<PlayerHotkeyAction>(action));
    }

    private void ApplyHotkey(HotkeyAction action, HotkeyChord? chord)
    {
        HotkeySettings candidate = _hotkeys.With(action, chord);
        bool hadPrevious = _activeHotkeys.TryGetValue(action, out var previous);
        int stagedId = hadPrevious && previous.Id == (int)action ? (int)action + 10 : (int)action;
        bool staged = false;
        HeldKeyHotkey? stagedWatcher = null;
        bool committed = false;
        try
        {
            candidate.Validate();
            if (_hotkeyHandle != 0 && chord is not null && (!hadPrevious || previous.Chord != chord))
            {
                stagedWatcher = ReserveChord(stagedId, chord);
                staged = true;
            }
            // Старое сочетание остаётся рабочим, пока новое не зарегистрировано и не сохранено.
            _hotkeyFile.Save(candidate);
            if (hadPrevious && (staged || chord is null)) ReleaseChord(previous);
            if (staged) _activeHotkeys[action] = (stagedId, chord!, stagedWatcher);
            else if (chord is null) _activeHotkeys.Remove(action);
            _hotkeys = candidate; _hotkeyIssues.Remove(action); HotkeysMessage.Text = "";
            committed = true;
        }
        catch (Win32Exception error) { SetHotkeyError(action, error, chord); }
        catch (Exception error) when (error is ArgumentException or IOException or UnauthorizedAccessException)
        { _hotkeyIssues[action] = error is ArgumentException ? error.Message : "Не удалось сохранить: " + error.Message; }
        finally
        {
            if (staged && !committed) ReleaseChord((stagedId, chord!, stagedWatcher));
            RenderHotkeys();
        }
    }

    private void OnResetHotkey(object sender, RoutedEventArgs args)
    { var action = Enum.Parse<HotkeyAction>((string)((Button)sender).Tag); ApplyHotkey(action, HotkeySettings.Default.Get(action)); }
    private void OnClearHotkey(object sender, RoutedEventArgs args)
    { ApplyHotkey(Enum.Parse<HotkeyAction>((string)((Button)sender).Tag), null); }
    private void OnRetryHotkeys(object sender, RoutedEventArgs args)
    {
        foreach (HotkeyAction action in Enum.GetValues<HotkeyAction>())
            if (_hotkeyIssues.ContainsKey(action)) ApplyHotkey(action, _hotkeys.Get(action));
    }

    private void SuspendHotkeysForInput()
    {
        CancelReplayDurationSelection();
        _capturingHotkey = true;
        foreach (var registration in _activeHotkeys.Values) ReleaseChord(registration);
        _activeHotkeys.Clear();
    }

    private void ResumeHotkeysAfterInput()
    {
        _capturingHotkey = false;
        if (_hotkeyHandle == 0) return;
        foreach (HotkeyAction action in Enum.GetValues<HotkeyAction>())
        {
            if (_activeHotkeys.ContainsKey(action) || _hotkeys.Get(action) is not HotkeyChord chord) continue;
            try { RegisterChord(action, (int)action, chord); }
            catch (Win32Exception error) { SetHotkeyError(action, error); }
        }
        RenderHotkeys();
    }

    private bool DispatchHotkey(int id, bool nativeMessage = false)
    {
        if (_capturingHotkey) return true;
        var binding = _activeHotkeys.FirstOrDefault(pair => pair.Value.Id == id);
        if (binding.Value.Chord is not HotkeyChord chord) return false;
        HotkeyAction action = binding.Key;
        if (action is HotkeyAction.Save or HotkeyAction.SaveAll)
            BeginReplayHotkey(chord, allScreens: action == HotkeyAction.SaveAll, nativeMessage);
        else if (action == HotkeyAction.CaptureToggle)
        {
            if (CaptureToggle.IsEnabled) OnCaptureToggle(this, new RoutedEventArgs());
        }
        else
        {
            uint queueDelay = unchecked((uint)Environment.TickCount - (uint)Native.GetMessageTime());
            _telemetry.Event("hotkey", new { queueMilliseconds = queueDelay, opening = !IsVisible });
            if (IsVisible) HideOverlay(); else ShowOverlay();
        }
        return true;
    }
}
