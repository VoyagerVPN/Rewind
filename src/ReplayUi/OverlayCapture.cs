using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Rewind.Preview;

public partial class OverlayWindow
{
    private CaptureSettingsFile _settingsFile = null!;
    private CaptureSettings? _captureSettings;
    private IReadOnlyList<CaptureMonitor> _monitors = [];
    private readonly List<Clip> _clips = [];
    private DesktopReplaySession? _capture;
    private DispatcherTimer? _captureTimer;
    private TrayIndicator? _tray;
    private bool _captureBusy;
    private bool _captureCloseAllowed;
    private bool _captureUiReady;
    private string? _captureFailure;
    private bool _recordingInterrupted, _captureBlinkActive, _captureBlinkLit;
    private int? _saveSecondsOverride;
    private Func<string>? _captureMessageProvider;

    private void SetCaptureMessage(Func<string>? text)
    {
        _captureMessageProvider = text;
        CaptureMessageText.Text = text?.Invoke() ?? "";
    }

    private bool _draftVideoEnabled = true;
    private string? _lastMonitorLayoutState;
    private ReplayIntervals _intervals = new(120, [30, 60, 90, 120], 120);

    private void InitializeCapture(Clip[] clips, string? replayDirectory)
    {
        _clips.AddRange(clips);
        _settingsFile = new CaptureSettingsFile(Path.Combine(_diagnostics, "capture-settings.json"));
        HistorySlider.Value = 120;
        StorageInput.Text = replayDirectory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos, Environment.SpecialFolderOption.DoNotVerify), "Rewind");
        try { _captureSettings = _settingsFile.Load(); }
        catch (Exception error) when (error is IOException or JsonException or ArgumentException)
        { SettingsMessageText.Text = Localization.Text("Message.CouldNotLoadSettings") + error.Message; }
        _draftVideoEnabled = _captureSettings?.CaptureVideo ?? true;
        if (_captureSettings is not null)
            foreach (MonitorRecordingProfile profile in _captureSettings.ResolveMonitorProfiles()) _monitorProfiles[ProfileKey(profile)] = profile;
        RefreshMonitors();
        RefreshAudioDevices();
        if (_captureSettings is CaptureSettings settings)
        {
            HistorySlider.Value = settings.HistorySeconds;
            _intervals = new ReplayIntervals(settings.HistorySeconds, settings.ReplayMarkers ?? new[] { 30, 60, 90, settings.ReplaySeconds }.Where(value => value <= settings.HistorySeconds), settings.PreferredReplaySeconds ?? settings.ReplaySeconds);
            SystemAudioEnabled.IsChecked = settings.CaptureSystemAudio;
            MicrophoneEnabled.IsChecked = settings.CaptureMicrophone;
            DiscordAudioEnabled.IsChecked = settings.SeparateDiscord;
            StorageInput.Text = settings.StorageRoot;
            LoadSavedReplays(settings.StorageRoot);
        }
        IntervalTimeline.SetModel(_intervals);
        FilenameFormatInput.Text = _captureSettings?.FileNameFormat ?? ReplayFileName.DefaultFormat;
        ShadowPlayPath.Text = _captureSettings?.ShadowPlayDirectory ?? "";
        ShadowPlayEnabled.IsChecked = _captureSettings?.ShadowPlayDirectory is not null;
        if (_intervals.Markers.Contains(NewIntervalTime.Value))
            NewIntervalTime.Value = Enumerable.Range(1, Math.Max(1, _intervals.MaximumSeconds / 15)).Select(step => step * 15)
                .FirstOrDefault(seconds => !_intervals.Markers.Contains(seconds) && seconds <= _intervals.MaximumSeconds, Math.Min(15, _intervals.MaximumSeconds));
        InitializeAutomaticSettings();
        _captureUiReady = true;
        UpdateIntervalLabels();
        RenderClipCards();
        _captureTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(500), DispatcherPriority.Background, (_, _) => UpdateCaptureStatus(advanceBlink: true), Dispatcher);
        UpdateCaptureStatus();
    }

    private void RefreshMonitors()
    {
        if (_capture is not null) return;
        try
        {
            _monitors = MonitorCatalog.Read();
            RefreshMonitorProfiles();
            RenderMonitorLayout();
        }
        catch (Exception error) { SettingsMessageText.Text = Localization.Text("Message.CouldNotGetMonitors") + error.Message; }
    }

    private void RenderMonitorLayout()
    {
        MonitorLayout.Children.Clear();
        if (_monitors.Count == 0) return;
        string? recording = RecordingMonitorName();
        _lastMonitorLayoutState = MonitorStateKey();
        int left = _monitors.Min(value => value.Left), top = _monitors.Min(value => value.Top);
        int width = _monitors.Max(value => value.Left + value.Width) - left;
        int height = _monitors.Max(value => value.Top + value.Height) - top;
        double scale = Math.Min(590.0 / width, 220.0 / height);
        MonitorLayout.Height = height * scale + 10;
        foreach (CaptureMonitor monitor in _monitors)
        {
            MonitorRecordingProfile profile = ProfileFor(monitor);
            bool enabled = profile.Enabled;
            bool capturing = recording is not null && (_captureSettings?.ResolveMonitorProfiles().Any(profile => profile.Enabled && profile.DeviceName == monitor.DeviceName) ?? false);
            Brush green = new SolidColorBrush(Color.FromRgb(164, 184, 158));
            var content = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
            content.Children.Add(new TextBlock { Text = MonitorNumber(monitor.DeviceName), FontSize = 24, HorizontalAlignment = HorizontalAlignment.Center });
            (int outputWidth, int outputHeight) = profile.OutputSize(monitor.Width, monitor.Height);
            if (enabled)
            {
                content.Children.Add(new TextBlock { Text = $"{outputWidth} × {outputHeight}", FontSize = 13, HorizontalAlignment = HorizontalAlignment.Center });
                content.Children.Add(new TextBlock { Text = $"{profile.Fps} FPS", FontSize = 13, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 2, 0, 2) });
            }
            string role = capturing ? Localization.Text("Message.Recording") : enabled ? Localization.Text("Message.ReplayStopped") : Localization.Text("Message.DoNotRecord");
            var symbols = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 3, 0, 0) };
            symbols.Children.Add(new Wpf.Ui.Controls.SymbolIcon { Symbol = capturing ? Wpf.Ui.Controls.SymbolRegular.Video24 : enabled ? Wpf.Ui.Controls.SymbolRegular.Pause24 : Wpf.Ui.Controls.SymbolRegular.VideoOff24,
                FontSize = 17, Foreground = capturing ? green : (Brush)FindResource(enabled ? "Ink" : "Muted"), Opacity = enabled ? 1 : .5, ToolTip = role });
            content.Children.Add(symbols);
            var frame = new Border
            {
                Child = content, Width = Math.Max(30, monitor.Width * scale - 5), Height = Math.Max(30, monitor.Height * scale - 5),
                Background = (Brush)FindResource(enabled ? "Raised" : "Surface"),
                BorderBrush = capturing ? green : (Brush)FindResource("Line"),
                BorderThickness = new Thickness(capturing ? 3 : 1), Padding = new Thickness(2),
                ToolTip = monitor.Label + "\n" + role + "\n" + monitor.Details
            };
            content.VerticalAlignment = VerticalAlignment.Center;
            System.Windows.Automation.AutomationProperties.SetName(frame, Localization.Text("Message.Screen") + MonitorNumber(monitor.DeviceName));
            Canvas.SetLeft(frame, (monitor.Left - left) * scale + 5);
            Canvas.SetTop(frame, (monitor.Top - top) * scale + 5);
            MonitorLayout.Children.Add(frame);
        }
        RenderMonitorSwitches();
    }

    private void OnSettings(object sender, RoutedEventArgs args)
    {
        if (_libraryVisible) { CloseLibrary(); return; }
        bool opening = SettingsView.Visibility != Visibility.Visible;
        HomeView.Visibility = opening ? Visibility.Collapsed : Visibility.Visible;
        SettingsView.Visibility = opening ? Visibility.Visible : Visibility.Collapsed;
        SettingsLabel.Text = opening ? Localization.Text("Message.Back") : Localization.Text("Ui.Settings");
        SettingsGlyph.Symbol = opening ? Wpf.Ui.Controls.SymbolRegular.ArrowLeft24 : Wpf.Ui.Controls.SymbolRegular.Settings24;
        if (opening) { RefreshMonitors(); RefreshAudioDevices(); }
        else RenderClipCards();
    }

    private void OnRefreshMonitors(object sender, RoutedEventArgs args) => RefreshMonitors();

    private async void OnIdentifyMonitors(object sender, RoutedEventArgs args)
    {
        var labels = new List<Window>();
        try
        {
            foreach (CaptureMonitor monitor in MonitorCatalog.Read())
            {
                var label = new Window
                {
                    WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false,
                    ShowActivated = false, Topmost = true, Width = 160, Height = 120,
                    Background = new SolidColorBrush(Color.FromRgb(36, 36, 40)),
                    Content = new TextBlock { Text = monitor.DeviceName.Replace("\\\\.\\DISPLAY", ""), FontSize = 64,
                        Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }
                };
                labels.Add(label);
                label.SourceInitialized += (_, _) => Native.SetWindowPos(new WindowInteropHelper(label).Handle, -1,
                    monitor.Left + (monitor.Width - 160) / 2, monitor.Top + (monitor.Height - 120) / 2, 160, 120, 0x0014);
                label.Show();
            }
            await Task.Delay(2500);
        }
        catch (Exception error) { SettingsMessageText.Text = error.Message; }
        finally { foreach (Window label in labels) label.Close(); }
    }

    private void OnWindowsDisplay(object sender, RoutedEventArgs args)
    {
        try { Process.Start(new ProcessStartInfo("ms-settings:display") { UseShellExecute = true }); }
        catch (Win32Exception error) { SettingsMessageText.Text = error.Message; }
    }

    private CaptureSettings ReadCaptureSettings()
    {
        if (_monitors.Count == 0) throw new ArgumentException(Localization.Text("Message.NoScreensAreConnected"));
        int history = (int)Math.Round(HistorySlider.Value);
        _intervals.Resize(history);
        MonitorRecordingProfile[] profiles = _monitorProfiles.Values.OrderBy(profile => profile.DeviceName).ToArray();
        MonitorRecordingProfile primary = profiles.FirstOrDefault(profile => profile.Enabled) ?? profiles[0];
        var settings = new CaptureSettings(primary.DeviceName, primary.Fps, history, _intervals.DefaultSeconds, StorageInput.Text.Trim(), primary.Identity,
            _intervals.Markers.ToArray(), SystemAudioEnabled.IsChecked == true,
            MicrophoneEnabled.IsChecked == true, (MicrophonePicker.SelectedItem as AudioDeviceChoice)?.Id, DiscordAudioEnabled.IsChecked == true, _draftVideoEnabled,
            _intervals.PreferredDefaultSeconds, profiles, FilenameFormatInput.Text)
        {
            ShadowPlayDirectory = ShadowPlayEnabled.IsChecked == true ? ShadowPlayPath.Text.Trim() : null,
        };
        if (settings.CaptureMicrophone && MicrophonePicker.SelectedItem is not AudioDeviceChoice)
            throw new ArgumentException(Localization.Text("Message.NoRecordingMicrophoneIsSelected"));
        settings.Validate();
        return settings;
    }

    private void OnHistoryChanged(object sender, RoutedEventArgs args)
    {
        try { _intervals.Resize((int)Math.Round(HistorySlider.Value)); IntervalTimeline.Redraw(); OnIntervalsChanged(sender, EventArgs.Empty); SettingsMessageText.Text = ""; }
        catch (ArgumentException error) { SettingsMessageText.Text = error.Message; }
    }

    private void OnIntervalsChanged(object? sender, EventArgs args)
    {
        RenderReplayDurations();
        RenderMarkerRows();
        RefreshSettingsPresentation();
    }

    private void OnAddInterval(object sender, RoutedEventArgs args)
    {
        try { _intervals.Add(NewIntervalTime.Value); IntervalTimeline.Redraw(); UpdateIntervalLabels(); SettingsMessageText.Text = ""; }
        catch (ArgumentException error) { SettingsMessageText.Text = error.Message; }
    }

    private async void OnCaptureToggle(object sender, RoutedEventArgs args) => await ToggleCaptureAsync();

    public async Task ToggleCaptureAsync()
    {
        if (_captureBusy) return;
        _captureBusy = true;
        CaptureToggle.IsEnabled = false;
        SetCaptureMessage(null);
        try
        {
            await StopAudioMonitoringAsync();
            if (_capture is DesktopReplaySession previous)
            {
                CancelReplayDurationSelection();
                await previous.StopAsync();
                if (previous.Status.Error is string error) SetCaptureMessage(() => error);
                await previous.DisposeAsync();
                _capture = null;
                RememberRecordingState(false);
                _captureStartNoticePending = false;
                ShowLocalizedActionToast("capture", "Message.RecordingIsOff", () => "", Wpf.Ui.Controls.SymbolRegular.VideoOff24);
            }
            else
            {
                if (_captureFailure is not null) RefreshMonitors();
                CaptureSettings settings = ReadCaptureSettings();
                if (!settings.CaptureVideo) throw new InvalidOperationException(Localization.Text("Message.ScreenRecordingIsDisabledInSettings"));
                IReadOnlyList<CaptureMonitor> monitors = MonitorCatalog.Read();
                _settingsFile.Save(settings);
                _captureSettings = settings;
                try { await _playerAudioOutput.WarmAsync().WaitAsync(TimeSpan.FromSeconds(2)); }
                catch (Exception error) { _telemetry.Event("player-audio-prewarm-error", new { error.Message }); }
                _capture = await Task.Run(() => DesktopReplaySession.Start(settings, monitors, diagnosticWarning: warning => _telemetry.Event("capture-log-unavailable", new { warning })));
                RememberRecordingState(true);
                _captureStartNoticePending = true; _captureFailureNotified = false;
                _telemetry.Event("capture-started", new { settings, monitors });
                SetCaptureMessage(null);
            }
        }
        catch (Exception error)
        {
            SetCaptureMessage(() => error.Message);
            _captureStartNoticePending = false;
            ShowLocalizedActionToast("capture", "Message.CouldNotChangeRecordingState", () => error.Message, Wpf.Ui.Controls.SymbolRegular.ErrorCircle24, error: true);
            _telemetry.Event("capture-error", new { error.Message });
        }
        finally { _captureBusy = false; CaptureToggle.IsEnabled = true; UpdateCaptureStatus(); }
    }

    private async void OnSaveReplay(object sender, RoutedEventArgs args) => await SaveReplayAsync(_saveSecondsOverride);
    private async void OnSaveAllScreens(object sender, RoutedEventArgs args) => await SaveReplayAsync(_saveSecondsOverride, allScreens: true);

    public void SelectReplayDuration(int seconds)
    {
        if (!_intervals.ActiveMarkers.Contains(seconds)) throw new ArgumentException(Localization.Text("Message.ThisIntervalIsUnavailableForSaving"));
        _saveSecondsOverride = seconds;
        UpdateCaptureStatus();
    }

    private void RenderReplayDurations()
    {
        if (_saveSecondsOverride is int chosen && !_intervals.ActiveMarkers.Contains(chosen)) _saveSecondsOverride = null;
        int selectedSeconds = _saveSecondsOverride ?? _intervals.DefaultSeconds;
        if (!ReplayDurationOptions.Children.OfType<RadioButton>().Select(button => (int)button.Tag).SequenceEqual(_intervals.ActiveMarkers))
        {
            ReplayDurationOptions.Children.Clear();
            foreach (int seconds in _intervals.ActiveMarkers)
            {
                var option = new RadioButton { Content = ReplayIntervals.Format(seconds), Tag = seconds,
                    GroupName = "SaveReplayDuration", Style = (Style)FindResource("ReplayDurationOption") };
                System.Windows.Automation.AutomationProperties.SetName(option, Localization.Text("Message.Duration") + ReplayIntervals.Format(seconds));
                option.Checked += (_, _) => { if (seconds != (_saveSecondsOverride ?? _intervals.DefaultSeconds)) SelectReplayDuration(seconds); };
                ReplayDurationOptions.Children.Add(option);
            }
        }
        ReplayDurationOptions.IsEnabled = !_captureBusy && _intervals.ActiveMarkers.Count > 0;
        foreach (RadioButton option in ReplayDurationOptions.Children.OfType<RadioButton>())
        {
            int seconds = (int)option.Tag;
            System.Windows.Automation.AutomationProperties.SetName(option, Localization.Text("Message.Duration") + ReplayIntervals.Format(seconds));
            option.IsChecked = seconds == selectedSeconds;
            option.ToolTip = seconds == _intervals.DefaultSeconds ? Localization.Text("Message.DefaultHotkeyInterval") : Localization.Text("Message.Select") + ReplayIntervals.Format(seconds);
        }
    }

    private async Task SaveReplayAsync(int? seconds = null, bool allScreens = false, string[]? selected = null,
        long? endTimestamp = null, nint? foregroundWindow = null)
    {
        string noticeKey = "save-" + Guid.NewGuid().ToString("N");
        try
        {
            if (_capture is null) throw new InvalidOperationException(Localization.Text("Message.RecordingIsCurrentlyOff"));
            SetCaptureMessage(() => Localization.Text("Message.SavingReplay"));
            if (!allScreens && selected is null)
            {
                if (!Native.GetCursorPos(out Native.Point cursor)) throw new Win32Exception();
                CaptureMonitor? monitor = _monitors.FirstOrDefault(monitor => cursor.X >= monitor.Left && cursor.X < monitor.Left + monitor.Width && cursor.Y >= monitor.Top && cursor.Y < monitor.Top + monitor.Height);
                if (monitor is null) throw new InvalidOperationException(Localization.Text("Message.CouldNotIdentifyTheScreenUnderThePointer"));
                selected = [monitor.DeviceName];
            }
            nint foreground = foregroundWindow ?? Native.GetForegroundWindow();
            if (foreground == new System.Windows.Interop.WindowInteropHelper(this).Handle) foreground = _previousForeground;
            long contextStamp = Stopwatch.GetTimestamp();
            ApplicationCaptureContext application = CaptureApplication.Read(foreground);
            double contextMs = Stopwatch.GetElapsedTime(contextStamp).TotalMilliseconds;
            ShowLocalizedActionToast(noticeKey, "Message.SavingReplay", () => Localization.Text("Message.PreparingVideo"), Wpf.Ui.Controls.SymbolRegular.Save24, persistent: true);
            long requestStamp = Stopwatch.GetTimestamp();
            Task<SavedReplay[]> saving = _capture.SaveScreensAsync(seconds, selected, application.Name, application.SessionId, application.SessionStartedUtc, endTimestamp);
            _telemetry.Event("save-request-stages", new { contextMs, requestMs = Stopwatch.GetElapsedTime(requestStamp).TotalMilliseconds });
            SavedReplay[] savedScreens = await saving;
            SavedReplay saved = savedScreens[0];
            string Detail() => ReplayNotificationText.SavedDetail((int)saved.Duration, savedScreens.Sum(screen => screen.Bytes), savedScreens.Select(screen => screen.File).ToArray());
            nint[] monitors = savedScreens.Length > 1 ? _monitors.Where(monitor =>
                savedScreens.Any(screen => screen.MonitorDeviceName == monitor.DeviceName)).Select(monitor => (nint)monitor.Handle).ToArray() : [];
            ShowLocalizedActionToast(noticeKey, "Message.ReplaySaved", Detail,
                Wpf.Ui.Controls.SymbolRegular.CheckmarkCircle24, file: saved.File, recordingMonitors: monitors);
            _telemetry.Event("replay-saved", saved);
            // Ошибка обновления библиотеки не отменяет успешно записанный видеофайл.
            try
            {
                Clip clip = ClipFromReplay(saved);
                _clips.Insert(0, clip);
                _libraryIndex.ReadSnapshot(CatalogRoots(), [clip]);
                _ = RefreshCatalogAsync();
                if (_clips.Count > 20) _clips.RemoveRange(20, _clips.Count - 20);
                RenderClipCards();
                SetCaptureMessage(() => Localization.Format("Format.SavedSummary", ReplayIntervals.Format((int)saved.Duration), savedScreens.Sum(screen => screen.Bytes) / 1048576.0));
            }
            catch (Exception libraryError)
            {
                SetCaptureMessage(() => Localization.Text("Message.ReplaySavedButTheLibraryCouldNotBeRefreshed"));
                _telemetry.Event("saved-clip-ui-error", new { libraryError.Message });
            }
        }
        catch (Exception error)
        {
            ReportReplaySaveError(error, noticeKey, allScreens, seconds, selected);
        }
    }

    private void ReportReplaySaveError(Exception error, string noticeKey, bool allScreens, int? seconds, string[]? selected)
    {
        SetCaptureMessage(() => error.Message);
        ShowLocalizedActionToast(noticeKey, "Message.ReplayWasNotSaved", () => error.Message, Wpf.Ui.Controls.SymbolRegular.ErrorCircle24, error: true);
        _telemetry.Event("save-error", new { error.Message, exception = error.ToString(), allScreens, requestedSeconds = seconds,
            requestedMonitors = selected,
            recordingMonitors = _captureSettings?.ResolveMonitorProfiles().Where(profile => profile.Enabled).Select(profile => profile.DeviceName).ToArray() });
    }

    private void UpdateCaptureStatus(bool advanceBlink = false)
    {
        using var timing = _telemetry.Measure("capture-status");
        ReplayStatus? status = _capture?.Status;
        ObserveCaptureNotification(status);
        bool running = status is { Running: true, Recovering: false };
        bool audioWarning = running && _capture!.AudioStatus.Any(source => source.Warning is not null);
        bool full = running && status!.AvailableSeconds >= (_captureSettings?.HistorySeconds ?? 120) - 0.1;
        Color color = running ? (full && !audioWarning ? Color.FromRgb(133, 184, 129) : Color.FromRgb(208, 177, 105)) : Color.FromRgb(196, 123, 116);
        UpdateCaptureMotion(running, color);
        RenderCaptureIndicator(status, audioWarning, color, advanceBlink);
        HistoryProgress.Value = running ? Math.Clamp(status!.AvailableSeconds / (_captureSettings?.HistorySeconds ?? 120), 0, 1) : 0;
        HistoryProgress.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
        UpdateAudioStatus();
        CaptureToggleText.Text = _capture is null ? Localization.Text("Ui.Start") : Localization.Text("Message.Stop");
        CaptureToggle.ToolTip = CaptureToggleText.Text + (_hotkeys.CaptureToggle is null ? "" : " · " + _hotkeys.CaptureToggle.Label);
        System.Windows.Automation.AutomationProperties.SetName(CaptureToggle, CaptureToggleText.Text);
        CaptureToggle.Style = (Style)FindResource(_capture is null ? "PrimaryButton" : typeof(Button));
        CaptureToggleGlyph.Symbol = _capture is null ? Wpf.Ui.Controls.SymbolRegular.Play24 : Wpf.Ui.Controls.SymbolRegular.Pause24;
        CaptureToggle.IsEnabled = !_closing && !_captureBusy && (_capture is not null || _draftVideoEnabled && (_monitors.Count > 0 || _captureFailure is not null));
        CaptureSettingsFields.IsEnabled = _capture is null && !_captureBusy;
        StorageConfigurationFields.IsEnabled = _capture is null && !_captureBusy;
        SaveReplayButton.IsEnabled = running && status!.AvailableSeconds >= 1 && status.SavesInProgress < 2;
        SaveAllScreensButton.IsEnabled = SaveReplayButton.IsEnabled;
        SaveAllScreensButton.Visibility = (_captureSettings?.ResolveMonitorProfiles().Count(profile => profile.Enabled) ?? 0) > 1 ? Visibility.Visible : Visibility.Collapsed;
        RenderReplayDurations();
        int saveSeconds = _saveSecondsOverride ?? _intervals.DefaultSeconds;
        HistoryValueText.Text = running ? $"{ReplayIntervals.Format((int)status!.AvailableSeconds)} / {ReplayIntervals.Format(_intervals.MaximumSeconds)}" : ReplayIntervals.Format(_intervals.MaximumSeconds);
        ScreensValueText.Text = (_captureSettings?.ResolveMonitorProfiles().Count(profile => profile.Enabled) ?? 0).ToString();
        ReplaySizeText.Text = running && status!.LatestSeconds > 2
            ? Localization.Format("Format.EstimatedMb", status.BufferBytes / Math.Min(status.LatestSeconds, _captureSettings!.HistorySeconds + 10) * saveSeconds / 1048576.0) : "—";
        RefreshSettingsPresentation();
        if (!_captureBusy && !_closing && _capture is DesktopReplaySession failed && status is { Running: false, Error: not null })
            _ = FinishFailedCaptureAsync(failed);
    }

    internal void RenderCaptureIndicator(ReplayStatus? status, bool audioWarning, Color color, bool advanceBlink = false)
    {
        bool running = status is { Running: true, Recovering: false };
        bool recovering = status is { Running: true, Recovering: true };
        if (running && status!.EncodedFrames > 0)
        {
            _captureFailure = null;
            _recordingInterrupted = false;
        }
        else if (!_captureBusy && status is { Running: false, Error: string failure })
        {
            _captureFailure = failure;
            _recordingInterrupted |= status.EncodedFrames > 0;
        }

        bool interrupted = _recordingInterrupted || recovering && status!.EncodedFrames > 0;
        string state;
        if (recovering) state = interrupted ? Localization.Text("Message.RecordingInterruptedRecovering") : Localization.Text("Message.RecoveringVideoCapture");
        else if (running && _captureFailure is null) state = audioWarning ? Localization.Text("Message.RecordingAudioProblemDetected") : Localization.Text("Message.Recording9d0a624f");
        else if (running) state = Localization.Text("Message.StartingRecording");
        else if (interrupted) state = Localization.Text("Message.RecordingInterrupted");
        else state = _captureFailure is null ? Localization.Text("Message.RecordingIsOff") : Localization.Text("Message.RecordingDidNotStart");

        if (!interrupted) _captureBlinkLit = false;
        else if (!_captureBlinkActive) _captureBlinkLit = true;
        else if (advanceBlink) _captureBlinkLit = !_captureBlinkLit;
        _captureBlinkActive = interrupted;
        CaptureStatusText.Text = state;
        CaptureStateGlyph.Symbol = running ? Wpf.Ui.Controls.SymbolRegular.Video24 : Wpf.Ui.Controls.SymbolRegular.VideoOff24;
        HeaderCaptureGlyph.Symbol = CaptureStateGlyph.Symbol;
        HeaderCaptureGlyph.Visibility = interrupted ? Visibility.Hidden : Visibility.Visible;
        HeaderCaptureErrorDot.Visibility = interrupted ? Visibility.Visible : Visibility.Collapsed;
        HeaderCaptureErrorDot.Opacity = _captureBlinkLit ? 1 : 0;
        HeaderCaptureStatus.ToolTip = state;
        System.Windows.Automation.AutomationProperties.SetName(HeaderCaptureStatus, state);
        CaptureFailureText.Text = _captureFailure ?? "";
        CaptureFailureText.Visibility = _captureFailure is null ? Visibility.Collapsed : Visibility.Visible;
        Color? trayColor = color;
        if (interrupted) trayColor = _captureBlinkLit ? Color.FromRgb(240, 82, 82) : null;
        try { _tray?.SetStatus(trayColor, "Rewind " + AppVersion.Display + " · " + state); }
        catch (Win32Exception trayError) { _telemetry.Event("tray-error", new { trayError.Message }); }
    }

    private async Task FinishFailedCaptureAsync(DesktopReplaySession failed)
    {
        CancelReplayDurationSelection();
        _captureBusy = true;
        CaptureToggle.IsEnabled = false;
        try { await failed.DisposeAsync(); }
        catch (Exception error) { _telemetry.Event("capture-cleanup-error", new { error.Message }); }
        finally
        {
            _capture = null;
            _captureBusy = false;
            UpdateCaptureStatus();
        }
    }

    private void InitializeTray(nint handle)
    {
        try { _tray = new TrayIndicator(handle); UpdateCaptureStatus(); _telemetry.Event("tray-ready", new { native = "Shell_NotifyIconW" }); }
        catch (Win32Exception error) { _telemetry.Event("tray-error", new { error.Message }); }
    }

    private async void OnCaptureClosing(object? sender, CancelEventArgs args)
    {
        if (_capture is null && !_captureBusy && _pendingAutoSettings is not null) SaveAutomaticSettings();
        if (_captureCloseAllowed || _capture is null && !_captureBusy && _player is null && _closingPlayers.Count == 0 && _audioPreview is null && _previewRefresh.IsCompleted) return;
        args.Cancel = true;
        if (_closing) return;
        _closing = true;
        CancelReplayDurationSelection();
        CaptureToggle.IsEnabled = false;
        SetCaptureMessage(() => Localization.Text("Message.StoppingCaptureAndFinishingSaves"));
        while (_captureBusy) await Task.Delay(50);
        await StopAudioMonitoringAsync();
        try { if (_capture is not null) await _capture.DisposeAsync(); }
        catch (Exception error) { _telemetry.Event("shutdown-error", new { error.Message }); }
        if (_player is not null) await ClosePlayerExtensionAsync();
        try { await Task.WhenAll(_closingPlayers.ToArray()); }
        catch (Exception error) { _telemetry.Event("player-shutdown-error", new { error.Message }); }
        _capture = null;
        _captureCloseAllowed = true;
        Close();
    }

    private void LoadSavedReplays(string root)
    {
        string folder = Path.Combine(root, "Clips");
        if (!Directory.Exists(folder)) return;
        var recent = new List<Clip>();
        foreach (string file in Directory.EnumerateFiles(folder, "*.mp4", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = true }).Where(file => !file.EndsWith(".partial.mp4", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(File.GetLastWriteTimeUtc).Take(60))
        {
            try
            {
                SavedReplay? saved = ReplayMetadata.Read(file);
                if (saved is not null) recent.Add(ClipFromReplay(saved, false));
            }
            catch (Exception error) when (error is IOException or JsonException) { _telemetry.Event("gallery-index-error", new { file, error.Message }); }
        }
        foreach (var group in recent.GroupBy(clip => clip.EventId ?? clip.Video).Take(20).Reverse())
        {
            Clip clip = group.First();
            if (_clips.Any(known => known.Video == clip.Video || clip.EventId is not null && known.EventId == clip.EventId)) continue;
            _clips.Insert(0, group.Count() > 1 ? clip with { Screens = group.ToArray() } : clip);
        }
    }

    internal static Clip ClipFromReplay(SavedReplay replay, bool includeScreens = true)
    {
        var clip = new Clip("Повтор " + Path.GetFileNameWithoutExtension(replay.File),
        $"{replay.Width} × {replay.Height} · {replay.Fps:F0} FPS" + (replay.AudioSources is { Length: > 0 } sources
            ? $" · дорожек: {sources.Length}" : replay.HasSystemAudio ? " · системный звук" : " · без звука"),
        replay.File, replay.Thumbnail, [null, null, null], replay.Duration, replay.Fps,
        replay.AudioSources is null && replay.HasSystemAudio, replay.AudioSources, replay.MonitorDeviceName, replay.EventId,
        ApplicationName: replay.ApplicationName, SessionId: replay.SessionId, SessionStartedUtc: replay.SessionStartedUtc, SavedAtUtc: replay.SavedAtUtc);
        if (!includeScreens || replay.RelatedFiles is not { Length: > 1 }) return clip;
        var screens = new List<Clip>();
        foreach (string file in replay.RelatedFiles)
        {
            try
            {
                if (!string.Equals(Path.GetDirectoryName(file), Path.GetDirectoryName(replay.File), StringComparison.OrdinalIgnoreCase)) continue;
                SavedReplay? sibling = ReplayMetadata.Read(file);
                if (sibling is not null && sibling.EventId == replay.EventId && File.Exists(sibling.File)) screens.Add(ClipFromReplay(sibling, false));
            }
            catch (Exception error) when (error is IOException or JsonException) { }
        }
        return screens.Count > 1 ? clip with { Title = $"Повтор · экранов: {screens.Count}", Screens = screens.ToArray() } : clip;
    }

}
