using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Interop;

namespace Rewind.Preview;

internal static class GalleryPreviewCheck
{
    public static async Task RunAsync(string diagnostics)
    {
        string folder = Path.Combine(diagnostics, "gallery-preview-" + Guid.NewGuid().ToString("N"));
        string archive = Path.Combine(folder, "Clips"); Directory.CreateDirectory(archive);
        string video = Path.Combine(archive, "ready.mp4"), pending = Path.Combine(archive, "unavailable.mp4");
        File.Copy(Path.Combine(diagnostics, "player-timed-fixture.mp4"), video);
        File.WriteAllBytes(pending, [0, 1, 2]);
        string thumbnail = Path.Combine(diagnostics, "media", "audio", "thumbnail.jpg");
        if (!File.Exists(thumbnail)) throw new FileNotFoundException("Нужно настоящее изображение превью.", thumbnail);
        new HotkeySettingsFile(Path.Combine(folder, "hotkeys.json")).Save(new(null, null, null));
        CaptureMonitor source = MonitorCatalog.Read().First(monitor => monitor.Primary);
        new CaptureSettingsFile(Path.Combine(folder, "capture-settings.json")).Save(new CaptureSettings(
            source.DeviceName, 30, 15, 15, folder, source.Identity, [15], CaptureSystemAudio: false, CaptureMicrophone: false, SeparateDiscord: false));
        // Реальный Program создаёт окно до app.Run, когда SynchronizationContext ещё может отсутствовать.
        SynchronizationContext? context = SynchronizationContext.Current;
        OverlayWindow window;
        try
        {
            SynchronizationContext.SetSynchronizationContext(null);
            window = new OverlayWindow([
                new Clip("Готовое превью", "", video, thumbnail, [null, null, null], 24, 30),
                new Clip("Недоступное превью", "", pending, "", [null, null, null], 24, 30)], folder);
        }
        finally { SynchronizationContext.SetSynchronizationContext(context); }
        var root = (FrameworkElement)window.Content;
        window.Content = null;
        root.Resources.MergedDictionaries.Add(window.Resources);
        TextElementStyle(root, window);
        void Layout()
        {
            if (window.IsVisible && ReferenceEquals(window.Content, root)) { window.UpdateLayout(); return; }
            root.Measure(new Size(520, 1100)); root.Arrange(new Rect(0, 0, 520, 1100)); root.UpdateLayout();
        }
        Button Card(string file)
        {
            bool library = ((FrameworkElement)window.FindName("LibraryView")).Visibility == Visibility.Visible;
            return Descendants((DependencyObject)window.FindName(library ? "LibraryItems" : "RecentItems")).OfType<Button>()
                .Single(button => button.ToolTip?.ToString() == file);
        }
        DependencyObject Body(string file) => (DependencyObject)Card(file).Content;
        Image Preview(string file) => Descendants(Body(file)).OfType<Image>().Single();
        void CheckPlaceholder(string file, string name)
        {
            Layout();
            var surface = Descendants(Body(file)).OfType<SixteenNineFrame>().Single();
            Point point = surface.TranslatePoint(new Point(surface.ActualWidth * .2, surface.ActualHeight * .2), root);
            var bitmap = new RenderTargetBitmap(520, Math.Max(1100, (int)Math.Ceiling(root.ActualHeight)), 96, 96, PixelFormats.Pbgra32); bitmap.Render(root);
            byte[] pixel = new byte[4];
            bitmap.CopyPixels(new Int32Rect((int)point.X, (int)point.Y, 1, 1), pixel, 4, 0);
            if (pixel.Take(3).All(channel => channel < 8))
                throw new InvalidOperationException(name + ": до загрузки превью карточка не должна выглядеть чёрным прямоугольником.");
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var output = File.Create(Path.Combine(folder, name + ".png")); encoder.Save(output);
        }
        try
        {
            CheckPlaceholder(pending, "cold-gallery");
            window.Content = root;
            var openingPreviews = new List<bool>();
            nint handle = new WindowInteropHelper(window).EnsureHandle();
            nint ObserveOpening(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
            {
                if (message is 0x0317 or 0x0318) openingPreviews.Add(Preview(video).Source is not null);
                return 0;
            }
            HwndSource.FromHwnd(handle).AddHook(ObserveOpening);
            window.ShowOverlay(); window.HideOverlay();
            await Task.Delay(150);
            if (window.IsVisible || openingPreviews.Count != 0)
                throw new InvalidOperationException("Скрытие во время подготовки превью должно отменять отложенный выезд.");
            window.ShowOverlay();
            await Task.Delay(700);
            if (SystemParameters.ClientAreaAnimation && (openingPreviews.Count == 0 || !openingPreviews[0]))
                throw new InvalidOperationException("В первом кадре системной анимации уже должно быть готовое JPEG-превью, а не пустая карточка.");
            DateTime deadline = DateTime.UtcNow.AddSeconds(8);
            while (Preview(video).Source is null && DateTime.UtcNow < deadline) { await Task.Delay(20); Layout(); }
            if (Preview(video).Source is null) throw new InvalidOperationException("Существующее превью должно загрузиться до открытия окна.");
            ((Slider)window.FindName("GalleryZoom")).Value = 1;
            Layout();
            if (Preview(video).Source is null) throw new InvalidOperationException("Изменение размера не должно сбрасывать готовое превью в пустое состояние.");
            if (Preview(video).Source is null) throw new InvalidOperationException("Первый показ должен сохранять готовое изображение.");
            deadline = DateTime.UtcNow.AddSeconds(5);
            bool Unavailable() => Descendants(Body(pending)).OfType<FrameworkElement>()
                .Any(element => System.Windows.Automation.AutomationProperties.GetName(element) == "Превью недоступно");
            while (!Unavailable() && DateTime.UtcNow < deadline) await Task.Delay(20);
            if (!Unavailable()) throw new InvalidOperationException("Повреждённый источник должен получить понятное состояние недоступного превью.");
            CheckPlaceholder(pending, "unavailable-gallery");
            await window.Dispatcher.InvokeAsync(root.UpdateLayout, System.Windows.Threading.DispatcherPriority.ContextIdle);
            Button retainedCard = Card(video);
            Image retainedImage = Preview(video);
            window.HideOverlay(); window.ShowOverlay();
            if (!ReferenceEquals(Card(video), retainedCard) || !ReferenceEquals(Preview(video), retainedImage))
                throw new InvalidOperationException("Повторный показ без изменения данных должен сохранять сами карточки и Image, а не создавать их заново.");
            if (Preview(video).Source is null) throw new InvalidOperationException("Повторное открытие не должно обнулять готовое изображение.");
            await window.OpenLibraryAsync();
            await window.Dispatcher.InvokeAsync(root.UpdateLayout, System.Windows.Threading.DispatcherPriority.ContextIdle);
            Button retainedLibraryCard = Card(video);
            window.HideOverlay(); window.ShowOverlay();
            await Task.Delay(350);
            if (!ReferenceEquals(Card(video), retainedLibraryCard))
                throw new InvalidOperationException("Повторный показ библиотеки не должен начинать её открытие заново и пересоздавать неизменившиеся карточки.");
            await File.WriteAllTextAsync(Path.Combine(diagnostics, "gallery-preview-check.json"), JsonSerializer.Serialize(new
            { completedUtc = DateTime.UtcNow, folder, coldPlaceholderNotBlack = true, missingPlaceholderNotBlack = true,
                readyInFirstNativeFrame = true, startupWithoutSynchronizationContext = true, cancelledOpeningDoesNotReappear = true,
                readyPreviewSurvivesResize = true, readyPreviewSurvivesReopen = true,
                sameHomeCardsRetained = true, sameLibraryCardsRetained = true,
                limitations = "Real JPEG and video fixture in isolated data; not every thumbnail in the user's archive." }));
        }
        finally { window.Close(); }
    }

    private static void TextElementStyle(FrameworkElement root, Window window)
    {
        System.Windows.Documents.TextElement.SetForeground(root, window.Foreground);
        System.Windows.Documents.TextElement.SetFontFamily(root, window.FontFamily);
        System.Windows.Documents.TextElement.SetFontSize(root, window.FontSize);
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(parent, index);
            yield return child;
            foreach (DependencyObject nested in Descendants(child)) yield return nested;
        }
    }
}
