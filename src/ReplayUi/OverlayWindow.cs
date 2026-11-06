using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Rewind.Preview;

public partial class OverlayWindow : Window
{
    private readonly UiTelemetry _telemetry;
    private readonly string _diagnostics;
    private readonly uint _showInstanceMessage;
    private ReplayPlayerView? _player;
    private readonly ReplayAudioOutput _playerAudioOutput = new();
    private readonly HashSet<Task> _closingPlayers = [];
    private CancellationTokenSource? _openClipCancellation;
    private int _openClipVersion;
    private bool _closing;
    private long? _pendingOpen;
    private nint _previousForeground;
    private OutsideClickWatcher? _outsideClicks;
    private bool _recentViewDismissed;
    private readonly LanguagePreferencesFile _languagePreferencesFile;
    private bool _languageReady;

    public OverlayWindow(Clip[] clips, string diagnostics, string? replayDirectory = null)
    {
        _languagePreferencesFile = new LanguagePreferencesFile(Path.Combine(diagnostics, "language.json"));
        string language;
        string? languageError = null;
        try { language = _languagePreferencesFile.Load(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            language = System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ru" ? "ru" : "en";
            languageError = error.Message;
        }
        UiLanguage.Apply(language);
        InitializeComponent();
        TestWindowActivation.Configure(this);
        InterfaceLanguagePicker.SelectedIndex = language == "ru" ? 1 : 0;
        _languageReady = true;
        if (languageError is not null) SettingsMessageText.Text = Localization.Text("Settings.LanguageLoadFailed") + " " + languageError;
        Title = "Rewind · " + AppVersion.Display;
        _diagnostics = diagnostics;
        _showInstanceMessage = Native.RegisterWindowMessage("Rewind.Show." + AppLaunchOptions.InstanceId(diagnostics));
        _telemetry = new UiTelemetry(Dispatcher, diagnostics, () => IsVisible || (_player?.IsVisible ?? false));
        InitializeCapture(clips, replayDirectory);
        InitializeHotkeys();
        InitializeStartupPreferences();
        InitializeAudioMonitoring();
        InitializeLibrary();
        RecentViewPopup.Closed += (_, _) =>
        {
            // При повторном нажатии Popup закрывается до Click своей кнопки.
            Point point = Mouse.GetPosition(RecentViewButton);
            _recentViewDismissed = Mouse.LeftButton == MouseButtonState.Pressed && new Rect(RecentViewButton.RenderSize).Contains(point);
        };
        SourceInitialized += (_, _) =>
        {
            HwndSource source = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
            source.AddHook(Hotkey);
            RegisterHotkeys(source.Handle);
            InitializeTray(source.Handle);
            DockToPrimaryMonitor();
        };
        Loaded += (_, _) => { _pendingOpen = Stopwatch.GetTimestamp(); CompositionTarget.Rendering += OnRendering; RenderClipCards(); };
        ContentRendered += (_, _) => DockToPrimaryMonitor();
        IsVisibleChanged += (_, _) => UpdateOutsideClickWatcher();
        Closed += (_, _) =>
        {
            _closing = true;
            ++_nativeOpeningVersion;
            StopInterfaceMotion();
            _usageCancellation?.Cancel();
            _outsideClicks?.Dispose(); _outsideClicks = null;
            UnregisterHotkeys();
            _captureTimer?.Stop();
            _settingsSaveTimer?.Stop();
            _audioMeterTimer?.Stop();
            _scanCancellation?.Cancel(); _previewCancellation?.Cancel();
            _catalogRefreshTimer?.Stop();
            _libraryIndex?.Dispose();
            _galleryPreviews.Clear(); _galleryPreviewOrder.Clear();
            _galleryWarmLoads.Clear();
            if (_libraryReady) SaveLibraryPreferences();
            _tray?.Dispose();
            _actionToasts?.Dispose();
            CompositionTarget.Rendering -= OnRendering;
            _playerAudioOutput.Dispose();
            _telemetry.Dispose();
        };
        Closing += OnCaptureClosing;
    }

    private void OnInterfaceLanguageChanged(object sender, SelectionChangedEventArgs args)
    {
        if (!_languageReady || InterfaceLanguagePicker.SelectedItem is not ComboBoxItem { Tag: string language } || language == Localization.Language) return;
        try
        {
            _languagePreferencesFile.Save(language);
            UiLanguage.Apply(language);
            RefreshLanguagePresentation();
            SettingsMessageText.Text = "";
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            _languageReady = false;
            InterfaceLanguagePicker.SelectedIndex = Localization.Language == "ru" ? 1 : 0;
            _languageReady = true;
            SettingsMessageText.Text = Localization.Text("Settings.LanguageSaveFailed") + " " + error.Message;
        }
    }

    private void RefreshLanguagePresentation()
    {
        SettingsLabel.Text = SettingsView.Visibility == Visibility.Visible || _libraryVisible ? Localization.Text("Message.Back") : Localization.Text("Ui.Settings");
        VersionDisplay.ToolTip = AppVersion.Details;
        foreach (HotkeyEditor editor in new[] { PanelHotkey, SaveHotkey, SaveAllHotkey, CaptureToggleHotkey, SettingsHotkey }
                     .Concat(PlayerHotkeyEditors.Children.OfType<Grid>().SelectMany(row => row.Children.OfType<HotkeyEditor>())))
            editor.RefreshLanguage();
        RenderHotkeys();
        RenderMarkerRows();
        IntervalTimeline.Redraw();
        MonitorSwitches.Children.Clear();
        RenderMonitorLayout();
        bool ready = _captureUiReady;
        _captureUiReady = false;
        try { RefreshAudioDevices(force: true); }
        finally { _captureUiReady = ready; }
        _lastOutputRefresh = DateTime.MinValue;
        UpdateCaptureStatus();
        if (_captureMessageProvider is not null) CaptureMessageText.Text = _captureMessageProvider();
        RefreshLibraryLanguageOptions();
        _lastGalleryLayout = null;
        ApplyCatalogSnapshot();
        _player?.RefreshLanguage();
        _actionToasts?.RefreshLanguage();
    }

    private async void OpenClip(Clip clip) => await OpenClipAsync(clip);

    public async Task OpenClipAsync(Clip clip)
    {
        int version = ++_openClipVersion;
        _openClipCancellation?.Cancel();
        using var cancellation = new CancellationTokenSource(); _openClipCancellation = cancellation;
        if (_player is null)
        {
            var created = new ReplayPlayerView(_telemetry, _diagnostics, _playerAudioOutput);
            _player = created;
            UpdateOutsideClickWatcher();
            created.CloseRequested += async (_, _) => { if (_player == created) await ClosePlayerExtensionAsync(); };
            created.ExplorerOpened += (_, _) => { if (_player == created) HideOverlay(restoreFocus: false); };
            created.PlaybackHealthChanged += (_, _) => { if (created.CurrentVideo is string file) MarkPlaybackIssue(file); };
            created.ClipDeleted += async (_, args) => await OnPlayerClipDeletedAsync(created, args.File);
            PlayerContent.Content = created;
            PlayerHost.Visibility = Visibility.Visible;
        }
        ReplayPlayerView player = _player;
        player.PausePlayback();
        player.ShowLoading(clip);
        DockToPrimaryMonitor();
        try { if (_libraryReady) clip = await _libraryIndex.PreparePlaybackAsync(clip, cancellation.Token); if (player == _player && version == _openClipVersion) await player.LoadClipAsync(clip); }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception error) { if (player == _player && version == _openClipVersion && !player.HasPlaybackError) player.ShowError(Localization.Text("Message.CouldNotOpenTheClipTryOpeningItAgain")); _telemetry.Event("player-open-error", new { error.Message }); }
        finally { if (ReferenceEquals(_openClipCancellation, cancellation)) _openClipCancellation = null; }
        if (!_closing && player == _player && version == _openClipVersion && !TestWindowActivation.Enabled) player.Focus();
    }

