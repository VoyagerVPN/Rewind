using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Rewind.Preview;

internal static class StorageUiCheck
{
    public static async Task RunAsync(string diagnostics)
    {
        var icon = BitmapSource.Create(2, 2, 96, 96, PixelFormats.Bgra32, null,
            new byte[] { 20, 30, 220, 255, 20, 30, 220, 255, 20, 30, 220, 255, 255, 255, 255, 0 }, 8);
        Color iconColor = StorageBranding.DominantColor(icon);
        if (iconColor.R < 180 || iconColor.G > 80 || iconColor.B > 80)
            throw new InvalidOperationException("Цвет приложения должен определяться видимыми пикселями его значка.");
        string[] shortcuts = [@"C:\Apps\Deadlock.lnk", @"C:\Other\Deadlock Helper.lnk"];
        if (StorageBranding.FindExactShortcut("deadlock", shortcuts) != shortcuts[0] || StorageBranding.FindExactShortcut("Unknown", shortcuts) is not null)
            throw new InvalidOperationException("Значок должен находиться по точному имени приложения, без случайных совпадений.");
        string run = Path.Combine(diagnostics, "storage-check-" + Guid.NewGuid().ToString("N"));
        string root = Path.Combine(run, "recordings");
        string clips = Path.Combine(root, "Clips", "Deadlock"), buffer = Path.Combine(root, "Buffer"), cache = Path.Combine(run, "library-cache");
        Directory.CreateDirectory(clips); Directory.CreateDirectory(buffer); Directory.CreateDirectory(cache);
        File.WriteAllText(Path.Combine(run, "THE FINALS.url"), "[InternetShortcut]\r\nURL=steam://rungameid/1\r\nIconFile=C:\\Windows\\System32\\shell32.dll\r\nIconIndex=3\r\n");
        if (!StorageBranding.Load(["THE FINALS"], [run]).ContainsKey("THE FINALS"))
            throw new InvalidOperationException("Значок игры должен находиться по ярлыку Steam.");
        File.WriteAllText(Path.Combine(run, "Broken.url"), "[InternetShortcut]\r\nURL=steam://rungameid/2\r\nIconFile=Z:\\missing\\app.ico\r\nIconIndex=0\r\n");
        if (StorageBranding.Load(["Broken"], [run]).ContainsKey("Broken"))
            throw new InvalidOperationException("При недоступном значке нельзя подставлять общий значок интернет-ссылки как значок игры.");
        File.WriteAllBytes(Path.Combine(clips, "clip.mp4"), new byte[3 * 1024 * 1024]);
        File.WriteAllBytes(Path.Combine(buffer, "part.mkv"), new byte[2 * 1024 * 1024]);
        File.WriteAllBytes(Path.Combine(cache, "preview.jpg"), new byte[1024 * 1024]);
        string desktop = Path.Combine(root, "Clips", "Desktop"); Directory.CreateDirectory(desktop);
        File.WriteAllBytes(Path.Combine(desktop, "desktop.mp4"), new byte[2 * 1024 * 1024]);
        File.WriteAllBytes(Path.Combine(clips, "ignored.bin"), new byte[1024 * 1024]);
        CaptureMonitor monitor = MonitorCatalog.Read().First(item => item.Primary);
        new CaptureSettingsFile(Path.Combine(run, "capture-settings.json")).Save(new CaptureSettings(monitor.DeviceName, 120, 120, 60, root, monitor.Identity));
        new HotkeySettingsFile(Path.Combine(run, "hotkeys.json")).Save(new(null, null, null));
        File.WriteAllText(Path.Combine(run, "library-settings.json"), JsonSerializer.Serialize(new LibraryPreferences([run])));
        var window = new OverlayWindow([], run);
        var content = (FrameworkElement)window.Content; window.Content = null; content.Resources.MergedDictionaries.Add(window.Resources);
        System.Windows.Documents.TextElement.SetForeground(content, window.Foreground);
        System.Windows.Documents.TextElement.SetFontFamily(content, window.FontFamily);
        try
        {
            window.ShowSettingsSection("storage");
            if (window.FindName("StorageChart") is null) throw new InvalidOperationException("Нужна интерактивная диаграмма реального занятого места.");
            await window.RefreshStorageUsageAsync();
            content.Measure(new Size(520, 1000)); content.Arrange(new Rect(0, 0, 520, 1000)); content.UpdateLayout();
            var buckets = (Panel)window.FindName("StorageBuckets");
            if (buckets.Children.OfType<Button>().Count() != 2 || buckets.Children.OfType<Button>().Any(button => System.Windows.Automation.AutomationProperties.GetName(button) is "Буфер" or "Свободно" or "Прочее на диске"))
                throw new InvalidOperationException("Разбивка должна содержать только игры и Desktop, без прочих данных.");
            Button clipsButton = buckets.Children.OfType<Button>().Single(button => System.Windows.Automation.AutomationProperties.GetName(button) == "Deadlock");
            if (!Descendants(clipsButton).OfType<FrameworkElement>().Any(element => System.Windows.Automation.AutomationProperties.GetName(element) == "Значок Deadlock"))
                throw new InvalidOperationException("Каждое приложение должно отображаться со значком, в том числе без найденного ярлыка.");
            clipsButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            if (((TextBlock)window.FindName("StorageDetailsTotal")).Text != "3.0 МБ" && ((TextBlock)window.FindName("StorageDetailsTotal")).Text != "3,0 МБ")
                throw new InvalidOperationException("Размер клипов должен считаться по реальным файлам, без буфера и кэша.");
            if (!((Panel)window.FindName("StorageDetails")).Children.OfType<Button>().Any()) throw new InvalidOperationException("Категория должна раскрываться до папок/файлов.");
            if (((TextBlock)window.FindName("StorageDetailsTitle")).Text != "Deadlock") throw new InvalidOperationException("Игра должна раскрывать свои клипы.");
            buckets.Children.OfType<Button>().Single(button => System.Windows.Automation.AutomationProperties.GetName(button) == "Desktop").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            if (((TextBlock)window.FindName("StorageDetailsTitle")).Text != "Desktop") throw new InvalidOperationException("Desktop должен быть отдельной категорией.");
            if (((TextBlock)window.FindName("StorageUsageTotal")).Text is not ("5.0 МБ" or "5,0 МБ")) throw new InvalidOperationException("Общий объём должен включать только 5 МБ видео.");
            ((StorageDonut)window.FindName("StorageChart")).ActivateSlice(0);
            if (((TextBlock)window.FindName("StorageDetailsTitle")).Text != "Deadlock") throw new InvalidOperationException("Выбор сектора должен раскрывать соответствующую игру.");
            var reopened = new OverlayWindow([], run);
            try
            {
                Task refresh = reopened.RefreshStorageUsageAsync();
                if (((TextBlock)reopened.FindName("StorageUsageTotal")).Text is not ("5.0 МБ" or "5,0 МБ"))
                    throw new InvalidOperationException("После перезапуска сохранённый снимок должен появляться до завершения фонового обновления.");
                await refresh;
            }
            finally { reopened.Close(); }
            content.UpdateLayout();
            var image = new RenderTargetBitmap(520, 1000, 96, 96, PixelFormats.Pbgra32); image.Render(content);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
            using (var output = File.Create(Path.Combine(run, "storage.png"))) encoder.Save(output);
            ((Button)window.FindName("StorageAllButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            if (((Panel)window.FindName("StorageDetails")).Children.OfType<Button>().Count() != 2)
                throw new InvalidOperationException("Все должно показывать файлы всех игр и Desktop.");
            ((Grid)window.FindName("StorageTableHeader")).Children.OfType<Button>().Single(button => (string)button.Tag == "file").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            string firstPath = (string)((Panel)window.FindName("StorageDetails")).Children.OfType<Button>().First().Tag;
            if (Path.GetFileName(firstPath) != "clip.mp4") throw new InvalidOperationException("Таблица должна сортироваться по настоящему имени файла.");
            string dotted = Path.Combine(root, "Clips", "S.T.A.L.K.E.R"); Directory.CreateDirectory(dotted);
            string watched = Path.Combine(dotted, "watch.mp4"); File.WriteAllBytes(watched, new byte[1024 * 1024]);
            async Task WaitForTotal(string expected)
            {
                DateTime deadline = DateTime.UtcNow.AddSeconds(4);
                while (!((TextBlock)window.FindName("StorageUsageTotal")).Text.StartsWith(expected, StringComparison.Ordinal) && DateTime.UtcNow < deadline) await Task.Delay(80);
                if (!((TextBlock)window.FindName("StorageUsageTotal")).Text.StartsWith(expected, StringComparison.Ordinal)) throw new InvalidOperationException("Изменение файлов должно обновлять снимок без ручного обновления: " + expected);
            }
            await WaitForTotal("6");
            await Task.Delay(1200);
            File.Move(watched, Path.ChangeExtension(watched, ".bak"));
            await WaitForTotal("5");
            File.Move(Path.Combine(clips, "clip.mp4"), Path.Combine(clips, "renamed.mp4"));
            File.Delete(Path.Combine(desktop, "desktop.mp4"));
            File.WriteAllBytes(Path.Combine(clips, "large.mp4"), new byte[4 * 1024 * 1024]);
            await window.RefreshStorageUsageAsync(true);
            if (((TextBlock)window.FindName("StorageUsageTotal")).Text is not ("7.0 МБ" or "7,0 МБ"))
                throw new InvalidOperationException("Сверка должна учитывать добавление, переименование и удаление файлов.");
            string offline = root + "-offline";
            if (!Path.GetFullPath(root).StartsWith(Path.GetFullPath(run) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Тестовый каталог вышел за собственную область.");
            window.Close();
            Directory.Move(root, offline);
            File.WriteAllText(Path.Combine(run, "library-settings.json"), JsonSerializer.Serialize(new LibraryPreferences([])));
            var disconnected = new OverlayWindow([], run);
            try
            {
                await disconnected.RefreshStorageUsageAsync(true);
                if (((TextBlock)disconnected.FindName("StorageUsageTotal")).Text is not ("7.0 МБ" or "7,0 МБ")) throw new InvalidOperationException("Недоступный корень не должен обнулять сохранённый каталог.");
            }
            finally { disconnected.Close(); Directory.Move(offline, root); }
            string readonlyCache = Path.Combine(run, "readonly-catalog");
            using (var catalogTelemetry = new UiTelemetry(System.Windows.Threading.Dispatcher.CurrentDispatcher, Path.Combine(run, "readonly-catalog-events"), () => false))
            using (var catalog = new ReplayLibraryIndex(readonlyCache, catalogTelemetry))
            {
                string[] catalogRoots = [Path.Combine(root, "Clips")];
                LibraryEntry[] before = await catalog.ScanAsync(catalogRoots, [], CancellationToken.None, true);
                string catalogFile = Path.Combine(readonlyCache, "catalog-v1.json");
                FileAttributes attributes = File.GetAttributes(catalogFile);
                File.SetAttributes(catalogFile, attributes | FileAttributes.ReadOnly);
                try
                {
                    File.WriteAllBytes(Path.Combine(clips, "new-with-readonly-cache.mp4"), new byte[1024]);
                    LibraryEntry[] after = await catalog.ScanAsync(catalogRoots, [], CancellationToken.None, true);
                    if (after.Length != before.Length + 1)
                        throw new InvalidOperationException("Read-only cache must not prevent the catalog from discovering a new replay.");
                }
                finally { File.SetAttributes(catalogFile, attributes); }
                LibraryEntry[] recovered = await catalog.ScanAsync(catalogRoots, [], CancellationToken.None, true);
                using var recoveredCatalog = new ReplayLibraryIndex(readonlyCache, catalogTelemetry);
                if (recoveredCatalog.ReadSnapshot(catalogRoots, []).Length != recovered.Length)
                    throw new InvalidOperationException("Catalog persistence must recover after the read-only attribute is removed.");
            }
            File.WriteAllText(Path.Combine(diagnostics, "storage-check.json"), JsonSerializer.Serialize(new { run, passed = new[] { "video-file-sizes", "games-only", "non-video-files-excluded", "game-clips", "desktop-category", "snapshot-reopen", "all-files", "file-sort", "watcher-add-and-rename", "reconcile-add-delete-rename", "unavailable-root-retains-snapshot", "overlapping-roots", "read-only-cache-keeps-live-catalog", "cache-write-recovers" } }));
        }
        finally { window.Close(); }
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, index);
            yield return child;
            foreach (DependencyObject nested in Descendants(child)) yield return nested;
        }
    }
}
