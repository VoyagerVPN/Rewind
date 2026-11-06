using Rewind.Preview;

string ffmpegRoot = Path.Combine(Path.GetTempPath(), "rewind-ffmpeg-" + Guid.NewGuid().ToString("N"));
try
{
    string app = Path.Combine(ffmpegRoot, "app");
    string tools = Path.Combine(app, "tools");
    string links = Path.Combine(ffmpegRoot, "Microsoft", "WinGet", "Links");
    Directory.CreateDirectory(tools);
    Directory.CreateDirectory(links);
    File.WriteAllText(Path.Combine(tools, "ffmpeg.exe"), "");
    File.WriteAllText(Path.Combine(links, "ffprobe.exe"), "");

    FfmpegTools? Find() => FfmpegTools.Find(app, ffmpegRoot, null);
    int installations = 0;
    async Task Install()
    {
        installations++;
        await File.WriteAllTextAsync(Path.Combine(links, "ffmpeg.exe"), "");
    }

    if (Find() is not null || !await FfmpegTools.EnsureAsync(Find, Install) ||
        !await FfmpegTools.EnsureAsync(Find, Install) || installations != 1 ||
        Find()?.Ffmpeg != Path.Combine(links, "ffmpeg.exe"))
        throw new InvalidOperationException("Setup must install a complete pair once, then reuse it.");

    File.Delete(Path.Combine(links, "ffmpeg.exe"));
    if (await FfmpegTools.EnsureAsync(Find, () => Task.CompletedTask))
        throw new InvalidOperationException("A successful command without a complete pair must not satisfy setup.");
    Console.WriteLine("PASS ffmpeg_setup_reuses_complete_pairs_and_verifies_install");
}
finally { Directory.Delete(ffmpegRoot, true); }

string startupPath = Path.Combine(Path.GetTempPath(), "rewind-startup-" + Guid.NewGuid().ToString("N"), "startup.json");
try
{
    var startupFile = new StartupPreferencesFile(startupPath);
    if (startupFile.Load().ShouldStartRecording) throw new InvalidOperationException("First launch must not enable recording without a previous state.");
    startupFile.Save(new StartupPreferences(LastRecordingEnabled: true));
    if (!startupFile.Load().ShouldStartRecording) throw new InvalidOperationException("Enabled recording must resume on next launch.");
    startupFile.Save(new StartupPreferences(LastRecordingEnabled: false));
    if (startupFile.Load().ShouldStartRecording) throw new InvalidOperationException("Disabled recording must stay disabled.");
    startupFile.Save(new StartupPreferences(AlwaysStartRecording: true, LastRecordingEnabled: false));
    if (!startupFile.Load().ShouldStartRecording) throw new InvalidOperationException("The always-record option must override a previously disabled state.");
    var launch = AppLaunchOptions.Parse(["--background"], @"D:\Programs\Rewind");
    if (!launch.Background || launch.DataDirectory != @"D:\Programs\Rewind\Data" || launch.Manifest != @"D:\Programs\Rewind\Data\live-clips.json")
        throw new InvalidOperationException("Autostart must use the installed application's persistent data directory.");
    string appDirectory = Path.GetDirectoryName(startupPath)!;
    string dataDirectory = Path.Combine(appDirectory, "Данные Rewind");
    string replayDirectory = Path.Combine(appDirectory, "Мои повторы");
    File.WriteAllLines(Path.Combine(appDirectory, "install-paths.txt"), [dataDirectory, replayDirectory], System.Text.Encoding.Unicode);
    var installedLaunch = AppLaunchOptions.Parse([], appDirectory);
    if (installedLaunch.DataDirectory != dataDirectory || installedLaunch.ReplayDirectory != replayDirectory ||
        installedLaunch.Manifest != Path.Combine(dataDirectory, "live-clips.json"))
        throw new InvalidOperationException("Rewind must honor the data folder chosen during installation.");
    Console.WriteLine("PASS startup_restores_recording_state_and_always_record_option_overrides_it");
}
finally { if (File.Exists(startupPath)) Directory.Delete(Path.GetDirectoryName(startupPath)!, true); }

if (ReplayNotificationText.SavedDetail(300, 1048576, ["first.mp4"]) != "5:00 · 1,0 МБ · 1 экран\nfirst.mp4" ||
    ReplayNotificationText.SavedDetail(300, 3145728, ["first.mp4", "second.mp4", "third.mp4"]) !=
        "5:00 · 3,0 МБ · 3 экрана\nfirst.mp4\nsecond.mp4\nthird.mp4")
    throw new InvalidOperationException("The same save toast must show parameters first, then one filename per line.");
Console.WriteLine("PASS_one_save_toast_formats_parameters_then_one_filename_per_line");

