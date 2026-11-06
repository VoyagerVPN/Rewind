using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Windows.Media;
using System.Windows.Threading;

namespace Rewind.Preview;

public partial class ReplayPlayerView : UserControl
{
    private readonly AudioMix _mix = new();
    private readonly ReplayAudioStreams _audio;
    private readonly VideoProcess _video = new();
    private readonly HashSet<string> _softwareVideos = new(StringComparer.OrdinalIgnoreCase);
    private readonly bool _headless;
    private readonly PlaybackHealthStore _healthStore;
    private PlaybackHealth? _currentHealth;
    private Task _healthWrite = Task.CompletedTask;
    private readonly UiTelemetry _telemetry;
    private readonly string _preferencesPath;
    private Button? _dismissedPopupTrigger;
    private sealed record PlayerPreferences(bool ShowTracksInPanel = false, int SeekStepSeconds = 5);
    private int _seekStepSeconds = 5;
    private readonly DispatcherTimer _positionTimer, _seekTimer;
    private readonly SemaphoreSlim _videoGate = new(1);
    private Clip? _clip, _deleteClip;
    private bool _playing, _updatingPosition, _ready, _disposed, _closingNative, _updatingMonitorClip, _nativeStarted;
    private bool _seekPending, _seekQueued, _loading, _loadOperationActive, _atEnd, _deleteBusy, _resumeAfterCancel;
    private double _position, _seekTarget, _issuedTarget, _anchorPosition;
    private long _seekStamp, _anchorStamp, _loadStamp;
    private long _positionStamp;
    private bool _renderingPosition;
    private TimeSpan _lastPositionRendering = TimeSpan.MinValue;
    private int _generation;
    private int _audioSyncVersion;
    private bool _synchronizingAudio, _audioReady;
    private CancellationTokenSource? _loadCancellation;
    private CancellationTokenSource? _recoveryCancellation;
    private bool _finding;
    private EventHandler<EventArgs>? _onPlaying, _onEnded, _onError;
    private EventHandler<VideoTimeEventArgs>? _onTime;
    private Task? _closeTask;
    private Task? _recoveryTask;
    private sealed record RiskRequest(Clip Clip, double Target, bool Resume, int Generation);
    private RiskRequest? _riskRequest;
    private Task? _nativeRecoveryTask;
    private bool _automaticRecoveryUsed, _restartingNative, _recoveredAutomatically;
    private double _lastGoodPosition;
    private bool _timelineGestureActive, _timelineGestureFromStart;
    private double PlaybackStart => _currentHealth?.RecoveredStartSeconds is double start && start > 0 && start < (_clip?.Duration ?? 0) && RiskRibbon.IsRisky(0) ? start : 0;
    private bool AtPlaybackStart => Math.Abs(_position - PlaybackStart) <= .1;
    private PlayerHotkeySettings _hotkeys = PlayerHotkeySettings.Default;
    public event EventHandler? CloseRequested;
    public event EventHandler? ExplorerOpened;
    public event EventHandler? PlaybackHealthChanged;
    public event EventHandler<ClipDeletedEventArgs>? ClipDeleted;
    public double PositionSeconds => _position;
    public double AudioPositionSeconds => _audio.Position;
    public double VideoPositionSeconds => _video.Time / 1000d;
    public bool IsPlaying => _playing;
    public bool IsLoading => _loading;
    public bool IsAtEnd => _atEnd;
    public string? CurrentVideo => _clip?.Video;
    public int? VideoProcessId => _video.ProcessId;
    public VideoPlaybackStatistics? PlaybackStatistics => _video.Statistics;
    public bool HasPlaybackError => PlaybackErrorPanel.Visibility == Visibility.Visible;
    private sealed record MonitorClipChoice(Clip Clip) { public override string ToString() => "Экран " + (Clip.MonitorName?.Replace(@"\\.\DISPLAY", "") ?? "?"); }

    public ReplayPlayerView(UiTelemetry telemetry, string diagnostics, bool headless = false) : this(telemetry, diagnostics, null, headless) { }