    private async Task OnPlayerClipDeletedAsync(ReplayPlayerView origin, string file)
    {
        if (_player == origin) await ClosePlayerExtensionAsync();
        for (int index = _clips.Count - 1; index >= 0; index--)
        {
            Clip clip = _clips[index]; Clip[] remaining = (clip.Screens ?? [clip]).Where(screen => !screen.Video.Equals(file, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (remaining.Length == 0) _clips.RemoveAt(index);
            else _clips[index] = remaining[0] with { Screens = remaining.Length > 1 ? remaining : null };
        }
        try { await _libraryIndex.ScanAsync(CatalogRoots(), _clips.ToArray(), CancellationToken.None, true); ApplyCatalogSnapshot(); }
        catch (Exception error) { _telemetry.Event("player-delete-refresh-error", new { error.Message }); RenderClipCards(); }
    }

    public async Task ClosePlayerExtensionAsync()
    {
        ++_openClipVersion; _openClipCancellation?.Cancel();
        ReplayPlayerView? player = _player;
        if (player is null) return;
        // Освобождение декодера может ждать watchdog; интерфейс закрываем сразу.
        PlayerHost.Visibility = Visibility.Collapsed; _player = null;
        UpdateOutsideClickWatcher();
        if (!_closing) DockToPrimaryMonitor();
        Task cleanup = player.ClosePlayerAsync();
        _closingPlayers.Add(cleanup);
        try { await cleanup; }
        catch (Exception error) { _telemetry.Event("player-close-error", new { error.Message }); }
        finally
        {
            // Пока mpv завершает вывод, сохраняем его скрытый HWND живым.
            if (ReferenceEquals(PlayerContent.Content, player)) PlayerContent.Content = null;
            _closingPlayers.Remove(cleanup);
        }
    }

    private nint Hotkey(nint handle, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message is 0x0317 or 0x0318 && wParam != 0 && PrintNativeClient(wParam))
        {
            // Нативные дочерние поверхности печатает Windows, не RenderTargetBitmap WPF.
            if (message == 0x0317 && ((long)lParam & 0x10) != 0)
                Native.DefWindowProc(handle, message, wParam, (nint)0x10);
            handled = true; return 1;
        }
        if ((uint)message == _showInstanceMessage) { ShowOverlay(); handled = true; return 0; }
        if (message is 0x007E or 0x02E0)
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, DockToPrimaryMonitor);
            if (_capture is null) Dispatcher.BeginInvoke(RefreshMonitors);
        }
        if (message == TrayIndicator.CallbackMessage)
        {
            int trayEvent = (int)((long)lParam & 0xffff);
            if (trayEvent is 0x0202 or 0x0203) { ShowOverlay(); handled = true; }
        }
        if (message == 0x0312) handled = DispatchHotkey((int)wParam, nativeMessage: true);
        return 0;
    }

    public void ShowOverlay()
    {
        using var timing = _telemetry.Measure("overlay-show");
        bool opening = !IsVisible;
        if (opening) new WindowInteropHelper(this).EnsureHandle();
        _previousForeground = Native.GetForegroundWindow();
        _pendingOpen = Stopwatch.GetTimestamp();
        CompositionTarget.Rendering -= OnRendering;
        CompositionTarget.Rendering += OnRendering;
        DockToPrimaryMonitor();
        // Открытие по горячей клавише не лишает игру фокуса и фонового звука.
        // Нативную поверхность плеера показываем сразу: AnimateWindow печатает её повторно.
        bool animate = opening && _player is null && SystemParameters.ClientAreaAnimation;
        if (animate) SetOpeningCloaked(true);
        try
        {
            Show();
            RenderClipCards();
            DockToPrimaryMonitor();
            if (animate) QueueNativeOpening();
        }
        catch { if (animate) CancelNativeOpening(); throw; }
        if (SettingsView.Visibility == Visibility.Visible && StorageSettings.Visibility == Visibility.Visible) _ = RefreshStorageUsageAsync();
    }

    private void UpdateOutsideClickWatcher()
    {
        if (TestWindowActivation.Enabled) return;
        if (_closing || !IsVisible || _player is not null)
        { _outsideClicks?.Dispose(); _outsideClicks = null; return; }
        nint handle = new WindowInteropHelper(this).Handle;
        if (handle == 0 || _outsideClicks is not null) return;
        try
        {
            _outsideClicks = new OutsideClickWatcher(handle, () => Dispatcher.BeginInvoke(() =>
            {
                if (!_closing && IsVisible && _player is null) HideOverlay(restoreFocus: false);
            }));
        }
        catch (System.ComponentModel.Win32Exception error) { _telemetry.Event("outside-click-error", new { error.Message }); }
    }

    public void HideOverlay(bool restoreFocus = true)
    {
        PanelHotkey.CancelCapture(); SaveHotkey.CancelCapture(); SaveAllHotkey.CancelCapture();
        CancelPlayerKeyCapture(); _usageCancellation?.Cancel();
        // Скрытие не разрушает галерею: готовые карточки и текущая подготовка остаются живыми.
        _player?.PausePlayback();
        CompositionTarget.Rendering -= OnRendering;
        _pendingOpen = null;
        Hide();
        CancelNativeOpening();
        StopInterfaceMotion();
        if (restoreFocus && Native.GetForegroundWindow() == new WindowInteropHelper(this).Handle && _previousForeground != 0 && _previousForeground != new WindowInteropHelper(this).Handle)
            Native.SetForegroundWindow(_previousForeground);
        _telemetry.Event("hidden", new { playing = false });
    }

    private void DockToPrimaryMonitor()
    {
        using var timing = _telemetry.Measure("panel-dock");
        nint handle = new WindowInteropHelper(this).Handle;
        if (handle == 0) return;
        var info = new Native.MonitorInfo { Size = Marshal.SizeOf<Native.MonitorInfo>() };
        if (!Native.GetMonitorInfo(Native.PrimaryMonitor(), ref info))
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        uint dpi = Native.GetDpiForWindow(handle);
        double scale = dpi == 0 ? 1 : dpi / 96.0;
        PanelBounds compact = PanelGeometry.ForMonitor(info.Monitor.Left, info.Monitor.Top,
            info.Monitor.Right - info.Monitor.Left, info.Monitor.Bottom - info.Monitor.Top, scale, playerVisible: false);
        SidebarColumn.Width = new GridLength(compact.Width / scale);
        PlayerHost.Height = PanelGeometry.PlayerSurfaceHeight(info.Monitor.Right - info.Monitor.Left - compact.Width, info.Monitor.Bottom - info.Monitor.Top, scale) / scale;
        PanelBounds bounds = PanelGeometry.ForMonitor(info.Monitor.Left, info.Monitor.Top,
            info.Monitor.Right - info.Monitor.Left, info.Monitor.Bottom - info.Monitor.Top,
            scale, playerVisible: _player is not null);
        // rcMonitor включает всю высоту экрана, в отличие от рабочей области без панели задач.
        // Поднимаем внутри topmost-группы, включая окна панели задач, не активируя приложение.
        if (!Native.SetWindowPos(handle, new nint(-1), bounds.Left, bounds.Top, bounds.Width, bounds.Height, 0x0010))
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        OverlayWindowRegion.Apply(handle, compact.Width, bounds.Height, _player is null ? null : PanelGeometry.PlayerSurfaceBounds(bounds.Width, bounds.Height, scale));
        if (Native.GetWindowRect(handle, out Native.Rect actual))
            _telemetry.Event("panel-docked", new { dpi, expected = bounds,
                actual = new PanelBounds(actual.Left, actual.Top, actual.Right - actual.Left, actual.Bottom - actual.Top) });
    }

    private void OnRendering(object? sender, EventArgs args)
    {
        if (_pendingOpen is not long start) return;
        double milliseconds = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        _pendingOpen = null;
        CompositionTarget.Rendering -= OnRendering;
        _telemetry.Opening(milliseconds);
    }
    private void OnRecentViewOptions(object sender, RoutedEventArgs args)
    {
        bool dismissed = _recentViewDismissed; _recentViewDismissed = false;
        RecentViewPopup.IsOpen = !dismissed && !RecentViewPopup.IsOpen;
    }
    private void OnRecentViewKeyDown(object sender, KeyEventArgs args)
    {
        if (args.Key != Key.Escape) return;
        RecentViewPopup.IsOpen = false; RecentViewButton.Focus(); args.Handled = true;
    }
    private void OnHide(object sender, RoutedEventArgs args) => HideOverlay();
    private void OnExit(object sender, RoutedEventArgs args) => Close();
    private void OnKeyDown(object sender, KeyEventArgs args)
    {
        if (args.Key == Key.Escape) { HideOverlay(); args.Handled = true; }
    }
}