foreach (var (count, expected) in new (int, string)[]
{
    (0, "0 экранов"), (1, "1 экран"), (2, "2 экрана"), (3, "3 экрана"), (4, "4 экрана"),
    (5, "5 экранов"), (6, "6 экранов"), (7, "7 экранов"), (8, "8 экранов"), (9, "9 экранов"),
    (10, "10 экранов"), (11, "11 экранов"), (12, "12 экранов"), (13, "13 экранов"), (14, "14 экранов"), (15, "15 экранов"),
    (20, "20 экранов"), (21, "21 экран"), (22, "22 экрана"), (23, "23 экрана"), (24, "24 экрана"), (25, "25 экранов"),
    (101, "101 экран"), (102, "102 экрана"), (104, "104 экрана"), (105, "105 экранов"),
    (111, "111 экранов"), (112, "112 экранов"), (114, "114 экранов"), (115, "115 экранов"),
    (121, "121 экран"), (122, "122 экрана"), (1000, "1000 экранов"), (1001, "1001 экран"), (1011, "1011 экранов")
})
{
    if (ReplayNotificationText.ScreenCount(count) != expected)
        throw new InvalidOperationException("Screen count must use the Russian form: " + expected);
}
Console.WriteLine("PASS notification_screen_counts_use_Russian_forms_including_teens_and_hundreds");

var capturePress = new HeldKeyPressTracker(new HotkeyChord(0, 0x79, 0x53));
if (capturePress.Update(0x79, true, 0, heldDown: false, windowsDown: false))
    throw new InvalidOperationException("F10 alone must not toggle recording.");
capturePress.Update(0x79, false, 0, false, false);
if (!capturePress.Update(0x79, true, 0, true, false) || capturePress.Update(0x79, true, 0, true, false) ||
    capturePress.Update(0x53, true, 0, true, false))
    throw new InvalidOperationException("S + F10 must toggle once per F10 press, never on auto-repeat or S alone.");
capturePress.Update(0x79, false, 0, true, false);
if (capturePress.Update(0x79, true, 4, true, false)) throw new InvalidOperationException("Shift + S + F10 must not match S + F10.");
capturePress.Update(0x79, false, 0, true, false);
if (capturePress.Update(0x79, true, 0, true, true)) throw new InvalidOperationException("Win + S + F10 must not match S + F10.");
capturePress.Update(0x79, false, 0, true, false);
if (!capturePress.Update(0x79, true, 0, true, false)) throw new InvalidOperationException("Releasing F10 must rearm S + F10.");
Console.WriteLine("PASS recording_chord_requires_S_and_F10_without_modifiers_or_auto_repeat");

var packetTimeline = new AudioPacketTimeline(0);
if (packetTimeline.Position(1000, 100000, 240, false) != 480 ||
    packetTimeline.Position(1240, 150104, 240, false) != 720 ||
    packetTimeline.Position(1480, 199896, 240, false) != 960)
    throw new InvalidOperationException("Adjacent device packets must remain contiguous despite sub-frame QPC timestamp jitter.");
Console.WriteLine("PASS audio_packet_timestamps_do_not_create_gaps_or_overlap_from_qpc_jitter");
var emptyPacketTimeline = new AudioPacketTimeline(0);
emptyPacketTimeline.Position(1000, 100000, 240, false);
if (emptyPacketTimeline.Position(0, 0, 0, false) != 720 || emptyPacketTimeline.Position(1240, 150104, 240, false) != 720)
    throw new InvalidOperationException("An empty driver callback must not reset the packet timeline or invent lost audio.");
Console.WriteLine("PASS empty_audio_callback_does_not_reset_the_source_clock");
var changedDeviceClock = new AudioPacketTimeline(0);
changedDeviceClock.Position(0, 100000, 480, false);
if (changedDeviceClock.Position(480, 800000, 480, true) != 3840)
    throw new InvalidOperationException("A changed device clock must return to the shared QPC timeline instead of leaving all later packets late.");
Console.WriteLine("PASS device_clock_jump_recovers_to_the_shared_audio_timeline");
var jitteredClock = new AudioPacketTimeline(0);
jitteredClock.Position(0, 100000, 480, false);
if (jitteredClock.Position(480, 300000, 480, false) != 960 || jitteredClock.Position(960, 300000, 480, false) != 1440)
    throw new InvalidOperationException("A single 10ms QPC outlier must not overlap adjacent microphone packets.");
Console.WriteLine("PASS isolated_timestamp_outlier_does_not_invent_audio_loss");
var driftingClock = new AudioPacketTimeline(0);
driftingClock.Position(0, 100000, 480, false);
long resynchronized = 0;
for (int packet = 1; packet <= 8; packet++) resynchronized = driftingClock.Position(packet * 480, (packet * 10 + 70) * 10000, 480, false);
if (resynchronized != 7200 || driftingClock.Corrections != 1)
    throw new InvalidOperationException("A persistent 60ms device clock shift must recover instead of keeping all later packets late.");