    internal ReplayPlayerView(UiTelemetry telemetry, string diagnostics, ReplayAudioOutput? audioOutput, bool headless = false)
    {
        InitializeComponent(); _telemetry = telemetry;
        _preferencesPath = Path.Combine(diagnostics, "player-settings.json");
        PlayerPreferences preferences = new();
        try { if (File.Exists(_preferencesPath)) preferences = System.Text.Json.JsonSerializer.Deserialize<PlayerPreferences>(File.ReadAllText(_preferencesPath)) ?? preferences; }
        catch (Exception error) when (error is IOException or System.Text.Json.JsonException or UnauthorizedAccessException) { telemetry.Event("player-settings-read-error", new { error.Message }); }
        PanelTracksToggle.IsChecked = preferences.ShowTracksInPanel;
        SeekStepPicker.SelectedItem = SeekStepPicker.Items.OfType<ComboBoxItem>().FirstOrDefault(item => item.Tag?.ToString() == preferences.SeekStepSeconds.ToString(CultureInfo.InvariantCulture)) ?? SeekStepPicker.Items[0];
        InlineTracks.Visibility = PanelTracksToggle.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        PreparePopupToggle(MixerPopup, SoundButton);
        PreparePopupToggle(ActionsPopup, ActionsButton);
        PreparePopupToggle(PlayerSettingsPopup, PlayerSettingsButton);
        try { _hotkeys = new HotkeySettingsFile(Path.Combine(diagnostics, "hotkeys.json")).Load().PlayerBindings; }
        catch (Exception error) when (error is IOException or System.Text.Json.JsonException or ArgumentException) { telemetry.Event("player-hotkeys-error", new { error.Message }); }
        _headless = headless;
        _healthStore = new(Path.Combine(diagnostics, "library-cache", "playback-health"));
        _audio = new ReplayAudioStreams(audioOutput);
        _positionTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(100), DispatcherPriority.Background, (_, _) =>
        { if (!_renderingPosition) UpdatePosition(); UpdateAppearance(); }, Dispatcher); _positionTimer.Stop();
        IsVisibleChanged += (_, _) => UpdatePositionRendering();
        _seekTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(90), DispatcherPriority.Background, (_, _) => IssueSeek(), Dispatcher); _seekTimer.Stop();
        PositionSlider.GotMouseCapture += (_, _) =>
        { _timelineGestureActive = true; _timelineGestureFromStart = AtPlaybackStart; };
        PositionSlider.LostMouseCapture += (_, _) =>
        {
            _timelineGestureActive = false;
            if (_riskRequest is not null) return;
            if (_seekQueued) IssueSeek(force: true);
            else if (!_seekPending && !_loading && _clip is not null && !_atEnd) SyncAudio(_position);
        };
        ToolTipService.SetInitialShowDelay(PositionSlider, 350);
        PositionSlider.AddHandler(Mouse.MouseMoveEvent, new MouseEventHandler((_, args) =>
        {
            double travel = Math.Max(1, PositionSlider.ActualWidth - 12);
            double seconds = Math.Clamp((args.GetPosition(PositionSlider).X - 6) / travel, 0, 1) * PositionSlider.Maximum;
            PositionSlider.ToolTip = RiskRibbon.IsRisky(seconds) ? RiskRibbon.Description : null;
        }), handledEventsToo: true);
        _ready = true; SetHotkeys(_hotkeys); UpdateAppearance();
    }

    public void SetHotkeys(PlayerHotkeySettings settings)
    {
        _hotkeys = settings;
        PlayButton.ToolTip = "Пауза / продолжить" + (settings.Pause is null ? "" : " · " + settings.Pause.Label);
        PreviousFrameButton.ToolTip = "Предыдущий кадр" + (settings.PreviousFrame is null ? "" : " · " + settings.PreviousFrame.Label);
        NextFrameButton.ToolTip = "Следующий кадр" + (settings.NextFrame is null ? "" : " · " + settings.NextFrame.Label);
        GoToStartButton.ToolTip = "К началу видео" + (settings.GoToStart is null ? "" : " · " + settings.GoToStart.Label);
        DeleteClipButton.ToolTip = "Удалить клип" + (settings.Delete is null ? "" : " · " + settings.Delete.Label);
        UpdateSeekButtons();
    }
    private void UpdateSeekButtons()
    {
        SeekBackwardSeconds.Text = SeekForwardSeconds.Text = _seekStepSeconds.ToString(CultureInfo.InvariantCulture);
        string backward = $"Назад на {_seekStepSeconds} с", forward = $"Вперёд на {_seekStepSeconds} с";
        SeekBackwardButton.ToolTip = backward + (_hotkeys.Backward is null ? "" : " · " + _hotkeys.Backward.Label);
        SeekForwardButton.ToolTip = forward + (_hotkeys.Forward is null ? "" : " · " + _hotkeys.Forward.Label);
        System.Windows.Automation.AutomationProperties.SetName(SeekBackwardButton, backward);
        System.Windows.Automation.AutomationProperties.SetName(SeekForwardButton, forward);
    }

    public void ShowLoading(Clip clip, double startSeconds = 0)
    {
        // Предварительная подготовка отменяет прежнее открытие, но ещё не запускает watchdog декодера.
        ++_generation; _loadCancellation?.Cancel(); _loadOperationActive = false;
        ShowLoadingCore(clip, startSeconds);
    }

    private void ShowLoadingCore(Clip clip, double startSeconds)
    {
        _resumeAfterCancel = false; DeletePopup.IsOpen = false; DeleteConfirmation.Visibility = Visibility.Collapsed; _deleteClip = null;
        if (_clip?.Video != clip.Video) _currentHealth = null;
        _recoveryCancellation?.Cancel(); _clip = clip; _position = Math.Clamp(startSeconds, 0, Math.Max(0, clip.Duration));
        RenderHealth();
        _riskRequest = null; RiskConfirmation.Visibility = Visibility.Collapsed;
        _updatingPosition = true; PositionSlider.Maximum = Math.Max(0, clip.Duration); _updatingPosition = false; RenderPosition();
        ActionsPopup.IsOpen = MixerPopup.IsOpen = PlayerSettingsPopup.IsOpen = false;
        _loading = true; _nativeStarted = false; _audioReady = false; _atEnd = false;
        Video.Visibility = Visibility.Hidden; LoadingOverlay.Visibility = Visibility.Visible;
        LoadingLabel.Text = "Загрузка…"; PlaybackErrorPanel.Visibility = Visibility.Collapsed;
        ClipTitleText.Text = Path.GetFileName(clip.Video); Poster.Source = null;
        try
        {
            if (File.Exists(clip.Thumbnail)) { var image = new BitmapImage(); image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad; image.UriSource = new Uri(clip.Thumbnail); image.EndInit(); image.Freeze(); Poster.Source = image; }
        }
        catch (Exception error) when (error is IOException or NotSupportedException) { _telemetry.Event("player-poster-error", new { error.Message }); }
        UpdateAppearance();
    }

    private void BindNativeEvents(int generation)
    {
        UnbindNativeEvents();
        void Queue(Action action) => Dispatcher.BeginInvoke(() => { if (!_disposed && !_closingNative && generation == _generation) action(); });
        _onPlaying = (_, _) => Queue(() =>
        {
            _nativeStarted = true;
            if (_seekQueued) IssueSeek(force: true); else if (!_seekPending) Anchor(Math.Max(0, _video.Time / 1000d));
            if (!_playing || !_audioReady || _synchronizingAudio) _video.SetPause(true);
        });
        _onTime = (_, args) =>
        {
            double seconds = args.Time / 1000d; long stamp = Stopwatch.GetTimestamp();
            Queue(() =>
            {
                if (_seekPending)
                {
                    // Во время seek метки времени не подтверждают готовый кадр.
                    return;
                }
                else if (!_seekQueued) Anchor(seconds, stamp);
            });
        };
        _onEnded = (_, _) => Queue(() =>
        {
            _playing = false; _atEnd = true; _seekPending = _seekQueued = false; _seekTimer.Stop();
            double end = _clip?.Duration ?? 0;
            // Небольшой остаток после последней грубой метки времени доезжает до края плавно.
            if (end - _position > .25) _position = end;
            _audio.Pause(); RenderPosition(); UpdateAppearance();
        });
        _onError = (_, _) =>
        {
            int? processId = _video.ProcessId;
            string? error = _video.Error, details = _video.ErrorDetails;
            bool rememberPosition = _video.FailureMayIndicateDamage;
            Queue(() =>
            {
                _telemetry.Event("player-video-failure", new { processId, error, details });
                HandleNativeFailure(error ?? "Не удалось воспроизвести видео.", rememberPosition);
            });
        };
        _video.Playing += _onPlaying; _video.TimeChanged += _onTime; _video.EndReached += _onEnded; _video.EncounteredError += _onError;
    }
    private void UnbindNativeEvents()
    { _video.Playing -= _onPlaying; _video.TimeChanged -= _onTime; _video.EndReached -= _onEnded; _video.EncounteredError -= _onError; }
    private void Anchor(double seconds, long? stamp = null) { _anchorPosition = Math.Max(0, seconds); _anchorStamp = stamp ?? Stopwatch.GetTimestamp(); }

    public async Task LoadClipAsync(Clip clip, long startMilliseconds = 0, bool autoPlay = true, bool preferKnownStart = true, bool allowDamaged = false, bool isAutomaticRecovery = false)
    {
        if (_clip?.Video != clip.Video) _lastGoodPosition = 0;
        if (!isAutomaticRecovery) { _automaticRecoveryUsed = false; _recoveredAutomatically = false; }
        bool recoveryAttempt = HasPlaybackError && _clip?.Video == clip.Video;
        _recoveryCancellation?.Cancel();
        int generation = ++_generation; ++_audioSyncVersion; _synchronizingAudio = false; _loadStamp = Stopwatch.GetTimestamp(); _loadCancellation?.Cancel();
        using var cancellation = new CancellationTokenSource(); _loadCancellation = cancellation;
        double previewStart = startMilliseconds / 1000d;
        if (preferKnownStart && startMilliseconds == 0 && _clip?.Video == clip.Video && _currentHealth?.RecoveredStartSeconds is double cachedStart) previewStart = cachedStart;
        ShowLoadingCore(clip, previewStart); _loadOperationActive = true; bool entered = false;
        if (isAutomaticRecovery) LoadingLabel.Text = "Восстанавливаем просмотр…";
        try
        {
            await _videoGate.WaitAsync(cancellation.Token); entered = true;
            if (_disposed || _closingNative || generation != _generation) return;
            await StopMediaAsync(); cancellation.Token.ThrowIfCancellationRequested();
            await _healthWrite; cancellation.Token.ThrowIfCancellationRequested();
            _clip = clip; _playing = autoPlay; _atEnd = false; _nativeStarted = false; _audioReady = false; _seekPending = _seekQueued = false;
            try { _currentHealth = await _healthStore.ReadAsync(clip.Video, cancellation.Token); }
            catch (Exception error) when (error is IOException or InvalidDataException or System.Text.Json.JsonException or UnauthorizedAccessException)
            {
                // При ошибке кэша прежнее начало допустимо только для той же версии исходного файла.
                try
                {
                    PlaybackHealth currentVersion = PlaybackHealthStore.VersionOf(clip.Video);
                    if (_currentHealth?.Key != currentVersion.Key) _currentHealth = currentVersion;
                }
                catch (Exception sourceError) when (sourceError is IOException or UnauthorizedAccessException) { _currentHealth = null; }
                _telemetry.Event("player-health-read-error", new { clip.Video, error.Message });
            }
            cancellation.Token.ThrowIfCancellationRequested();
            // Отметка повреждения описывает участок файла, а не пригодность аппаратного декодера.
            RenderHealth();
            if (preferKnownStart && startMilliseconds == 0 && _currentHealth?.RecoveredStartSeconds is double known && known < clip.Duration)
            { startMilliseconds = (long)(known * 1000); _telemetry.Event("player-known-part", new { clip.Video, seconds = known }); }
            _position = _seekTarget = Math.Clamp(startMilliseconds / 1000d, 0, Math.Max(0, clip.Duration - .05)); Anchor(_position);
            double loadedStart = _seekTarget;
            _updatingMonitorClip = true;
            try
            {
                var choices = (clip.Screens ?? []).Select(screen => new MonitorClipChoice(screen)).ToArray();
                MonitorClipPicker.ItemsSource = choices; MonitorClipPicker.SelectedItem = choices.FirstOrDefault(choice => choice.Clip.Video == clip.Video);
                MonitorClipPicker.Visibility = choices.Length > 1 ? Visibility.Visible : Visibility.Collapsed;
            }
            finally { _updatingMonitorClip = false; }
            _updatingPosition = true; PositionSlider.Maximum = clip.Duration; PositionSlider.Value = _position; _updatingPosition = false;
            RenderPosition();
            BindNativeEvents(generation);
            long surfaceStamp = Stopwatch.GetTimestamp();
            await WaitForVideoSurfaceAsync(cancellation.Token);
            double surfaceReadyMs = Stopwatch.GetElapsedTime(surfaceStamp).TotalMilliseconds;
            long preparationStamp = Stopwatch.GetTimestamp();
            double videoReadyMs = 0, audioStartMs = 0, audioReadyMs = 0;
            async Task PrepareVideoAsync()
            {
                try
                {
                    await _video.OpenAsync(clip.Video, Video.SurfaceHandle, (long)(_position * 1000), _headless, cancellation.Token, allowDamaged || _softwareVideos.Contains(clip.Video), allowDamaged);
                    videoReadyMs = Stopwatch.GetElapsedTime(preparationStamp).TotalMilliseconds;
                }
                catch { cancellation.Cancel(); throw; }
            }
            async Task PrepareAudioAsync()
            {
                audioStartMs = Stopwatch.GetElapsedTime(preparationStamp).TotalMilliseconds;
                try
                {
                    await _audio.StartAsync(clip, _position, false, cancellation.Token);
                    audioReadyMs = Stopwatch.GetElapsedTime(preparationStamp).TotalMilliseconds;
                }
                catch { cancellation.Cancel(); throw; }
            }
            // Оба потока готовятся на паузе; ошибка одного отменяет подготовку второго.
            Task videoReady = PrepareVideoAsync();
            Task audioReady = PrepareAudioAsync();
            SystemSlider.IsEnabled = _audio.HasSource(0); DiscordSlider.IsEnabled = _audio.HasSource(1); MicrophoneSlider.IsEnabled = _audio.HasSource(2); ApplyVolumes();
            _positionTimer.Start(); RenderPosition(); UpdateAppearance();
            await Task.WhenAll(videoReady, audioReady);
            await _video.WaitUntilReadyAsync(cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            if (HasPlaybackError) throw new InvalidOperationException(_video.Error ?? "Видеоплеер завершился с ошибкой.");
            double actualVideo = _video.Time / 1000d;
            if (_position > .01 && Math.Abs(actualVideo - _position) > .08)
            {
                _position = Math.Clamp(actualVideo, 0, clip.Duration); await _audio.SeekAsync(_position, false, cancellation.Token);
                cancellation.Token.ThrowIfCancellationRequested();
            }
            _audioReady = true;
            Anchor(_position);
            UpdatePosition();
            if (IsLoaded) await Dispatcher.InvokeAsync(UpdateLayout, DispatcherPriority.Render);
            // После показа HWND подтверждаем кадр уже на видимой поверхности.
            await _video.SeekAsync((long)(_position * 1000), cancellation.Token);
            if (autoPlay) { await _video.SetPauseAsync(false, cancellation.Token); _audio.Resume(); }
            else { _audio.Pause(); if (_nativeStarted) _video.SetPause(true); }
            _telemetry.Event("player-load-stages", new { clip.Video, videoReadyMs, audioStartMs, audioReadyMs, preparationMs = Stopwatch.GetElapsedTime(preparationStamp).TotalMilliseconds, autoPlay,
                videoProcessId = _video.ProcessId, overlayCompatibility = _video.OverlayCompatibility, surfaceReadyMs, headless = _headless });
            // Следующая перемотка не меняет точку, проверенную этим открытием.
            if (recoveryAttempt && !allowDamaged && loadedStart > .01) await RememberHealthAsync(_currentHealth, [], loadedStart);
            if (!allowDamaged) _lastGoodPosition = loadedStart;
            // Не принимаем перемотку до завершения исходного seek и фиксации доступного начала.
            _loadOperationActive = false; UpdatePosition();
            _telemetry.Event("clip-loaded", new { file = clip.Video, milliseconds = Stopwatch.GetElapsedTime(_loadStamp).TotalMilliseconds, audioSources = _audio.Count, streaming = true, videoProcessId = _video.ProcessId });
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            if (generation == _generation && HasPlaybackError && !_closingNative)
                throw new InvalidOperationException(_video.Error ?? "Не удалось воспроизвести повтор.");
        }
        catch (Exception error)
        {
            if (generation == _generation && !_closingNative)
            {
                _telemetry.Event("player-load-failure", new { clip.Video, error.Message, details = _video.ErrorDetails });
                ShowError(error.Message, rememberFailure: _video.FailureMayIndicateDamage);
            }
            throw;
        }
        finally { if (entered) _videoGate.Release(); if (ReferenceEquals(_loadCancellation, cancellation)) _loadCancellation = null; }
    }

    private async Task WaitForVideoSurfaceAsync(CancellationToken cancellation)
    {
        if (_headless) return;
        if (!IsLoaded)
        {
            var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            RoutedEventHandler loaded = (_, _) => ready.TrySetResult();
            Loaded += loaded;
            try
            {
                if (IsLoaded) ready.TrySetResult();
                // Открытие до первого показа не должно незаметно включать vo=null.
                await ready.Task.WaitAsync(TimeSpan.FromSeconds(3), cancellation);
            }
            catch (TimeoutException error) { throw new InvalidOperationException("Не удалось дождаться готовности области видео.", error); }
            finally { Loaded -= loaded; }
        }
        await Dispatcher.InvokeAsync(UpdateLayout, DispatcherPriority.Loaded, cancellation);
        cancellation.ThrowIfCancellationRequested();
        if (Video.SurfaceHandle == 0) throw new InvalidOperationException("Не удалось подготовить область видео.");
    }

    private async Task StopMediaAsync()
    {
        _positionTimer.Stop(); _seekTimer.Stop();
        UnbindNativeEvents();
        await Task.WhenAll(_audio.StopAsync(), _video.StopAsync());
    }
    private async void OnMonitorClipSelected(object sender, SelectionChangedEventArgs args)
    {
        if (_updatingMonitorClip || MonitorClipPicker.SelectedItem is not MonitorClipChoice choice || _clip is null || choice.Clip.Video == _clip.Video) return;
        try { await LoadClipAsync(choice.Clip with { Screens = _clip.Screens }, (long)(_position * 1000), _playing); } catch (Exception error) { ShowError(error.Message, rememberFailure: false); }
    }
    public void PausePlayback()
    {
        if (_disposed || _closingNative) return;
        UpdatePosition(); _playing = false; _audio.Pause(); if (_nativeStarted) _video.SetPause(true);
        MixerPopup.IsOpen = ActionsPopup.IsOpen = PlayerSettingsPopup.IsOpen = false; UpdateAppearance();
    }
    public void TogglePlayback()
    {
        if (_riskRequest is not null) return;
        if (_clip is null || _disposed || _closingNative || _loading || _deleteBusy) return;
        if (HasPlaybackError) { SeekTo(_position); return; }
        if (_atEnd) { _ = RestartAsync(); return; }
        if (_playing) PausePlayback();
        else { _playing = true; if (!_seekPending && !_seekQueued && !_synchronizingAudio) SyncAudio(_position); _positionTimer.Start(); UpdateAppearance(); }
    }
    private async Task RestartAsync()
    { if (_clip is not null) try { await LoadClipAsync(_clip); } catch (Exception error) { ShowError(error.Message, rememberFailure: false); } }

    public void SeekTo(double seconds) => SeekTo(seconds, allowUnavailableStart: true);
    private void SeekTo(double seconds, bool allowUnavailableStart)
    {
        if (_clip is null || !double.IsFinite(seconds) || _disposed || _closingNative || _loading) return;
        if (_riskRequest is not null) return;
        _automaticRecoveryUsed = false;
        double target = Math.Clamp(seconds, 0, Math.Max(0, _clip.Duration - 1 / Math.Max(1, _clip.Fps)));
        double start = PlaybackStart;
        if (target < start)
        {
            bool fromStart = allowUnavailableStart && (_timelineGestureActive ? _timelineGestureFromStart : AtPlaybackStart);
            if (fromStart) { AskRiskConfirmation(target); return; }
            target = start;
            _updatingPosition = true; PositionSlider.Value = start; _updatingPosition = false;
        }
        if (!(Math.Abs(target - start) <= .005 && (start > 0 || !allowUnavailableStart)) && RiskRibbon.IsRisky(target)) { AskRiskConfirmation(target); return; }
        if (HasPlaybackError)
        {
            _recoveryCancellation?.Cancel();
            _position = _seekTarget = target; _seekQueued = true;
            _seekTimer.Stop(); _seekTimer.Start(); RenderPosition(); return;
        }
        if (_atEnd) { _ = SeekFromEndAsync(target); return; }
        ++_audioSyncVersion; _synchronizingAudio = false;
        _position = _seekTarget = target; _seekQueued = true; _audio.Pause(); _video.SetPause(true);
        if (!_seekTimer.IsEnabled) _seekTimer.Start(); RenderPosition(); UpdateAppearance();
    }
    private async Task SeekFromEndAsync(double target)
    { if (_clip is not null) try { await LoadClipAsync(_clip, (long)(target * 1000), false); } catch (Exception error) { ShowError(error.Message, rememberFailure: false); } }
    public void SeekBy(double seconds) => SeekTo(Math.Max(PlaybackStart, _position + seconds), allowUnavailableStart: false);
    private void OnGoToStart(object sender, RoutedEventArgs args) => SeekTo(PlaybackStart, allowUnavailableStart: false);
    private async void IssueSeek(bool force = false)
    {
        if (HasPlaybackError && _seekQueued && _clip is not null)
        {
            _seekQueued = false; _seekTimer.Stop(); _ = RetryAtAsync(_seekTarget); return;
        }
        if (!_seekQueued || !_nativeStarted || _clip is null || _disposed) { if (!_seekQueued) _seekTimer.Stop(); return; }
        if (_seekPending) return;
        _seekQueued = false; _seekPending = true; _issuedTarget = _seekTarget; _seekStamp = Stopwatch.GetTimestamp();
        int generation = _generation;
        double target = _issuedTarget;
        _seekTimer.Stop();
        _telemetry.Event("player-seek-issued", new { target = _issuedTarget });
        try
        {
            await _video.SeekAsync((long)(target * 1000));
            if (generation != _generation || _closingNative || _disposed || !_seekPending) return;
            ResolveSeek(_video.Time / 1000d);
        }
        catch (Exception error)
        {
            if (generation == _generation && !_closingNative && !_disposed)
            { _seekPending = false; HandleNativeFailure(error.Message, rememberPosition: false); }
        }
    }
    private async Task RetryAtAsync(double seconds)
    { if (_clip is not null) try { await LoadClipAsync(_clip, (long)(seconds * 1000), preferKnownStart: false); } catch (Exception error) { ShowError(error.Message, rememberFailure: false); } }
    private void AskRiskConfirmation(double target)
    {
        if (_clip is not Clip clip) return;
        bool resume = _playing; PausePlayback(); _recoveryCancellation?.Cancel();
        _seekTimer.Stop(); _seekQueued = _seekPending = false;
        _riskRequest = new(clip, target, resume, _generation);
        RiskTargetText.Text = "Переход к " + Time(target);
        GoToSafePartButton.Visibility = _currentHealth?.RecoveredStartSeconds is double ? Visibility.Visible : Visibility.Collapsed;
        SafePartLabel.Text = _currentHealth?.RecoveredStartSeconds is double safe ? "К доступному · " + Time(safe) : "К доступному фрагменту";

        Video.Visibility = Visibility.Hidden; RiskConfirmation.Visibility = Visibility.Visible;
        _updatingPosition = true; PositionSlider.Value = _position; _updatingPosition = false;
        UpdateAppearance(); CancelRiskButton.Focus();
    }
    private RiskRequest? DismissRisk()
    {
        RiskRequest? request = _riskRequest; _riskRequest = null; RiskConfirmation.Visibility = Visibility.Collapsed;
        if (!_loading && !HasPlaybackError) Video.Visibility = Visibility.Visible;
        UpdateAppearance(); return request;
    }
    private void OnCancelRisk(object sender, RoutedEventArgs args)
    { RiskRequest? request = DismissRisk(); if (request?.Resume == true && request.Generation == _generation) TogglePlayback(); }
    private async void OnConfirmRisk(object sender, RoutedEventArgs args)
    {
        RiskRequest? request = DismissRisk(); if (request is null || request.Generation != _generation || _closingNative) return;
        _telemetry.Event("player-risk-confirmed", new { request.Clip.Video, request.Target });
        try { await LoadClipAsync(request.Clip, (long)(request.Target * 1000), request.Resume, preferKnownStart: false, allowDamaged: true); }
        catch (Exception error) { ShowError(error.Message, rememberFailure: false); }
    }
    private async void OnGoToSafePart(object sender, RoutedEventArgs args)
    {
        double? safe = _currentHealth?.RecoveredStartSeconds; RiskRequest? request = DismissRisk();
        if (safe is null || request is null || request.Generation != _generation || _closingNative) return;
        try { await LoadClipAsync(request.Clip, (long)(safe.Value * 1000), request.Resume); } catch (Exception error) { ShowError(error.Message, rememberFailure: false); }
    }
    private async void OnRestartPlayer(object sender, RoutedEventArgs args)
    { if (_clip is not null) try { await LoadClipAsync(_clip, (long)((_currentHealth?.RecoveredStartSeconds ?? _lastGoodPosition) * 1000), autoPlay: true); } catch (Exception error) { ShowError(error.Message, rememberFailure: false); } }
    private void ResolveSeek(double seconds)
    {
        _seekPending = false;
        if (_seekQueued) { IssueSeek(force: true); return; }
        _position = _seekTarget; Anchor(_seekTarget);
        if (!PositionSlider.IsMouseCaptured) SyncAudio(_position);
        _telemetry.Event("player-seek-resolved", new { target = _seekTarget, seconds });
    }
    private async void SyncAudio(double seconds)
    {
        int version = ++_audioSyncVersion, generation = _generation;
        _synchronizingAudio = true;
        try
        {
            if (_nativeStarted) await _video.SetPauseAsync(true);
            if (version != _audioSyncVersion || generation != _generation || _closingNative) return;
            await _audio.SeekAsync(seconds, false);
            if (version != _audioSyncVersion || generation != _generation || _closingNative) return;
            Anchor(seconds);
            if (_playing && !_seekPending && !_seekQueued && !_atEnd && !PositionSlider.IsMouseCaptured)
            {
                await _video.SetPauseAsync(false);
                if (version == _audioSyncVersion && generation == _generation && _playing && !_closingNative)
                { Anchor(seconds); _audio.Resume(); }
            }
        }
        catch (Exception error) { if (version == _audioSyncVersion && generation == _generation) ShowError(error.Message, rememberFailure: false); }
        finally { if (version == _audioSyncVersion) { _synchronizingAudio = false; UpdateAppearance(); } }
    }

    private void UpdatePosition()
    {
        long stamp = Stopwatch.GetTimestamp();
        double elapsed = _positionStamp == 0 ? 0 : Stopwatch.GetElapsedTime(_positionStamp, stamp).TotalSeconds;
        _positionStamp = stamp;
        if (_clip is null || _disposed || _closingNative) return;
        if (_loading && !_loadOperationActive && _audioReady && _nativeStarted && (!IsLoaded || _video.VoutCount > 0) && Stopwatch.GetElapsedTime(_loadStamp).TotalMilliseconds >= 120)
        {
            _loading = false; _loadOperationActive = false; LoadingOverlay.Visibility = Visibility.Collapsed; Video.Visibility = Visibility.Visible;
            _telemetry.Event("player-first-frame", new { milliseconds = Stopwatch.GetElapsedTime(_loadStamp).TotalMilliseconds }); UpdateAppearance();
        }
        if (_loading && _loadOperationActive && Stopwatch.GetElapsedTime(_loadStamp).TotalSeconds > 15) { HandleNativeFailure("Плеер не завершил загрузку видео.", rememberPosition: false); return; }
        if (_loading) return;
        if (_atEnd)
        {
            _position = Math.Min(_clip.Duration, _position + elapsed);
            RenderPosition(); UpdatePositionRendering(); return;
        }
        if (!_nativeStarted) return;
        if (!_seekPending && !_seekQueued && !_synchronizingAudio && _playing && !PositionSlider.IsMouseCaptured)
        {
            double age = Stopwatch.GetElapsedTime(_anchorStamp, stamp).TotalSeconds;
            double estimate = _anchorPosition + Math.Min(.6, age);
            // Редкие метки декодера корректируют скорость часов, а не переставляют бегунок.
            double predicted = _position + elapsed;
            double corrected = predicted + Math.Clamp(estimate - predicted, -elapsed * .1, elapsed * .1);
            if (age > .6) corrected = Math.Min(corrected, estimate);
            _position = Math.Clamp(Math.Max(_position, corrected), 0, _clip.Duration);
        }
        if (_seekPending && Stopwatch.GetElapsedTime(_seekStamp).TotalSeconds > 4)
        { _seekPending = false; _seekQueued = false; HandleNativeFailure("Плеер не завершил переход к выбранному месту.", rememberPosition: false); }
        RenderPosition();
    }
    private void OnPositionRendering(object? sender, EventArgs args)
    {
        if (args is RenderingEventArgs frame)
        {
            if (frame.RenderingTime == _lastPositionRendering) return;
            _lastPositionRendering = frame.RenderingTime;
        }
        UpdatePosition();
    }
    private void UpdatePositionRendering()
    {
        bool render = IsVisible && !_disposed && !_closingNative && (_loading || _playing && !_atEnd || _atEnd && _position < (_clip?.Duration ?? 0) || _seekPending || _seekQueued);
        if (render == _renderingPosition) return;
        _renderingPosition = render; _positionStamp = Stopwatch.GetTimestamp(); _lastPositionRendering = TimeSpan.MinValue;
        if (render) CompositionTarget.Rendering += OnPositionRendering;
        else CompositionTarget.Rendering -= OnPositionRendering;
    }
    private void RenderPosition()
    {
        if (_clip is null) return;
        RiskRibbon.SetPlayhead(_position);
        _updatingPosition = true; if (!PositionSlider.IsMouseCaptured) PositionSlider.Value = _position; _updatingPosition = false;
        TimeText.Text = $"{Time(_position)} / {Time(_clip.Duration)}";
    }
    private static string Time(double seconds) => TimeSpan.FromSeconds(Math.Max(0, seconds)).ToString(seconds >= 3600 ? @"h\:mm\:ss" : @"m\:ss");
    private void UpdateAppearance()
    {
        UpdatePositionRendering();
        if (PlayerStateText is null) return;
        RecoveryNotice.Visibility = _recoveredAutomatically ? Visibility.Visible : Visibility.Collapsed;
        PlayGlyph.Symbol = HasPlaybackError || _atEnd ? Wpf.Ui.Controls.SymbolRegular.ArrowClockwise24 : _playing ? Wpf.Ui.Controls.SymbolRegular.Pause24 : Wpf.Ui.Controls.SymbolRegular.Play24;
        string playbackAction = HasPlaybackError ? "Повторить" : _atEnd ? "Сначала" : _playing ? "Пауза" : "Играть";
        PlayButton.ToolTip = playbackAction + (_hotkeys.Pause is null ? "" : " · " + _hotkeys.Pause.Label);
        System.Windows.Automation.AutomationProperties.SetName(PlayButton, playbackAction);
        PlayerStateText.Text = _restartingNative ? "Восстановление…" : _finding ? "Поиск фрагмента…" : _loading ? "Загрузка…" : HasPlaybackError ? "Участок недоступен" : _atEnd ? "Конец" : _seekQueued || _seekPending || _synchronizingAudio ? "Перемотка…" : _playing ? "Воспроизведение" : "Пауза";
        PlayButton.IsEnabled = !_loading && !_deleteBusy && _riskRequest is null && _clip is { Duration: > 0 };
        PositionSlider.IsEnabled = !_loading && !_deleteBusy && _riskRequest is null && _clip is { Duration: > 0 };
        FindAvailablePartButton.IsEnabled = !_finding && _clip is { Duration: > 0 };
        RestartPlayerButton.IsEnabled = !_restartingNative && !_loading && _clip is not null;
        GoToStartButton.IsEnabled = PlayButton.IsEnabled;
        SeekBackwardButton.IsEnabled = SeekForwardButton.IsEnabled = PlayButton.IsEnabled;
        GoToStartButton.ToolTip = PlaybackStart > 0 ? "К началу доступного фрагмента" : "К началу видео";
        ActionsButton.IsEnabled = DeleteClipButton.IsEnabled = ShowClipInExplorerButton.IsEnabled = !_loading && !_deleteBusy && _clip is not null;
    }
    private void OnSeek(object sender, RoutedPropertyChangedEventArgs<double> args) { if (!_updatingPosition) SeekTo(args.NewValue, allowUnavailableStart: _timelineGestureActive); }
    private void OnPlay(object sender, RoutedEventArgs args) { Focus(); TogglePlayback(); }
    private void OnVideoClicked(object? sender, EventArgs args) { Focus(); TogglePlayback(); }
    private void PreparePopupToggle(Popup popup, Button trigger)
    {
        popup.Closed += (_, _) =>
        {
            // Popup отпускает захват до Click кнопки: повторное нажатие должно закрывать меню.
            Point point = Mouse.GetPosition(trigger);
            if (Mouse.LeftButton == MouseButtonState.Pressed && new Rect(trigger.RenderSize).Contains(point))
                _dismissedPopupTrigger = trigger;
        };
    }
    private void TogglePopup(Popup popup, Button trigger)
    {
        bool dismissed = _dismissedPopupTrigger == trigger; _dismissedPopupTrigger = null;
        bool open = !dismissed && !popup.IsOpen;
        MixerPopup.IsOpen = ActionsPopup.IsOpen = PlayerSettingsPopup.IsOpen = false;
        popup.IsOpen = open;
    }
    private void OnMixer(object sender, RoutedEventArgs args) => TogglePopup(MixerPopup, SoundButton);
    private void OnActions(object sender, RoutedEventArgs args) => TogglePopup(ActionsPopup, ActionsButton);
    private void OnPlayerSettings(object sender, RoutedEventArgs args) => TogglePopup(PlayerSettingsPopup, PlayerSettingsButton);
    private void OnPanelTracksChanged(object sender, RoutedEventArgs args)
    {
        if (!_ready) return;
        bool visible = PanelTracksToggle.IsChecked == true;
        InlineTracks.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        SavePlayerPreferences();
    }
    private void OnSeekStepChanged(object sender, SelectionChangedEventArgs args)
    {
        if (sender is not ComboBox { SelectedItem: ComboBoxItem { Tag: string value } } || !int.TryParse(value, out int seconds)) return;
        _seekStepSeconds = seconds;
        if (!_ready) return;
        UpdateSeekButtons(); SavePlayerPreferences();
    }
    private void SavePlayerPreferences()
    {
        try
        {
            string temporary = _preferencesPath + ".tmp";
            File.WriteAllText(temporary, System.Text.Json.JsonSerializer.Serialize(new PlayerPreferences(PanelTracksToggle.IsChecked == true, _seekStepSeconds)));
            File.Move(temporary, _preferencesPath, overwrite: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { _telemetry.Event("player-settings-write-error", new { error.Message }); }
    }
    private void OnActionsOpened(object? sender, EventArgs args) => ShowClipInExplorerButton.Focus();
    private void OnActionsKeyDown(object sender, KeyEventArgs args)
    {
        if (args.Key == Key.Escape) { ActionsPopup.IsOpen = false; ActionsButton.Focus(); args.Handled = true; }
        else if (args.Key is Key.Up or Key.Down && Keyboard.FocusedElement is UIElement focused)
        { focused.MoveFocus(new TraversalRequest(args.Key == Key.Up ? FocusNavigationDirection.Previous : FocusNavigationDirection.Next)); args.Handled = true; }
    }
    private void OnShowInExplorer(object sender, RoutedEventArgs args)
    {
        ActionsPopup.IsOpen = false;
        if (_clip is null) return;
        try
        {
            ExplorerSelection.SelectFile(_clip.Video);
            ExplorerOpened?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception error) { _telemetry.Event("player-explorer-error", new { error.Message }); }
    }
    private void OnClosePlayer(object sender, RoutedEventArgs args) => CloseRequested?.Invoke(this, EventArgs.Empty);

    private void OnDeleteClip(object sender, RoutedEventArgs args)
    {
        if (_clip is null || _loading || _deleteBusy) return;
        ActionsPopup.IsOpen = false;
        _deleteClip = _clip; _resumeAfterCancel = _playing; PausePlayback();
        DeleteFilenameText.Text = Path.GetFileName(_clip.Video);
        DeleteScopeText.Text = _clip.Screens is { Length: > 1 } ? "Остальные экраны останутся." : "";
        DeleteConfirmation.Visibility = Visibility.Visible;
        DeletePopup.IsOpen = true;
    }
    private void OnCancelDelete(object sender, RoutedEventArgs args)
    {
        if (_deleteBusy) return;
        DeletePopup.IsOpen = false; DismissDelete();
    }
    private void OnDeletePopupOpened(object? sender, EventArgs args) => CancelDeleteButton.Focus();
    private void OnDeletePopupClosed(object? sender, EventArgs args) => DismissDelete();
    private void OnDeletePopupKeyDown(object sender, KeyEventArgs args)
    { if (args.Key == Key.Escape) { OnCancelDelete(this, new()); args.Handled = true; } }
    private void DismissDelete()
    {
        if (_deleteBusy) return;
        bool resume = _resumeAfterCancel; _resumeAfterCancel = false;
        _deleteClip = null; DeleteConfirmation.Visibility = Visibility.Collapsed;
        if (resume && !_closingNative && !_disposed && !_loading && !HasPlaybackError && !_playing) TogglePlayback();
    }
    private async void OnConfirmDelete(object sender, RoutedEventArgs args)
    {
        Clip? selected = _deleteClip; if (selected is null || _deleteBusy) return;
        _deleteBusy = true; ConfirmDeleteButton.IsEnabled = CancelDeleteButton.IsEnabled = false; UpdateAppearance();
        try
        {
            await _videoGate.WaitAsync();
            try { await StopMediaAsync(); } finally { _videoGate.Release(); }
            await ReplayClipDeletion.RecycleAsync(selected.Video);
            _clip = null; _resumeAfterCancel = false; DeletePopup.IsOpen = false; DeleteConfirmation.Visibility = Visibility.Collapsed; ClipDeleted?.Invoke(this, new(selected.Video));
        }
        catch (Exception error) { _telemetry.Event("player-delete-error", new { error = error.ToString() }); _resumeAfterCancel = false; DeletePopup.IsOpen = false; DeleteConfirmation.Visibility = Visibility.Collapsed; ShowError(error.Message); }
        finally { _deleteBusy = false; _deleteClip = null; ConfirmDeleteButton.IsEnabled = CancelDeleteButton.IsEnabled = true; UpdateAppearance(); }
    }

    private void OnKeyDown(object sender, KeyEventArgs args)
    {
        if (_riskRequest is not null) { if (args.Key == Key.Escape) { OnCancelRisk(this, new()); args.Handled = true; } return; }
        if (args.OriginalSource is TextBox || MonitorClipPicker.IsDropDownOpen || SeekStepPicker.IsKeyboardFocusWithin) return;
        if (DeleteConfirmation.Visibility == Visibility.Visible) { if (args.Key == Key.Escape) OnCancelDelete(this, new()); else return; }
        else if (args.Key == Key.Escape) { if (ActionsPopup.IsOpen) ActionsPopup.IsOpen = false; else if (MixerPopup.IsOpen) MixerPopup.IsOpen = false; else if (PlayerSettingsPopup.IsOpen) PlayerSettingsPopup.IsOpen = false; else CloseRequested?.Invoke(this, EventArgs.Empty); }
        else
        {
            ModifierKeys modifiers = Keyboard.Modifiers; if ((modifiers & ModifierKeys.Windows) != 0) return;
            uint mask = ((modifiers & ModifierKeys.Control) != 0 ? 2u : 0) | ((modifiers & ModifierKeys.Alt) != 0 ? 1u : 0) | ((modifiers & ModifierKeys.Shift) != 0 ? 4u : 0);
            Key key = args.Key == Key.System ? args.SystemKey : args.Key;
            if (!HandleShortcut(new(mask, (uint)KeyInterop.VirtualKeyFromKey(key)))) return;
        }
        args.Handled = true;
    }
    public bool HandleShortcut(HotkeyChord chord)
    {
        if (_riskRequest is not null || DeletePopup.IsOpen) return false;
        if (chord == _hotkeys.Pause) TogglePlayback();
        else if (chord == _hotkeys.PreviousFrame) { PausePlayback(); SeekBy(-1 / (_clip?.Fps ?? 30)); }
        else if (chord == _hotkeys.NextFrame) { PausePlayback(); SeekBy(1 / (_clip?.Fps ?? 30)); }
        else if (chord == _hotkeys.Backward && !MixerPopup.IsOpen) SeekBy(-_seekStepSeconds);
        else if (chord == _hotkeys.Forward && !MixerPopup.IsOpen) SeekBy(_seekStepSeconds);
        else if (chord == _hotkeys.GoToStart) SeekTo(PlaybackStart, allowUnavailableStart: false);
        else if (chord == _hotkeys.Delete) OnDeleteClip(this, new RoutedEventArgs());
        else return false;
        return true;
    }
    private void OnPreviousFrame(object sender, RoutedEventArgs args) { PausePlayback(); SeekBy(-1 / (_clip?.Fps ?? 30)); }
    private void OnNextFrame(object sender, RoutedEventArgs args) { PausePlayback(); SeekBy(1 / (_clip?.Fps ?? 30)); }
    private void OnSeekBackward(object sender, RoutedEventArgs args) => SeekBy(-_seekStepSeconds);
    private void OnSeekForward(object sender, RoutedEventArgs args) => SeekBy(_seekStepSeconds);
    public void SetAudioVolume(int source, double volume)
    { if (source is < 0 or > 2 || !double.IsFinite(volume) || volume is < 0 or > 1) throw new ArgumentOutOfRangeException(nameof(source)); new[] { SystemSlider, DiscordSlider, MicrophoneSlider }[source].Value = volume * 100; }
    private void OnVolume(object sender, RoutedPropertyChangedEventArgs<double> args)
    {
        if (!_ready) return; var slider = (Slider)sender;
        if ((string)slider.Tag == "master") _mix.Master = args.NewValue / 100; else _mix.SetSource(int.Parse((string)slider.Tag, CultureInfo.InvariantCulture), args.NewValue / 100); ApplyVolumes();
    }
    private void ApplyVolumes()
    {
        for (int index = 0; index < 3; index++) _audio.SetVolume(index, _mix.EffectiveGain(index));
        SystemValue.Text = SystemSlider.IsEnabled ? $"{SystemSlider.Value:F0}%" : "—"; DiscordValue.Text = DiscordSlider.IsEnabled ? $"{DiscordSlider.Value:F0}%" : "—";
        MicrophoneValue.Text = MicrophoneSlider.IsEnabled ? $"{MicrophoneSlider.Value:F0}%" : "—"; MasterValue.Text = $"{MasterSlider.Value:F0}%";
    }
    public void ShowError(string message, bool rememberFailure = true)
    {
        if (_disposed || _closingNative) return;
        _riskRequest = null; RiskConfirmation.Visibility = Visibility.Collapsed;
        _resumeAfterCancel = false; DeletePopup.IsOpen = false; DeleteConfirmation.Visibility = Visibility.Collapsed;
        PlaybackErrorTitle.Text = "Воспроизведение остановлено";
        _recoveredAutomatically = false;
        if (rememberFailure) _ = RememberHealthAsync(_currentHealth, [_position]);
        _loadCancellation?.Cancel();
        _loading = false; _loadOperationActive = false; _playing = false; _nativeStarted = false; _seekPending = _seekQueued = false;
        ++_audioSyncVersion; _synchronizingAudio = false; _audio.Pause(); _positionTimer.Stop(); _seekTimer.Stop();
        _ = ReleaseFailedMediaAsync();
        Video.Visibility = Visibility.Hidden; LoadingOverlay.Visibility = Visibility.Collapsed;
        PlaybackError.Text = message; PlaybackErrorPanel.Visibility = Visibility.Visible; RenderPosition(); UpdateAppearance(); _telemetry.Event("player-error", new { message });
    }
    private void HandleNativeFailure(string message, bool rememberPosition = true)
    {
        if (_disposed || _closingNative) return;
        Clip? clip = _clip; int generation = _generation; bool resume = _playing;
        if (clip is not null) _softwareVideos.Add(clip.Video);
        double safe = _currentHealth?.RecoveredStartSeconds ?? _lastGoodPosition;
        // Сбой декодера не доказывает, что ранее проверенная точка файла стала нечитаемой.
        if (rememberPosition) _ = RememberHealthAsync(_currentHealth, [_position], failureKind: "decoder-failed", invalidateKnownStart: false);
        ShowError(message, rememberFailure: false); PlaybackErrorTitle.Text = "Сбой видеоплеера";
        if (_automaticRecoveryUsed || clip is null)
        { PlaybackError.Text = "Плеер снова остановился. Можно перезапустить его вручную или найти другой фрагмент. Rewind продолжает работать."; return; }
        _automaticRecoveryUsed = true; _restartingNative = true;
        PlaybackError.Text = "Перезапускаем видеоплеер с доступного места. Rewind продолжает работать."; UpdateAppearance();
        _nativeRecoveryTask = RestoreNativeAsync(clip, safe, resume, generation);
    }
    private async Task RestoreNativeAsync(Clip clip, double seconds, bool resume, int generation)
    {
        await Task.Yield();
        int recoveryGeneration = generation + 1;
        try
        {
            if (_closingNative || _disposed || generation != _generation) return;
            _telemetry.Event("player-auto-recovery-start", new { clip.Video, seconds });
            await LoadClipAsync(clip, (long)(seconds * 1000), resume, isAutomaticRecovery: true);
            if (_closingNative || recoveryGeneration != _generation || HasPlaybackError) return;
            _recoveredAutomatically = true;
            _telemetry.Event("player-auto-recovery-complete", new { clip.Video, processId = _video.ProcessId, position = _position });
        }
        catch (Exception error)
        {
            if (!_closingNative && recoveryGeneration == _generation && _clip?.Video == clip.Video)
            { ShowError("Автоматически восстановить просмотр не удалось. Перезапусти плеер или выбери другой участок.", rememberFailure: false); PlaybackErrorTitle.Text = "Нужен другой участок записи"; }
            _telemetry.Event("player-auto-recovery-error", new { error.Message });
        }
        finally { _restartingNative = false; if (!_closingNative && !_disposed) UpdateAppearance(); }
    }
    private async void OnFindAvailablePart(object sender, RoutedEventArgs args)
    {
        if (_finding || _closingNative || _disposed) return;
        Task recovery = FindAvailablePartAsync(); _recoveryTask = recovery;
        try { await recovery; }
        finally { if (ReferenceEquals(_recoveryTask, recovery)) _recoveryTask = null; }
    }
    private async Task FindAvailablePartAsync()
    {
        if (_clip is not Clip clip || _finding || _closingNative || _disposed) return;
        int generation = _generation;
        PlaybackHealth? health = _currentHealth; var failedProbes = new List<double>();
        using var cancellation = new CancellationTokenSource(); _recoveryCancellation = cancellation;
        _finding = true; CancelRecoveryButton.Visibility = Visibility.Visible; UpdateAppearance();
        try
        {
            double? found = await VideoRecovery.FindNextAsync(clip.Video, _position, clip.Duration, new Progress<double>(seconds =>
            {
                if (generation == _generation && !cancellation.IsCancellationRequested)
                    PlaybackError.Text = $"Ищем доступный фрагмент… {Time(seconds)} из {Time(clip.Duration)}";
            }), failedProbes.Add, cancellation.Token);
            if (generation != _generation || cancellation.IsCancellationRequested || _closingNative) return;
            if (found is double seconds)
            {
                _telemetry.Event("player-part-found", new { clip.Video, seconds });
                await LoadClipAsync(clip, (long)(seconds * 1000));
            }
            else PlaybackError.Text = "Поиск не нашёл доступный фрагмент. Можно выбрать другое место по шкале — часть записи всё ещё может читаться.";
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        { if (generation == _generation && !_closingNative) PlaybackError.Text = "Поиск остановлен. Можно выбрать другое место по шкале."; }
        catch (Exception error)
        { if (generation == _generation && !_closingNative) ShowError("Не удалось найти фрагмент. Можно выбрать другое место по шкале.", rememberFailure: false); _telemetry.Event("player-recovery-error", new { error.Message }); }
        finally
        {
            if (failedProbes.Count > 0) await RememberHealthAsync(health, failedProbes);
            if (ReferenceEquals(_recoveryCancellation, cancellation))
            { _recoveryCancellation = null; _finding = false; CancelRecoveryButton.Visibility = Visibility.Collapsed; if (!_closingNative) UpdateAppearance(); }
        }
    }
    private void OnCancelRecovery(object sender, RoutedEventArgs args) => _recoveryCancellation?.Cancel();
    private Task RememberHealthAsync(PlaybackHealth? health, IReadOnlyList<double> failures, double? recoveredStart = null, string failureKind = "unavailable-at-position", bool invalidateKnownStart = true)
    {
        if (health is null) return Task.CompletedTask;
        if (_currentHealth?.Key == health.Key)
        {
            _currentHealth = _currentHealth with { Issues = _currentHealth.Issues.Concat(failures.Select(seconds => new PlaybackIssue(seconds, failureKind, DateTime.UtcNow))).ToArray() };
            RenderHealth();
            if (failures.Count > 0) PlaybackHealthChanged?.Invoke(this, EventArgs.Empty);
        }
        Task previous = _healthWrite;
        return _healthWrite = SaveAsync();
        async Task SaveAsync()
        {
            await previous;
            try { await _healthStore.SaveAsync(health, failures, recoveredStart, failureKind, invalidateKnownStart); }
            catch (Exception error) when (error is IOException or InvalidDataException or System.Text.Json.JsonException or UnauthorizedAccessException)
            { _telemetry.Event("player-health-write-error", new { health.File, error.Message }); }
            if (_currentHealth?.Key == health.Key && !_disposed && !_closingNative)
            {
                try { _currentHealth = await _healthStore.ReadAsync(health.File, CancellationToken.None); RenderHealth(); }
                catch (Exception error) when (error is IOException or InvalidDataException or System.Text.Json.JsonException or UnauthorizedAccessException)
                { _telemetry.Event("player-health-read-error", new { error.Message }); }
            }
        }
    }
    private void RenderHealth()
    {
        RiskRibbon.SetHealth(_currentHealth, _clip?.Duration ?? 0);
        PlaybackHealthIcon.Visibility = _currentHealth is { Issues.Length: > 0 } ? Visibility.Visible : Visibility.Collapsed;
        PlaybackHealthIcon.ToolTip = RiskRibbon.Description;
    }
    private async Task ReleaseFailedMediaAsync()
    {
        await _videoGate.WaitAsync();
        try { if (HasPlaybackError && !_closingNative) await StopMediaAsync(); }
        catch (Exception error) { _telemetry.Event("player-release-error", new { error.Message }); }
        finally { _videoGate.Release(); }
    }
    public Task ClosePlayerAsync() => _closeTask ??= CloseCoreAsync();
    private async Task CloseCoreAsync()
    {
        var closeTimer = Stopwatch.StartNew();
        _closingNative = true; ++_generation; _loadCancellation?.Cancel(); _recoveryCancellation?.Cancel(); _positionTimer.Stop(); _seekTimer.Stop(); MixerPopup.IsOpen = ActionsPopup.IsOpen = PlayerSettingsPopup.IsOpen = false;
        UpdatePositionRendering();
        _riskRequest = null; RiskConfirmation.Visibility = Visibility.Collapsed;
        _resumeAfterCancel = false; DeletePopup.IsOpen = false; DeleteConfirmation.Visibility = Visibility.Collapsed;
        if (_recoveryTask is Task recovery) await recovery;
        if (_nativeRecoveryTask is Task nativeRecovery) await nativeRecovery;
        await _healthWrite;
        await _videoGate.WaitAsync();
        double preparedMs = closeTimer.Elapsed.TotalMilliseconds;
        double stoppedMs = 0, surfaceMs = 0;
        try { await StopMediaAsync(); stoppedMs = closeTimer.Elapsed.TotalMilliseconds; }
        finally
        {
            try { Video.Dispose(); surfaceMs = closeTimer.Elapsed.TotalMilliseconds; }
            finally
            {
                try { _audio.Dispose(); }
                finally
                {
                    _disposed = true; _videoGate.Release();
                    _telemetry.Event("player-close-stages", new { preparedMs, stoppedMs, surfaceMs, totalMs = closeTimer.Elapsed.TotalMilliseconds });
                }
            }
        }
    }
}
