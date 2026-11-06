using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;

namespace Rewind.Preview;

internal static class UiResponsivenessCheck
{
    private sealed record Response(string Action, double HandlerMs, double LayoutMs, long AllocatedBytes);
    public static async Task RunAsync(string diagnostics)
    {
        string run = Path.Combine(diagnostics, "ui-response-" + Guid.NewGuid().ToString("N"));
        string archive = Path.Combine(run, "archive", "Clips"); Directory.CreateDirectory(archive);
        Directory.CreateDirectory(Path.Combine(run, "Clips"));
        Environment.SetEnvironmentVariable("REWIND_VIDEO_TRACE_FOLDER", run);
        string fixture = Path.Combine(diagnostics, "player-timed-fixture.mp4");
        string thumbnail = Path.Combine(diagnostics, "media", "audio", "thumbnail.jpg");
        var clips = new List<Clip>();
        for (int index = 0; index < 36; index++)
        {
            string file = Path.Combine(archive, $"moment-{index:00}.mp4"); File.Copy(fixture, file);
            clips.Add(new Clip("Момент " + index, "", file, thumbnail, [null, null, null], 24, 30,
                EmbeddedAudioSources: ["system", "discord", "microphone"]));
        }
        IReadOnlyList<CaptureMonitor> monitors = MonitorCatalog.Read();
        CaptureMonitor monitor = monitors.First(item => item.Primary);
        new CaptureSettingsFile(Path.Combine(run, "capture-settings.json")).Save(new CaptureSettings(monitor.DeviceName, 30, 30, 5, run,
            CaptureSystemAudio: false, CaptureMicrophone: false, SeparateDiscord: false,
            MonitorProfiles: monitors.Take(2).Select(item => new MonitorRecordingProfile(item.DeviceName, true, 30, 360, item.Identity)).ToArray()));
        new HotkeySettingsFile(Path.Combine(run, "hotkeys.json")).Save(new(null, null, null));
        var window = new OverlayWindow(clips.ToArray(), run);
        var root = (FrameworkElement)window.Content; window.Content = null;
        root.Resources.MergedDictionaries.Add(window.Resources);
        System.Windows.Documents.TextElement.SetForeground(root, window.Foreground);
        System.Windows.Documents.TextElement.SetFontFamily(root, window.FontFamily);
        System.Windows.Documents.TextElement.SetFontSize(root, window.FontSize);
        var host = new Window { Content = root, Width = 1600, Height = 1000, Left = -20000, Top = -20000,
            ShowActivated = false, ShowInTaskbar = false, WindowStyle = WindowStyle.None };
        var responses = new List<Response>();
        Task? closing = null; int? suspended = null;
        double closeFeedbackMs = 0, closeCleanupMs = 0, healthyCloseMs = 0;
        bool reopenedDuringCleanup = false, loadingClose = false;
        T Find<T>(string name) where T : class => (T)window.FindName(name);
        void Click(string name) => Find<Button>(name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        async Task MeasureAsync(string name, Action action)
        {
            long allocated = GC.GetAllocatedBytesForCurrentThread(); var watch = Stopwatch.StartNew();
            action(); double handler = watch.Elapsed.TotalMilliseconds;
            await root.Dispatcher.InvokeAsync(root.UpdateLayout, DispatcherPriority.Render);
            responses.Add(new(name, handler, watch.Elapsed.TotalMilliseconds, GC.GetAllocatedBytesForCurrentThread() - allocated));
        }
        async Task WaitAsync(Func<bool> condition, string message)
        {
            var watch = Stopwatch.StartNew();
            while (!condition() && watch.Elapsed.TotalSeconds < 10) await Task.Delay(20);
            if (!condition()) throw new InvalidOperationException(message);
        }
        ReplayPlayerView CurrentPlayer() => (ReplayPlayerView)Find<ContentControl>("PlayerContent").Content;
        async Task OpenAsync(Clip clip)
        {
            Task opening = window.OpenClipAsync(clip);
            ReplayPlayerView player = CurrentPlayer();
            for (int source = 0; source < 3; source++) player.SetAudioVolume(source, 0);
            await opening;
            if (player.HasPlaybackError) throw new InvalidOperationException("Тестовый повтор не открылся.");
            player.PausePlayback(); await Task.Delay(100);
        }
        try
        {
            host.Show();
            await window.AddLibraryFolderAsync(archive);
            if (Find<FrameworkElement>("LibraryView").Visibility == Visibility.Visible) Click("SettingsButton");
            await MeasureAsync("recent-menu-open", () => Click("RecentViewButton"));
            await MeasureAsync("recent-menu-close", () => Click("RecentViewButton"));
            await Task.Delay(800);
            await MeasureAsync("recent-menu-warm-open", () => Click("RecentViewButton"));
            await MeasureAsync("recent-menu-warm-close", () => Click("RecentViewButton"));
            for (int repeat = 0; repeat < 3; repeat++)
            {
                await MeasureAsync("recent-list", () => Click("ListViewButton"));
                await MeasureAsync("recent-list-again", () => Click("ListViewButton"));
                await MeasureAsync("recent-grid", () => Click("GridViewButton"));
                foreach (int grouping in new[] { 1, 2, 0 }) await MeasureAsync("group-" + grouping, () => Find<ComboBox>("RecentGroupingPicker").SelectedIndex = grouping);
                foreach (int sort in new[] { 1, 2, 0 }) await MeasureAsync("sort-" + sort, () => Find<ComboBox>("RecentSortPicker").SelectedIndex = sort);
                foreach (double zoom in new[] { 0, .5, 1, .5 }) await MeasureAsync("zoom-" + zoom, () => Find<Slider>("GalleryZoom").Value = zoom);
                await MeasureAsync("settings-open", () => Click("SettingsButton"));
                foreach (string section in new[] { "audio", "replay", "storage", "hotkeys", "screens" })
                    await MeasureAsync("tab-" + section, () => Descendants(Find<DependencyObject>("SettingsTabs")).OfType<RadioButton>().Single(button => button.Tag?.ToString() == section).IsChecked = true);
                await MeasureAsync("settings-history", () => Find<Slider>("HistorySlider").Value = repeat % 2 == 0 ? 45 : 30);
                await MeasureAsync("settings-filename", () => Find<TextBox>("FilenameFormatInput").Text = repeat % 2 == 0 ? "{app}_{n:000}" : ReplayFileName.DefaultFormat);
                await MeasureAsync("settings-close", () => Click("SettingsButton"));
            }
            await MeasureAsync("capture-start-feedback", () => Click("CaptureToggle"));
            await WaitAsync(() => Find<Button>("SaveReplayButton").IsEnabled, "Тестовая запись не подготовила доступный повтор.");
            await MeasureAsync("save-all-feedback", () => Click("SaveAllScreensButton"));
            await WaitAsync(() => Find<TextBlock>("CaptureMessageText").Text.StartsWith("Сохранено", StringComparison.Ordinal), "Тестовый повтор не сохранился.");
            string[] saved = Directory.GetFiles(Path.Combine(run, "Clips"), "*.mp4", SearchOption.AllDirectories);
            if (saved.Length != Math.Min(2, monitors.Count)) throw new InvalidOperationException("Сохранение должно включать все выбранные тестовые экраны.");
            SavedReplay[] savedMetadata = saved.Select(file => ReplayMetadata.Read(file) ?? throw new InvalidOperationException("Нет метаданных тестового повтора.")).ToArray();
            if (savedMetadata.Select(item => item.EventId).Distinct().Count() != 1 || savedMetadata.Select(item => item.RequestedEndSeconds).Distinct().Count() != 1)
                throw new InvalidOperationException("Асинхронная упаковка должна сохранять общий момент события для экранов.");
            foreach (string file in saved)
                await CaptureProcesses.RunAsync(CaptureProcesses.Ffmpeg, ["-v", "error", "-nostdin", "-xerror", "-i", file, "-map", "0:v:0", "-f", "null", "-"], TimeSpan.FromSeconds(15));
            await MeasureAsync("capture-stop-feedback", () => Click("CaptureToggle"));
            await WaitAsync(() => Find<Button>("CaptureToggle").IsEnabled && Find<TextBlock>("CaptureStatusText").Text == "Запись выключена", "Тестовая запись не остановилась.");
            await MeasureAsync("library-open-feedback", () => Click("GalleryButton"));
            await WaitAsync(() => Find<ProgressBar>("LibraryCatalogProgress").Visibility != Visibility.Visible, "Библиотека не завершила сканирование.");
            await MeasureAsync("library-search", () => Find<TextBox>("LibrarySearch").Text = "moment-0");
            await MeasureAsync("library-search-clear", () => Find<TextBox>("LibrarySearch").Text = "");
            await MeasureAsync("library-next-page", () => Click("LibraryNext"));
            await MeasureAsync("library-previous-page", () => Click("LibraryPrevious"));
            await MeasureAsync("library-list", () => Click("LibraryListButton"));
            await MeasureAsync("library-grid", () => Click("LibraryGridButton"));
            await MeasureAsync("library-close", () => Click("SettingsButton"));
            await OpenAsync(clips[0]);
            ReplayPlayerView active = CurrentPlayer();
            void PlayerClick(string name) => ((Button)active.FindName(name)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            foreach (string name in new[] { "PlayButton", "PlayButton", "SoundButton", "SoundButton", "PlayerSettingsButton", "PlayerSettingsButton", "ActionsButton", "ActionsButton" })
                await MeasureAsync("player-" + name, () => PlayerClick(name));
            await MeasureAsync("player-step-forward", () => PlayerClick("NextFrameButton"));
            await Task.Delay(300);
            await MeasureAsync("player-step-back", () => PlayerClick("PreviousFrameButton"));
            await Task.Delay(300);
            await MeasureAsync("player-skip-forward", () => PlayerClick("SeekForwardButton"));
            await Task.Delay(300);
            await MeasureAsync("player-skip-back", () => PlayerClick("SeekBackwardButton"));
            await Task.Delay(300);
            await MeasureAsync("player-start", () => PlayerClick("GoToStartButton"));
            await Task.Delay(300);
            active.PausePlayback();
            suspended = active.VideoProcessId ?? throw new InvalidOperationException("Нет тестового видеопроцесса.");
            using (Process worker = Process.GetProcessById(suspended.Value))
                if (NtSuspendProcess(worker.Handle) != 0) throw new InvalidOperationException("Не удалось приостановить тестовый видеопроцесс.");
            var closeWatch = Stopwatch.StartNew();
            closing = window.ClosePlayerExtensionAsync();
            await root.Dispatcher.InvokeAsync(root.UpdateLayout, DispatcherPriority.Render);
            closeFeedbackMs = closeWatch.Elapsed.TotalMilliseconds;
            bool hidden = Find<FrameworkElement>("PlayerHost").Visibility == Visibility.Collapsed;
            if (hidden && !closing.IsCompleted)
            {
                await OpenAsync(clips[1]);
                await closing.WaitAsync(TimeSpan.FromSeconds(5));
                reopenedDuringCleanup = Find<FrameworkElement>("PlayerHost").Visibility == Visibility.Visible && CurrentPlayer().CurrentVideo == clips[1].Video && !CurrentPlayer().HasPlaybackError;
            }
            else { await closing; if (!hidden) closeFeedbackMs = closeWatch.Elapsed.TotalMilliseconds; }
            closeCleanupMs = closeWatch.Elapsed.TotalMilliseconds;
            suspended = null;
            if (closeCleanupMs > 5000) throw new InvalidOperationException($"Watchdog должен завершать зависший видеопроцесс независимо от UI: {closeCleanupMs:F1} мс.");
            if (!hidden || closeFeedbackMs > 100) throw new InvalidOperationException($"Закрытие должно скрывать плеер до очистки зависшего процесса: feedback={closeFeedbackMs:F1} мс, hidden={hidden}.");
            if (!reopenedDuringCleanup) throw new InvalidOperationException("Следующий повтор должен открываться во время очистки старого и оставаться открытым.");
            var healthyWatch = Stopwatch.StartNew(); await window.ClosePlayerExtensionAsync(); healthyCloseMs = healthyWatch.Elapsed.TotalMilliseconds;
            if (healthyCloseMs >= 1500) throw new InvalidOperationException($"Исправный видеопроцесс должен закрываться штатно, без двухсекундного watchdog: {healthyCloseMs:F1} мс.");
            Task loading = window.OpenClipAsync(clips[2]);
            await window.ClosePlayerExtensionAsync(); await loading;
            loadingClose = Find<FrameworkElement>("PlayerHost").Visibility == Visibility.Collapsed;
            if (!loadingClose) throw new InvalidOperationException("Отмена загрузки не должна возвращать закрытый плеер.");
        }
        finally
        {
            if (suspended is int id)
                try { using Process worker = Process.GetProcessById(id); NtResumeProcess(worker.Handle); }
                catch (ArgumentException) { }
            if (closing is not null) await closing;
            await window.ClosePlayerExtensionAsync();
            await File.WriteAllTextAsync(Path.Combine(diagnostics, "ui-responsiveness.json"), JsonSerializer.Serialize(new
            { run, responses, closeFeedbackMs, closeCleanupMs, healthyCloseMs, reopenedDuringCleanup, loadingClose,
                measurement = "Routed button action to dispatcher render-priority layout on own offscreen WPF HWND; not physical input-to-photon latency" }, new JsonSerializerOptions { WriteIndented = true }));
            host.Content = null; host.Close(); window.Close();
        }
    }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        { DependencyObject child = VisualTreeHelper.GetChild(parent, index); yield return child; foreach (DependencyObject nested in Descendants(child)) yield return nested; }
    }
    [DllImport("ntdll.dll")] private static extern int NtSuspendProcess(nint process);
    [DllImport("ntdll.dll")] private static extern int NtResumeProcess(nint process);
}