Console.WriteLine("PASS sustained_device_clock_shift_resynchronizes_without_following_single_outliers");

// Потребитель может остановиться на 1,5 с, затем получить исходный звук без сдвига.
var delayedAudio = new AudioTimelineBuffer();
byte[] bufferedAudio = new byte[72000 * 8];
for (int frame = 0; frame < 72000; frame++) BitConverter.TryWriteBytes(bufferedAudio.AsSpan(frame * 8), frame + 1);
delayedAudio.Write(0, bufferedAudio);
byte[] recoveredAudio = new byte[bufferedAudio.Length];
delayedAudio.Read(0, recoveredAudio);
if (!bufferedAudio.SequenceEqual(recoveredAudio) || delayedAudio.Metrics.DroppedFrames != 0)
    throw new InvalidOperationException("A 1.5-second consumer pause must preserve every sample and its timeline.");
Console.WriteLine("PASS audio_preserves_every_sample_across_1500ms_consumer_pause");

var boundedAudio = new AudioTimelineBuffer();
byte[] threeSeconds = Enumerable.Repeat((byte)0x5a, 144000 * 8).ToArray();
boundedAudio.Write(0, threeSeconds);
if (boundedAudio.Metrics.BufferedFrames != 96000 || boundedAudio.Metrics.DroppedFrames != 48000 || boundedAudio.Metrics.OverflowEvents != 1)
    throw new InvalidOperationException("A 3-second pause must retain 2 seconds and count the missing final second within a fixed memory limit.");
byte[] withGap = new byte[threeSeconds.Length];
boundedAudio.Read(0, withGap);
byte[] afterGap = [1, 2, 3, 4, 5, 6, 7, 8];
boundedAudio.Write(144000, afterGap);
byte[] nextFrame = new byte[8];
boundedAudio.Read(144000, nextFrame);
if (withGap.AsSpan(0, 96000 * 8).ContainsAnyExcept((byte)0x5a) || withGap.AsSpan(96000 * 8).ContainsAnyExcept((byte)0) ||
    !nextFrame.SequenceEqual(afterGap) || boundedAudio.Metrics.MissingFrames != 48000)
    throw new InvalidOperationException("Overflow must leave silence at its original timeline position and retain the next sample's position.");
Console.WriteLine("PASS audio_overflow_is_bounded_counted_and_does_not_shift_later_audio");

var silentAudio = new AudioTimelineBuffer(480);
silentAudio.Write(0, Enumerable.Repeat((byte)255, 480 * 8).ToArray(), silent: true);
byte[] silentPacket = new byte[480 * 8];
silentAudio.Read(0, silentPacket);
silentAudio.Write(480, Enumerable.Repeat((byte)17, 480 * 8).ToArray());
silentAudio.Read(480, silentPacket);
if (silentPacket.Any(value => value != 17) || silentAudio.Metrics.DroppedFrames != 0 || silentAudio.Metrics.BufferedFrames != 0)
    throw new InvalidOperationException("The ring must reuse its storage without stale samples or invented losses.");
Console.WriteLine("PASS audio_timeline_ring_reuses_storage_without_stale_samples");

var audioDiagnostics = new AudioCaptureDiagnostics(new AudioTransportMetrics(144000, 300, 0, 1500, 1, 12, 0),
    [new AudioSourceStatus("system", "test", 0, null, true, Buffer: boundedAudio.Metrics)]);
string diagnosticText = audioDiagnostics.Describe();
if (!diagnosticText.Contains("сейчас 0 мс", StringComparison.Ordinal) || !diagnosticText.Contains("максимум 1500 мс", StringComparison.Ordinal) ||
    !diagnosticText.Contains("потери 1000 мс", StringComparison.Ordinal) || !diagnosticText.Contains("за текущий сеанс", StringComparison.Ordinal))
    throw new InvalidOperationException("Diagnostics must distinguish current delay, session maximum and the actual duration of lost audio.");
Console.WriteLine("PASS audio_diagnostics_distinguish_current_delay_session_maximum_and_losses");

var meter = new AudioLevelEnvelope();
meter.Observe(0.1, TimeSpan.Zero);
meter.Observe(0, TimeSpan.FromMilliseconds(20));
if (meter.Read(TimeSpan.FromMilliseconds(40)) < 0.099 ||
    AudioLevelEnvelope.Fill(0.1) < 0.65 || AudioLevelEnvelope.Fill(0) != 0 ||
    meter.Read(TimeSpan.FromSeconds(1)) >= 0.02 || meter.Read(TimeSpan.FromSeconds(3)) > 0.001)
    throw new InvalidOperationException("Meters must retain short peaks, make -20 dBFS visible, decay in silence and return to zero.");
Console.WriteLine("PASS audio_meter_retains_short_peaks_uses_decibels_and_decays");

