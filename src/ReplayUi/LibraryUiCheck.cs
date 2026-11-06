using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Rewind.Preview;

internal static class LibraryUiCheck
{
    public static async Task RunAsync(string diagnostics)
    {
        string folder = Path.Combine(diagnostics, "library-check-" + Guid.NewGuid().ToString("N"));
        string archive = Path.Combine(folder, "archive");
        string firstGame = Path.Combine(archive, "Arc Raiders"), secondGame = Path.Combine(archive, "Deadlock");
        Directory.CreateDirectory(firstGame); Directory.CreateDirectory(secondGame);
        DateTime today = DateTime.Now.Date;
        string fixture = Path.GetFullPath(Path.Combine(diagnostics, "..", "filmora-qa", "three_audio.mp4"));
        string thumbnail = Path.Combine(diagnostics, "media", "audio", "thumbnail.jpg");
        await CheckPlaybackPreparationAsync(fixture, folder);
        await CheckPlaybackPreparationAsync(fixture, folder, metadataKnown: false);
        for (int index = 0; index < 30; index++)
        {
            string file = Path.Combine(firstGame, $"moment-{index:00}.mp4");
            File.Copy(fixture, file);
            File.SetLastWriteTimeUtc(file, today.AddDays(index < 4 ? 0 : index < 8 ? -1 : -2).AddHours(1).AddMinutes(index).ToUniversalTime());
            var saved = new SavedReplay(file, thumbnail, 8, 640, 360, 30, 8, 8, 0, new FileInfo(file).Length, true,
                ["system", "discord", "microphone"]);
            File.WriteAllText(Path.ChangeExtension(file, ".json"), JsonSerializer.Serialize(saved));
        }
        string raw = Path.Combine(secondGame, "raw-moment.mp4"); File.Copy(fixture, raw);
        File.SetLastWriteTimeUtc(raw, today.AddHours(3).ToUniversalTime());
        string hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(raw)));
        string troubled = Path.Combine(firstGame, "moment-00.mp4");
        var health = new PlaybackHealthStore(Path.Combine(folder, "library-cache", "playback-health"));
        await health.SaveAsync(await health.ReadAsync(troubled, CancellationToken.None), [0], 2);
        string[] screens = [Path.Combine(secondGame, "screen-a.mp4"), Path.Combine(secondGame, "screen-b.mp4")];
        foreach (string file in screens)
        {
            File.Copy(fixture, file);
            File.SetLastWriteTimeUtc(file, today.AddHours(2).ToUniversalTime());
            var saved = new SavedReplay(file, thumbnail, 8, 640, 360, 30, 8, 8, 0, new FileInfo(file).Length, true,
                ["system", "discord", "microphone"], EventId: "two-screens", RelatedFiles: screens);
            File.WriteAllText(Path.ChangeExtension(file, ".json"), JsonSerializer.Serialize(saved));
        }
        await health.SaveAsync(await health.ReadAsync(screens[1], CancellationToken.None), [0], 2);
        new HotkeySettingsFile(Path.Combine(folder, "hotkeys.json")).Save(new(null, null, null));
        CaptureMonitor primary = MonitorCatalog.Read().First(monitor => monitor.Primary);
        new CaptureSettingsFile(Path.Combine(folder, "capture-settings.json")).Save(new CaptureSettings(primary.DeviceName, 30, 120, 60, folder,
            primary.Identity, CaptureSystemAudio: false, CaptureMicrophone: false, SeparateDiscord: false));
        File.WriteAllText(Path.Combine(folder, "library-settings.json"), "{\"Folders\":[],\"Sort\":3}");
        var window = new OverlayWindow([], folder);
        var root = (FrameworkElement)window.Content; window.Content = null;
        root.Resources.MergedDictionaries.Add(window.Resources);
        System.Windows.Documents.TextElement.SetForeground(root, window.Foreground);
        System.Windows.Documents.TextElement.SetFontFamily(root, window.FontFamily);
        System.Windows.Documents.TextElement.SetFontSize(root, window.FontSize);
        void Layout() { root.Measure(new Size(520, 1440)); root.Arrange(new Rect(0, 0, 520, 1440)); root.UpdateLayout(); }
        void Render(string name)
        {
            Layout(); var bitmap = new RenderTargetBitmap(520, 1440, 96, 96, PixelFormats.Pbgra32); bitmap.Render(root);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var output = File.Create(Path.Combine(folder, name + ".png")); encoder.Save(output);
        }
        try
        {
            await window.AddLibraryFolderAsync(archive + Path.DirectorySeparatorChar); Layout();
            var items = (WrapPanel)window.FindName("LibraryItems");
            var count = (TextBlock)window.FindName("LibraryCount");
            if (count.Text != "32 повтора" || items.Children.OfType<Button>().Count() != 30)
                throw new InvalidOperationException("Библиотека должна прочитать весь архив и объединить файлы одного события; первая страница содержит 30 повторов: " + count.Text);
            ((ComboBox)window.FindName("LibrarySort")).SelectedIndex = 1;
            ((Button)window.FindName("SettingsButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Layout();
            string[] days = Descendants((DependencyObject)window.FindName("RecentItems")).OfType<TextBlock>()
                .Where(text => System.Windows.Automation.AutomationProperties.GetName(text) == "Группа даты").Select(text => text.Text).ToArray();
            if (!days.SequenceEqual(new[] { "Сегодня", "Вчера", today.AddDays(-2).ToString("d MMMM", System.Globalization.CultureInfo.GetCultureInfo("ru-RU")) }))
                throw new InvalidOperationException("Недавние должны быть разделены на Сегодня, Вчера и дату: " + string.Join(" | ", days));
            if (Descendants((DependencyObject)window.FindName("RecentItems")).OfType<Button>().First().ToolTip?.ToString() != raw)
                throw new InvalidOperationException("Недавние должны начинаться с последнего повтора независимо от сортировки библиотеки.");
            Button Card(string file) => Descendants((DependencyObject)window.FindName("RecentItems")).OfType<Button>().Single(card => card.ToolTip?.ToString() == file);
            DateTime healthDeadline = DateTime.UtcNow.AddSeconds(3);
            Wpf.Ui.Controls.SymbolIcon? Warning(Button card) => Descendants(card).OfType<Wpf.Ui.Controls.SymbolIcon>()
                .SingleOrDefault(icon => System.Windows.Automation.AutomationProperties.GetName(icon) == "В записи обнаружены ошибки" && icon.Visibility == Visibility.Visible);
            while (Warning(Card(troubled)) is null && DateTime.UtcNow < healthDeadline) { await Task.Delay(30); Layout(); }
            if (Warning(Card(troubled)) is not { Parent: StackPanel badge } || badge.Parent is not Border ||
                !badge.Children.OfType<TextBlock>().Any(text => System.Windows.Automation.AutomationProperties.GetName(text) == "Длительность повтора"))
                throw new InvalidOperationException("Проблемный повтор должен показывать треугольник внутри плашки длительности.");
            if (Warning(Card(raw)) is not null) throw new InvalidOperationException("Исправный повтор не должен получать предупреждение.");
            while (Warning(Card(screens[0])) is null && DateTime.UtcNow < healthDeadline) { await Task.Delay(30); Layout(); }
            if (Warning(Card(screens[0])) is null) throw new InvalidOperationException("Общая карточка экранов должна предупреждать об ошибке на втором экране.");
            Render("recent-health");
            if (window.FindName("RecentSortPicker") is not ComboBox recentSort)
                throw new InvalidOperationException("Сортировка должна быть доступна прямо в Недавних.");
            recentSort.SelectedIndex = 2; Layout();
            if (Descendants((DependencyObject)window.FindName("RecentItems")).OfType<Button>().First().ToolTip?.ToString() != Path.Combine(firstGame, "moment-00.mp4") ||
                ((ComboBox)window.FindName("LibrarySort")).SelectedIndex != 1)
                throw new InvalidOperationException("Сортировка по имени должна менять порядок внутри дней, сохраняя отдельную сортировку библиотеки.");
            await Task.Delay(400);
            var reopened = new OverlayWindow([], folder);
            try
            {
                if (((ComboBox)reopened.FindName("RecentSortPicker")).SelectedIndex != 2 || ((ComboBox)reopened.FindName("LibrarySort")).SelectedIndex != 1)
                    throw new InvalidOperationException("Отдельные сортировки главной и библиотеки должны восстанавливаться после перезапуска.");
            }
            finally { reopened.Close(); }
            recentSort.SelectedIndex = 1; Layout();
            string oldestDay = today.AddDays(-2).ToString("d MMMM", System.Globalization.CultureInfo.GetCultureInfo("ru-RU"));
            if (Descendants((DependencyObject)window.FindName("RecentItems")).OfType<TextBlock>()
                .First(text => System.Windows.Automation.AutomationProperties.GetName(text) == "Группа даты").Text != oldestDay ||
                Descendants((DependencyObject)window.FindName("RecentItems")).OfType<Button>().First().ToolTip?.ToString() != Path.Combine(firstGame, "moment-28.mp4"))
                throw new InvalidOperationException("Сначала старые должно разворачивать группы дней и порядок внутри них среди последних 12 повторов.");
            ((Button)window.FindName("ListViewButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Layout();
            ((Grid)window.FindName("HomeTableHeader")).Children.OfType<Button>().Single(button => (string)button.Tag == "title")
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Layout();
            if (recentSort.SelectedIndex != 2 || ((ComboBox)window.FindName("LibrarySort")).SelectedIndex != 1 ||
                Descendants((DependencyObject)window.FindName("RecentItems")).OfType<Button>().Count() != 12)
                throw new InvalidOperationException("Заголовок таблицы Недавних должен управлять своей сортировкой и сохранять 12 последних событий.");
            ((Button)window.FindName("GridViewButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            recentSort.SelectedIndex = 0;
            if (window.FindName("RecentGroupingPicker") is not ComboBox grouping)
                throw new InvalidOperationException("На главной должен быть выбор группировки по дате, приложению и сессии.");
            grouping.SelectedIndex = 1; Layout();
            string[] applications = Descendants((DependencyObject)window.FindName("RecentItems")).OfType<TextBlock>()
                .Where(text => System.Windows.Automation.AutomationProperties.GetName(text) == "Группа приложения").Select(text => text.Text).ToArray();
            if (!applications.SequenceEqual(new[] { "Deadlock", "Arc Raiders" }))
                throw new InvalidOperationException("Повторы одного приложения должны быть вместе, самое недавнее приложение первым: " + string.Join(" | ", applications));
            grouping.SelectedIndex = 0;
            ((ComboBox)window.FindName("LibrarySort")).SelectedIndex = 0;
            await window.OpenLibraryAsync(); Layout();
            ((Button)window.FindName("LibraryNext")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            if (items.Children.OfType<Button>().Count() != 2) throw new InvalidOperationException("Оставшиеся повторы должны быть доступны на следующей странице.");
            var games = (ComboBox)window.FindName("LibraryGames");
            games.SelectedItem = games.Items.Cast<object>().Single(choice => choice.ToString() == "Deadlock");
            if (items.Children.OfType<Button>().Count() != 2) throw new InvalidOperationException("Фильтр игры должен оставлять только её клипы и событие нескольких мониторов.");
            var search = (TextBox)window.FindName("LibrarySearch"); search.Text = "raw-moment";
            Layout();
            DateTime deadline = DateTime.UtcNow.AddSeconds(20);
            string Duration() => Descendants(items).OfType<TextBlock>().Single(label =>
                System.Windows.Automation.AutomationProperties.GetName(label) == "Длительность повтора").Text;
            while (Duration() == "…" && DateTime.UtcNow < deadline) { await Task.Delay(80); Layout(); }
            if (Duration() != "0:08") throw new InvalidOperationException("Обычный MP4 без JSON должен получить настоящую длительность через ffprobe: " + Duration());
            if (hash != Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(raw)))) throw new InvalidOperationException("Чтение библиотеки изменило исходный файл.");
            search.Text = "";
            ((Button)window.FindName("LibraryListButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Layout();
            if (window.FindName("LibraryTableHeader") is not Grid header || header.Visibility != Visibility.Visible ||
                header.Children.OfType<Button>().Count() != 3)
                throw new InvalidOperationException("Табличный вид должен иметь три сортируемых столбца.");
            Button sortTitle = header.Children.OfType<Button>().Single(button => (string)button.Tag == "title");
            string FirstTitle() => ((Grid)items.Children.OfType<Button>().First().Content).Children.OfType<StackPanel>().First().Children.OfType<TextBlock>().First().Text;
            sortTitle.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Layout();
            if (FirstTitle() != "raw-moment.mp4") throw new InvalidOperationException("Заголовок должен сортировать строки по имени файла.");
            sortTitle.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Layout();
            if (FirstTitle() != "screen-a.mp4") throw new InvalidOperationException("Повторное нажатие должно менять направление сортировки.");
            if (items.Children.OfType<Button>().Any(card => card.Width < 400)) throw new InvalidOperationException("Список должен занимать ширину панели.");
            Render("library-list");
            var zoom = (Slider)window.FindName("LibraryZoom"); zoom.Value = .05; Layout();
            double small = items.Children.OfType<Button>().First().Width;
            zoom.Value = .95; Layout();
            if (items.Children.OfType<Button>().First().Width <= small) throw new InvalidOperationException("Масштаб должен менять размеры настоящих превью.");
            zoom.Value = .65; Layout(); await Task.Delay(200); Render("library-grid");
            window.ShowSettingsSection("audio");
            if (((FrameworkElement)window.FindName("LibraryView")).Visibility != Visibility.Collapsed)
                throw new InvalidOperationException("Переход к настройкам должен закрывать страницу библиотеки.");
            ((Button)window.FindName("SettingsButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Render("home");
            if (window.FindName("DiagnosticsPanel") is not null || window.FindName("FooterStatus") is not null)
                throw new InvalidOperationException("В пользовательском интерфейсе не должно оставаться диагностики и нижних подсказок.");
            string relocated = Path.Combine(folder, "copied-videos"); Directory.CreateDirectory(relocated);
            string comment = "Rewind/1:" + """{"Width":640,"Height":360,"Fps":30,"AudioSources":["system","discord","microphone"],"MonitorDeviceName":"screen","EventId":"copied-pair"}""";
            foreach (string name in new[] { "renamed-a.mp4", "renamed-b.mp4" })
                await CaptureProcesses.RunAsync(CaptureProcesses.Ffmpeg,
                    ["-v", "error", "-nostdin", "-i", fixture, "-map", "0", "-c", "copy", "-metadata", "comment=" + comment, Path.Combine(relocated, name)], TimeSpan.FromSeconds(20));
            string[] hashes = new string[2];
            foreach (var (file, index) in new[] { fixture, Path.Combine(relocated, "renamed-a.mp4") }.Select((file, index) => (file, index)))
                hashes[index] = await CaptureProcesses.RunAsync(CaptureProcesses.Ffmpeg,
                    ["-v", "error", "-nostdin", "-i", file, "-map", "0", "-c", "copy", "-f", "streamhash", "-hash", "sha256", "-"], TimeSpan.FromSeconds(20));
            if (hashes[0] != hashes[1]) throw new InvalidOperationException("Запись метадаты не должна менять закодированные видео и три звуковые дорожки.");
            await window.AddLibraryFolderAsync(relocated);
            games.SelectedIndex = 0;
            var locations = (ComboBox)window.FindName("LibraryFolders");
            locations.SelectedItem = locations.Items.Cast<object>().Single(choice => choice.ToString() == relocated);
            Layout();
            DateTime groupedDeadline = DateTime.UtcNow.AddSeconds(20);
            while (items.Children.OfType<Button>().Count() != 1 && DateTime.UtcNow < groupedDeadline) { await Task.Delay(100); Layout(); }
            if (items.Children.OfType<Button>().Count() != 1 ||
                !Descendants(items).OfType<TextBlock>().Any(text => text.Text == "2 экрана"))
                throw new InvalidOperationException("Скопированные и переименованные MP4 должны объединяться без JSON и JPG рядом с видео: " +
                    items.Children.OfType<Button>().Count() + " карточек; " + string.Join(" | ", Descendants(items).OfType<TextBlock>().Select(text => text.Text)));
            if (Directory.EnumerateFiles(relocated).Any(file => Path.GetExtension(file) != ".mp4"))
                throw new InvalidOperationException("Библиотека не должна создавать служебные файлы в подключённой папке.");
            await SessionGroupingCheck.RunAsync(diagnostics);
            await CheckCacheRecoveryAsync(fixture, folder);
            await File.WriteAllTextAsync(Path.Combine(diagnostics, "library-check.json"), JsonSerializer.Serialize(new
            { completedUtc = DateTime.UtcNow, folder, archiveFiles = 33, events = 32, pageSize = 30, passed = new[]
                { "scan-all", "multi-monitor-group", "pagination", "game-folder-filter", "search", "raw-mp4-duration", "source-unchanged", "list", "zoom", "navigation", "internal-diagnostics-hidden", "copied-mp4-metadata", "no-sidecars", "media-streams-unchanged",
                    "recent-calendar-groups", "recent-newest-default", "recent-sort-independent", "recent-oldest-groups", "recent-sort-reload", "recent-table-sort", "legacy-sort-migration", "application-grouping", "process-session-groups", "session-midnight", "session-long-pause", "legacy-session-estimate", "grouping-reload", "playback-error-duration-badge", "healthy-card-no-warning", "secondary-screen-warning", "corrupt-preview-cache-recovers", "read-only-preview-cache-keeps-playback", "unavailable-preview-cache-keeps-playback", "missing-or-empty-thumbnail-regenerated", "playback-independent-of-preview-queue", "uncached-playback-independent-of-preview-queue" } }, new JsonSerializerOptions { WriteIndented = true }));
        }
        finally { window.Close(); }
    }

    private static async Task CheckPlaybackPreparationAsync(string fixture, string folder, bool metadataKnown = true)
    {
        string cache = Path.Combine(folder, metadataKnown ? "playback-priority-cache" : "playback-priority-cache-unknown");
        string archive = Path.Combine(folder, metadataKnown ? "playback-priority-videos" : "playback-priority-videos-unknown"); Directory.CreateDirectory(archive);
        using var telemetry = new UiTelemetry(System.Windows.Threading.Dispatcher.CurrentDispatcher, Path.Combine(folder, "playback-priority-events"), () => false);
        using var index = new ReplayLibraryIndex(cache, telemetry);
        using var cancellation = new CancellationTokenSource();
        var clips = new List<Clip>();
        for (int number = 0; number < 16; number++)
        {
            string video = Path.Combine(archive, "preview-" + number + ".mp4"); File.Copy(fixture, video);
            clips.Add(new Clip("Preview", "", video, "", [null, null, null], 8, 30, EmbeddedAudioSources: ["system", "discord", "microphone"]));
        }
        // Реальные холодные превью занимают очередь. Подготовленный повтор должен открываться независимо от неё.
        Task<Clip>[] thumbnails = clips.Select(clip => index.PrepareAsync(clip, cancellation.Token)).ToArray();
        Clip selected = clips[^1] with { Title = "Playback", Duration = metadataKnown ? 8 : 0, EmbeddedAudioSources = metadataKnown ? clips[^1].EmbeddedAudioSources : null };
        var timer = System.Diagnostics.Stopwatch.StartNew();
        Task<Clip> playback = index.PreparePlaybackAsync(selected, cancellation.Token);
        bool prompt = false;
        try
        {
            prompt = await Task.WhenAny(playback, Task.Delay(750)) == playback;
            if (prompt)
            {
                Clip ready = await playback;
                if (ready.Duration != 8 || ready.Fps != 30 || ready.EmbeddedAudioSources?.Length != 3 || ready.Video != selected.Video || ready.Thumbnail != selected.Thumbnail)
                    throw new InvalidOperationException("Открытие должно сохранять уже известную метадату повтора.");
            }
            await File.WriteAllTextAsync(Path.Combine(folder, metadataKnown ? "playback-priority.json" : "playback-priority-unknown.json"), JsonSerializer.Serialize(new
            { prompt, milliseconds = timer.Elapsed.TotalMilliseconds, queuedPreviews = thumbnails.Length, metadataKnown }));
            if (!prompt) throw new InvalidOperationException("Открытие повтора ждёт очередь миниатюр более750мс; метадата известна=" + metadataKnown);
        }
        finally
        {
            cancellation.Cancel();
            foreach (Task task in thumbnails.Append(playback))
                try { await task; } catch (OperationCanceledException) { }
        }
    }

    private static async Task CheckCacheRecoveryAsync(string fixture, string folder)
    {
        string cache = Path.Combine(folder, "cache-recovery");
        using var telemetry = new UiTelemetry(System.Windows.Threading.Dispatcher.CurrentDispatcher, Path.Combine(folder, "cache-recovery-events"), () => false);
        var clip = new Clip("Cache recovery", "", fixture, "", [null, null, null], 0, 30);
        using (var catalog = new ReplayLibraryIndex(cache, telemetry))
        {
            Clip prepared = await catalog.PrepareAsync(clip, CancellationToken.None);
            if (Math.Abs(prepared.Duration - 8) > .05) throw new InvalidOperationException("Cache fixture must initially prepare its real duration.");
        }
        string metadata = Directory.EnumerateFiles(cache, "*.json").Single(file => Path.GetFileName(file) != "catalog-v1.json");
        foreach (string damaged in new[] { "{", "{}", "{\"Duration\":8,\"Fps\":0}" })
        {
            File.WriteAllText(metadata, damaged);
            using var reopened = new ReplayLibraryIndex(cache, telemetry);
            Clip recovered = await reopened.PrepareAsync(clip, CancellationToken.None);
            if (Math.Abs(recovered.Duration - 8) > .05 || recovered.Fps <= 0 || recovered.EmbeddedAudioSources?.Length != 3)
                throw new InvalidOperationException("Damaged or incomplete derived metadata must be rebuilt from the intact replay.");
        }
        File.WriteAllText(metadata, "{");
        FileAttributes originalAttributes = File.GetAttributes(metadata);
        File.SetAttributes(metadata, originalAttributes | FileAttributes.ReadOnly);
        try
        {
            using var readOnly = new ReplayLibraryIndex(cache, telemetry);
            Clip ready = await readOnly.PrepareAsync(clip, CancellationToken.None);
            if (Math.Abs(ready.Duration - 8) > .05 || ready.EmbeddedAudioSources?.Length != 3)
                throw new InvalidOperationException("An unwritable preview cache must not prevent playback of the intact replay.");
        }
        finally { File.SetAttributes(metadata, originalAttributes); }
        Clip preparedForThumbnail;
        using (var writable = new ReplayLibraryIndex(cache, telemetry))
            preparedForThumbnail = await writable.PrepareAsync(clip, CancellationToken.None);
        foreach (bool empty in new[] { false, true })
        {
            if (empty) File.WriteAllBytes(preparedForThumbnail.Thumbnail, []);
            else File.Delete(preparedForThumbnail.Thumbnail);
            using var missingThumbnail = new ReplayLibraryIndex(cache, telemetry);
            Clip rebuilt = await missingThumbnail.PrepareAsync(preparedForThumbnail, CancellationToken.None);
            if (!File.Exists(rebuilt.Thumbnail) || new FileInfo(rebuilt.Thumbnail).Length == 0)
                throw new InvalidOperationException("Missing or empty derived thumbnail must be regenerated for an intact replay.");
            _ = BitmapFrame.Create(new Uri(rebuilt.Thumbnail), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            preparedForThumbnail = rebuilt;
        }
        string blockedCache = Path.Combine(folder, "cache-path-is-a-file");
        File.WriteAllText(blockedCache, "cache location blocked");
        using var noCache = new ReplayLibraryIndex(blockedCache, telemetry);
        Clip uncached = await noCache.PrepareAsync(clip, CancellationToken.None);
        if (Math.Abs(uncached.Duration - 8) > .05 || uncached.EmbeddedAudioSources?.Length != 3)
            throw new InvalidOperationException("An unavailable cache directory must not prevent preparation of the intact replay.");
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        { DependencyObject child = VisualTreeHelper.GetChild(parent, index); yield return child; foreach (DependencyObject next in Descendants(child)) yield return next; }
    }
}