string hotkeyFolder = Path.Combine(Path.GetTempPath(), "rewind-hotkeys-" + Guid.NewGuid().ToString("N"));
try
{
    var hotkeyFile = new HotkeySettingsFile(Path.Combine(hotkeyFolder, "hotkeys.json"));
    Directory.CreateDirectory(hotkeyFolder);
    File.WriteAllText(Path.Combine(hotkeyFolder, "hotkeys.json"), """
        {"Panel":{"Modifiers":3,"Key":81},"Save":null,"SaveAll":null,
         "Player":{"Pause":{"Modifiers":0,"Key":74},"PreviousFrame":null,"NextFrame":null,"Backward":null,"Forward":null}}
        """);
    HotkeySettings migrated = hotkeyFile.Load();
    using (var migratedJson = System.Text.Json.JsonDocument.Parse(System.Text.Json.JsonSerializer.Serialize(migrated)))
    {
        var keys = migratedJson.RootElement;
        if (!keys.TryGetProperty("CaptureToggle", out var captureKey) || captureKey.GetProperty("Modifiers").GetUInt32() != 0 ||
            captureKey.GetProperty("Key").GetUInt32() != 0x79 || captureKey.GetProperty("HeldKey").GetUInt32() != 0x53 ||
            keys.GetProperty("Settings").GetProperty("Modifiers").GetUInt32() != 2 || keys.GetProperty("Settings").GetProperty("Key").GetUInt32() != 0xbc ||
            keys.GetProperty("Player").GetProperty("GoToStart").GetProperty("Key").GetUInt32() != 0x24 ||
            keys.GetProperty("Player").GetProperty("Delete").GetProperty("Key").GetUInt32() != 0x2e ||
            migrated.Panel != new HotkeyChord(3, 0x51) || migrated.Save is not null || migrated.PlayerBindings.Pause != new HotkeyChord(0, 0x4a))
            throw new InvalidOperationException("Existing hotkey files must gain S + F10, Home, Delete and Ctrl + comma without replacing custom or disabled bindings.");
    }
    File.Delete(Path.Combine(hotkeyFolder, "hotkeys.json"));
    Console.WriteLine("PASS legacy_hotkeys_gain_new_actions_without_losing_user_bindings");
    File.WriteAllText(Path.Combine(hotkeyFolder, "hotkeys.json"), """
        {"Panel":{"Modifiers":2,"Key":188},"Save":null,"SaveAll":null,
         "Player":{"Pause":{"Modifiers":0,"Key":36},"PreviousFrame":null,"NextFrame":null,"Backward":{"Modifiers":0,"Key":46},"Forward":null}}
        """);
    HotkeySettings occupied = hotkeyFile.Load();
    if (occupied.Panel != new HotkeyChord(2, 0xbc) || occupied.PlayerBindings.Pause != new HotkeyChord(0, 0x24) ||
        occupied.Settings is not null || occupied.PlayerBindings.GoToStart is not null || occupied.PlayerBindings.Delete is not null)
        throw new InvalidOperationException("New defaults must stay unassigned when their keys already belong to the user's existing actions.");
    File.Delete(Path.Combine(hotkeyFolder, "hotkeys.json"));
    Console.WriteLine("PASS new_defaults_never_replace_conflicting_legacy_bindings");
    if (hotkeyFile.Load() != HotkeySettings.Default)
        throw new InvalidOperationException("First launch must retain the NVIDIA-safe prototype shortcuts.");
    var custom = HotkeySettings.Default with { Panel = new HotkeyChord(3, 0x51), SaveAll = null };
    hotkeyFile.Save(custom);
    if (hotkeyFile.Load() != custom)
        throw new InvalidOperationException("Custom and disabled hotkeys must survive restart.");
    Console.WriteLine("PASS custom_and_disabled_hotkeys_survive_restart");
}
finally { if (Directory.Exists(hotkeyFolder)) Directory.Delete(hotkeyFolder, recursive: true); }

var mix = new AudioMix { Master = 0.5 };
mix.SetSource(1, 0.25);
Equal(0.125, mix.EffectiveGain(1));
Equal(0.5, mix.EffectiveGain(0));
Equal(0.5, mix.EffectiveGain(2));
Console.WriteLine("PASS changing_discord_gain_does_not_change_other_sources");

PanelBounds panel = PanelGeometry.ForMonitor(0, 0, 2560, 1440, 1.25, playerVisible: false);
if (panel != new PanelBounds(0, 0, 650, 1440))
    throw new InvalidOperationException($"Expected left panel 650x1440 at 125% DPI; got {panel}.");
Console.WriteLine("PASS left_panel_uses_full_monitor_height_and_dpi");

PanelBounds withPlayer = PanelGeometry.ForMonitor(0, 0, 2560, 1440, 1.25, playerVisible: true);
if (withPlayer != new PanelBounds(0, 0, 2560, 1440))
    throw new InvalidOperationException($"The player must extend the same overlay across the monitor; got {withPlayer}.");
Console.WriteLine("PASS player_extends_the_same_overlay");
if (PanelGeometry.PlayerSurfaceHeight(2040, 1440, 1) != 1240)
    throw new InvalidOperationException("Player should retain 16:9 video and use compact vertical chrome instead of filling the monitor.");
Console.WriteLine("PASS player_surface_is_vertically_compact");
if (PanelGeometry.PlayerSurfaceBounds(2560, 1440, 1) != new PanelBounds(536, 100, 2008, 1240) ||
    PanelGeometry.PlayerSurfaceBounds(2560, 1440, 1.25) != new PanelBounds(670, 126, 1870, 1189))
    throw new InvalidOperationException("Only the sidebar and compact player should cover the desktop, with DPI-correct gaps above and below.");
Console.WriteLine("PASS desktop_is_exposed_around_player_at_normal_and_scaled_dpi");

string settingsFolder = Path.Combine(Path.GetTempPath(), "rewind-settings-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(settingsFolder);
try
{
    var settingsFile = new CaptureSettingsFile(Path.Combine(settingsFolder, "settings.json"));
    var settings = new CaptureSettings("\\\\.\\DISPLAY2", 120, 120, 30, settingsFolder);
    settingsFile.Save(settings);
    if (settingsFile.Load() != settings)
        throw new InvalidOperationException("Selected monitor and replay settings must survive restart.");
    Console.WriteLine("PASS capture_settings_survive_restart");
    bool rejected = false;
    try { settingsFile.Save(settings with { ReplaySeconds = 150 }); }
    catch (ArgumentException) { rejected = true; }
    if (!rejected || settingsFile.Load() != settings)
        throw new InvalidOperationException("Invalid replay must not replace valid saved settings.");
    Console.WriteLine("PASS invalid_replay_cannot_replace_saved_settings");
    var withMarkers = settings with { HistorySeconds = 600, ReplaySeconds = 300, ReplayMarkers = [90, 300, 600], CaptureSystemAudio = true };
    settingsFile.Save(withMarkers);
    CaptureSettings restored = settingsFile.Load()!;
    if (restored.ReplaySeconds != 300 || restored.ReplayMarkers is null || !restored.ReplayMarkers.SequenceEqual(new[] { 90, 300, 600 }) || !restored.CaptureSystemAudio)
        throw new InvalidOperationException("Replay markers, default interval, and audio choice must survive restart.");
    Console.WriteLine("PASS timeline_markers_default_and_audio_choice_survive_restart");
    var withMicrophone = settings with { CaptureMicrophone = true, MicrophoneDeviceId = "{capture-headset-stable-id}" };
    settingsFile.Save(withMicrophone);
    CaptureSettings audioRestored = settingsFile.Load()!;
    if (!audioRestored.CaptureMicrophone || audioRestored.MicrophoneDeviceId != "{capture-headset-stable-id}")
        throw new InvalidOperationException("The selected microphone must survive restart without following a different device.");
    Console.WriteLine("PASS selected_microphone_survives_restart");
    settingsFile.Save(withMarkers);
    CaptureSettings sameValues = settingsFile.Load()!;
    if (!withMarkers.HasSameValues(sameValues) || withMarkers.HasSameValues(sameValues with { MonitorDeviceName = "\\\\.\\DISPLAY3" }) ||
        withMarkers.HasSameValues(sameValues with { Fps = 60 }))
        throw new InvalidOperationException("Restored settings must be applied; choosing another screen or FPS must be pending changes.");
    Console.WriteLine("PASS pending_changes_compare_values_including_restored_markers");
    var screenDisabled = settings with { CaptureVideo = false };
    settingsFile.Save(screenDisabled);
    if (settingsFile.Load()!.CaptureVideo || settings.HasSameValues(settingsFile.Load()))
        throw new InvalidOperationException("Turning off the screen must survive restart and count as a settings change.");
    Console.WriteLine("PASS screen_off_survives_restart");
    File.WriteAllText(Path.Combine(settingsFolder, "settings.json"),
        System.Text.Json.JsonSerializer.Serialize(settings).Replace(
            ",\"CaptureVideo\":true", "", StringComparison.Ordinal));
    if (!settingsFile.Load()!.CaptureVideo)
        throw new InvalidOperationException("Existing settings without a screen switch must retain enabled capture.");
    Console.WriteLine("PASS existing_settings_keep_screen_enabled");
}
finally { Directory.Delete(settingsFolder, recursive: true); }

var intervals = new ReplayIntervals(600, [90, 300, 600], 300);
var hotkeyIntervals = new ReplayIntervals(300, [30, 90, 300], 90);
var durationSelection = new ReplayDurationSelection(hotkeyIntervals);
if (durationSelection.Seconds != 90 || !durationSelection.Step(-1) || durationSelection.Seconds != 30 ||
    durationSelection.Step(-1) || !durationSelection.Step(1) || durationSelection.Seconds != 90 ||
    !durationSelection.Step(1) || durationSelection.Seconds != 300 || durationSelection.Step(1) ||
    new ReplayDurationSelection(hotkeyIntervals).Seconds != 90)
    throw new InvalidOperationException("Горячая клавиша начинает с основной отметки и движется по точкам без зацикливания и изменения настроек.");
Console.WriteLine("PASS replay_hotkey_duration_steps_through_configured_markers");

var cancelledSelection = new ReplayDurationSelection(hotkeyIntervals);
cancelledSelection.Step(-1);
cancelledSelection.Cancel();
var nextSelection = new ReplayDurationSelection(hotkeyIntervals);
if (cancelledSelection.Complete() is not null || nextSelection.Complete() != 90 || nextSelection.Complete() is not null)
    throw new InvalidOperationException("Отпускание Alt после отмены не должно сохранять клип или мешать следующему сохранению.");
Console.WriteLine("PASS replay_hotkey_cancel_blocks_save_on_alt_release_and_allows_next_save");

string holdRoot = Path.Combine(Path.GetTempPath(), "rewind-replay-hold-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(holdRoot);
try
{
    var buffer = new Rewind.DiskBuffer.DiskReplayBuffer(holdRoot, TimeSpan.FromSeconds(4));
    void CloseSegment(int second)
    {
        string file = Path.Combine(holdRoot, second + ".mkv");
        File.WriteAllText(file, "segment");
        buffer.RegisterClosedSegment(new(file, TimeSpan.FromSeconds(second), TimeSpan.FromSeconds(second + 1)));
    }
    CloseSegment(0);
    CloseSegment(1);
    using (buffer.HoldRange(TimeSpan.Zero, TimeSpan.FromSeconds(3)))
    {
        for (int second = 2; second < 10; second++) CloseSegment(second);
        using var replay = buffer.Reserve(TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(2));
        if (replay.Segments.Select(segment => Path.GetFileName(segment.Path)).SequenceEqual(["1.mkv", "2.mkv"]) is false ||
            File.Exists(Path.Combine(holdRoot, "3.mkv")))
            throw new InvalidOperationException("Выбор повтора должен удерживать закрытые и ожидаемые фрагменты только исходного интервала.");
    }
    if (File.Exists(Path.Combine(holdRoot, "1.mkv"))) throw new InvalidOperationException("Удержанная история должна очищаться после выбора.");
    CloseSegment(10);
    using var nextReplay = buffer.Reserve(TimeSpan.FromSeconds(11), TimeSpan.FromSeconds(1));
    if (Path.GetFileName(nextReplay.Segments.Single().Path) != "10.mkv")
        throw new InvalidOperationException("Освобождение удержанной истории должно оставлять свежий буфер доступным для следующего повтора.");
    Console.WriteLine("PASS replay_hotkey_holds_original_history_while_the_buffer_keeps_rolling");
}
finally { Directory.Delete(holdRoot, recursive: true); }
string shadowRoot = Path.Combine(Path.GetTempPath(), "rewind-shadowplay-" + Guid.NewGuid().ToString("N"));
try
{
    string archive = Path.Combine(shadowRoot, "NVIDIA");
    string desktop = Path.Combine(archive, "Desktop");
    string game = Path.Combine(archive, "Deadlock");
    Directory.CreateDirectory(desktop);
    Directory.CreateDirectory(game);
    string original = Path.Combine(desktop, "Yandex Browser 2025.07.09 - 22.48.31.03.DVR.mp4");
    File.WriteAllText(original, "original");
    var normal = new CaptureSettings("DISPLAY1", 30, 90, 30, Path.Combine(shadowRoot, "Rewind"), FileNameFormat: "{app}_{n}");
    var mode = normal with { ShadowPlayDirectory = archive };
    var settingsFile = new CaptureSettingsFile(Path.Combine(shadowRoot, "settings.json"));
    settingsFile.Save(mode);
    if (!mode.HasSameValues(settingsFile.Load()) ||
        normal.ResolveReplayFolder("Deadlock") != Path.Combine(normal.StorageRoot, "Clips", "Deadlock") ||
        mode.ResolveReplayFolder("Deadlock") != game ||
        mode.ResolveReplayFolder("Yandex Browser") != desktop ||
        mode.ResolveReplayFolder("Yandex") != Path.Combine(archive, "Yandex") ||
        mode.EffectiveFileNameFormat != ReplayFileName.DefaultFormat ||
        (mode with { ShadowPlayDirectory = null }).EffectiveFileNameFormat != normal.FileNameFormat ||
        File.ReadAllText(original) != "original")
        throw new InvalidOperationException("ShadowPlay must reuse game folders and Desktop routes, preserve the archive, and restore custom naming when disabled.");
    Console.WriteLine("PASS shadowplay_preserves_existing_archive_routes_and_filename_format");
}
finally { Directory.Delete(shadowRoot, true); }

var nameTime = new DateTime(2026, 10, 23, 14, 5, 9, 120);
if (ReplayFileName.Format("{app} {size} {screen}", "Game", nameTime, 90, 1, "2", sizeBytes: 1572864) != "Game 1.5 МБ 2.mp4" ||
    ReplayFileName.Format("{size}", "Game", nameTime, 90, 1, "1", sizeBytes: 1536) != "1.5 КБ.mp4" ||
    ReplayFileName.Format("{size}", "Game", nameTime, 90, 1, "1", sizeBytes: 1610612736) != "1.5 ГБ.mp4")
    throw new InvalidOperationException("Size placeholders must use each completed file's bytes and a filename-safe human-readable size.");
ReplayFileName.Validate("{app} {size}");
Console.WriteLine("PASS filename_size_placeholder_formats_the_completed_file_size");
if (ReplayFileName.Format(ReplayFileName.DefaultFormat, "Desktop", nameTime, 90, 1, "1") != "Desktop 2026.10.23 - 14.05.09.12.DVR.mp4" ||
    ReplayFileName.Format("{app}_{n:000}_{duration}_{screen}", "Deadlock", nameTime, 90, 7, "2") != "Deadlock_007_01-30_2.mp4")
    throw new InvalidOperationException("Filename constructor must preserve NVIDIA format and support application, ordinal, duration and monitor tokens.");
bool badNameRejected = false;
try { ReplayFileName.Validate("../{app}"); } catch (ArgumentException) { badNameRejected = true; }
if (!badNameRejected) throw new InvalidOperationException("Filename templates must not escape their application folder.");
Console.WriteLine("PASS filename_constructor_and_safe_path");
string namingFolder = Path.Combine(Path.GetTempPath(), "rewind-names-" + Guid.NewGuid().ToString("N"));
try
{
    string firstName = ReplayFileName.Reserve(namingFolder, ReplayFileName.DefaultFormat, "Desktop", nameTime, 90, "1", false);
    File.WriteAllText(firstName, "original");
    string secondName = ReplayFileName.Reserve(namingFolder, ReplayFileName.DefaultFormat, "Desktop", nameTime, 90, "1", false);
    if (firstName == secondName || File.ReadAllText(firstName) != "original") throw new InvalidOperationException("Colliding saves must reserve distinct names without replacing an existing clip.");
    Console.WriteLine("PASS colliding_filenames_never_replace_clips");
}
finally
{
    if (!Path.GetFullPath(namingFolder).StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Unexpected temporary folder.");
    Directory.Delete(namingFolder, true);
}
intervals.SetDefault(90);
if (intervals.DefaultSeconds != 90 || !intervals.Markers.SequenceEqual(new[] { 90, 300, 600 }))
    throw new InvalidOperationException("Choosing the default must not remove the other replay markers.");
Console.WriteLine("PASS default_marker_changes_without_losing_other_intervals");
intervals.Resize(120);
if (intervals.MaximumSeconds != 120 || !intervals.Markers.SequenceEqual(new[] { 90, 300, 600 }) || intervals.DefaultSeconds != 90)
    throw new InvalidOperationException("Shorter history must retain configured markers while using an available default.");
intervals.SetDefault(90);
intervals.Resize(600);
intervals.SetDefault(300);
intervals.Resize(120);
intervals.Resize(600);
if (intervals.DefaultSeconds != 300)
    throw new InvalidOperationException("Restoring history must restore the preferred default.");
Console.WriteLine("PASS shorter_history_preserves_markers_and_preferred_default");
intervals.Move(300, 315);
if (intervals.DefaultSeconds != 315 || !intervals.Markers.SequenceEqual(new[] { 90, 315, 600 }))
    throw new InvalidOperationException("Moving the default must retain its role without changing other markers.");
bool collision = false;
try { intervals.Move(315, 600); } catch (ArgumentException) { collision = true; }
if (!collision || intervals.DefaultSeconds != 315)
    throw new InvalidOperationException("Moving into another marker must not remove either marker.");
if (ReplayIntervals.Snap(607, 900) != 600 || ReplayIntervals.Snap(608, 900) != 615)
    throw new InvalidOperationException("Pointer adjustment must snap to fifteen-second increments.");
Console.WriteLine("PASS marker_move_preserves_default_and_snaps_to_fifteen_seconds");
var savedIntervals = new ReplayIntervals(600, [90, 300, 600], 600);
savedIntervals.Resize(120);
string intervalFile = Path.Combine(Path.GetTempPath(), "rewind-intervals-" + Guid.NewGuid().ToString("N") + ".json");
try
{
    var file = new CaptureSettingsFile(intervalFile);
    file.Save(new CaptureSettings("DISPLAY1", 60, 120, savedIntervals.DefaultSeconds, Path.GetTempPath(),
        ReplayMarkers: savedIntervals.Markers.ToArray(), PreferredReplaySeconds: savedIntervals.PreferredDefaultSeconds));
    CaptureSettings saved = file.Load()!;
    var loaded = new ReplayIntervals(saved.HistorySeconds, saved.ReplayMarkers!, saved.PreferredReplaySeconds ?? saved.ReplaySeconds);
    loaded.Resize(600);
    if (loaded.DefaultSeconds != 600 || !loaded.Markers.SequenceEqual(new[] { 90, 300, 600 }))
        throw new InvalidOperationException("Unavailable markers and preferred default must survive saving and restart.");
    Console.WriteLine("PASS hidden_markers_and_preferred_default_survive_restart");
}
finally { File.Delete(intervalFile); }

static void Equal(double expected, double actual)
{
    if (Math.Abs(expected - actual) > 0.000001)
        throw new InvalidOperationException($"Expected {expected}; got {actual}.");
}

string multiSettingsPath = Path.Combine(Path.GetTempPath(), "rewind-multi-" + Guid.NewGuid().ToString("N") + ".json");
try
{
    var profiles = new[] { new MonitorRecordingProfile("DISPLAY1", true, 120, 0), new MonitorRecordingProfile("DISPLAY2", true, 30, 720) };
    var settings = new CaptureSettings("DISPLAY1", 120, 120, 60, Path.GetTempPath(), MonitorProfiles: profiles);
    var file = new CaptureSettingsFile(multiSettingsPath); file.Save(settings);
    CaptureSettings loaded = file.Load()!;
    if (!settings.HasSameValues(loaded) || loaded.ResolveMonitorProfiles().Count(profile => profile.Enabled) != 2 ||
        loaded.ResolveMonitorProfiles()[1].OutputSize(1920, 1080) != (1280, 720))
        throw new InvalidOperationException("Independent screen profiles must survive restart and preserve aspect ratio.");
    if (settings.HasSameValues(loaded with { MonitorProfiles = [profiles[0], profiles[1] with { Fps = 60 }] }))
        throw new InvalidOperationException("Changing only the secondary profile must be a settings change.");
    Console.WriteLine("PASS simultaneous_monitor_profiles_and_independent_quality_survive_restart");
}
finally { File.Delete(multiSettingsPath); }

if (new MonitorRecordingProfile("DISPLAY1", true, 120).OutputSize(3840, 2160) != (2560, 1440) ||
    new MonitorRecordingProfile("DISPLAY1", true, 120, 1080).OutputSize(2560, 1440) != (1920, 1080))
    throw new InvalidOperationException("Native recording must be capped at 1440p while preserving lower selected resolutions.");
Console.WriteLine("PASS recording_output_is_capped_at_1440p");

if (CaptureApplication.ResolveName("javaw", "OpenJDK Platform 25.0.5", "GLFW30", "Minecraft* 26.2 - Private world") != "Minecraft")
    throw new InvalidOperationException("Minecraft replay must use the game name instead of its Java runtime.");
Console.WriteLine("PASS minecraft_replay_uses_game_name_instead_of_java_runtime");

if (CaptureApplication.ResolveName("javaw", "OpenJDK Platform 25.0.5", "GLFW30", "Minecraft Launcher - Private profile") != "OpenJDK Platform 25.0.5")
    throw new InvalidOperationException("A launcher or unrelated Java window must keep its product name.");
Console.WriteLine("PASS minecraft_launcher_is_not_mistaken_for_the_game");

foreach (var (executable, product, windowClass, title, expected) in new (string, string?, string?, string?, string)[]
{
    ("java", "Java(TM) Platform SE binary", "LWJGL", "Minecraft 1.7.10 - Private server", "Minecraft"),
    ("JAVAW", null, "GLFW30", "Minecraft", "Minecraft"),
    ("javaw", "OpenJDK Platform", "SunAwtFrame", "Minecraft 26.2 - Private document", "OpenJDK Platform"),
    ("javaw", "OpenJDK Platform", "GLFW30", "Private document", "OpenJDK Platform"),
    ("notes", "Notes", "GLFW30", "Minecraft 26.2 - Private document", "Notes"),
    ("game", "Unreal Engine", null, null, "game"),
    ("game", null, null, null, "game")
})
{
    if (CaptureApplication.ResolveName(executable, product, windowClass, title) != expected)
        throw new InvalidOperationException("Application names must recognize Minecraft versions and preserve other products without leaking window titles.");
}
Console.WriteLine("PASS game_names_preserve_other_products_and_never_include_private_window_titles");
