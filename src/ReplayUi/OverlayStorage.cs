using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Rewind.Preview;
public partial class OverlayWindow
{
    private sealed record UsageNode(string Name, string? Path, long Bytes, long Files, UsageNode[] Children, DateTime Date = default, string Game = "");
    private sealed record UsageBucket(string Name, UsageNode Node, Color Color);
    private sealed record UsageSnapshot(long Total, int Events, UsageBucket[] Buckets);
    private UsageSnapshot? _usageSnapshot;
    private sealed record UsageEntryKey(string File, string Game, long Bytes, DateTime Date);
    private UsageEntryKey[]? _usageEntries;
    private CancellationTokenSource? _usageCancellation;
    private string? _usageGame;
    private string _usageSort = "size";
    private bool _usageAscending;
    private readonly Dictionary<string, (BitmapSource Icon, Color Color)> _storageBranding = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _storageBrandingRequested = new(StringComparer.OrdinalIgnoreCase);
    private async void OnRefreshStorageUsage(object sender, RoutedEventArgs args) => await RefreshStorageUsageAsync(true);

    public async Task RefreshStorageUsageAsync(bool force = false)
    {
        _usageCancellation?.Cancel(); using var cancellation = new CancellationTokenSource(); _usageCancellation = cancellation;
        ApplyStorageSnapshot([]);
        StorageUsageMessage.Text = "Обновление…"; StorageCatalogProgress.Visibility = Visibility.Visible;
        try
        {
            await _libraryIndex.ScanAsync(CatalogRoots(), _clips.ToArray(), cancellation.Token, force);
            if (_closing || cancellation.IsCancellationRequested) return;
            ApplyStorageSnapshot([]); StorageUsageMessage.Text = _libraryIndex.Warning ?? (_usageSnapshot?.Events == 0 ? "Клипы не найдены" : "");
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
        { StorageUsageMessage.Text = "Не удалось обновить библиотеку"; _telemetry.Event("storage-usage-error", new { error.Message }); }
        finally { if (ReferenceEquals(_usageCancellation, cancellation)) { _usageCancellation = null; StorageCatalogProgress.Visibility = Visibility.Collapsed; } }
    }

    private void ApplyStorageSnapshot(LibraryEntry[] unused)
    {
        LibraryEntry[] entries = _libraryIndex.ReadSnapshot(CatalogRoots(), [], false);
        // Предпросмотр и повторная сверка каталога не меняют состав таблицы хранения.
        UsageEntryKey[] keys = entries.OrderBy(entry => entry.Clip.Video, StringComparer.OrdinalIgnoreCase)
            .Select(entry => new UsageEntryKey(entry.Clip.Video, entry.Game, entry.Bytes, entry.CreatedUtc)).ToArray();
        if (_usageEntries is not null && _usageEntries.SequenceEqual(keys)) return;
        _usageEntries = keys;
        Color[] colors = [Color.FromRgb(177,193,166), Color.FromRgb(139,165,181), Color.FromRgb(188,169,128), Color.FromRgb(164,148,173), Color.FromRgb(166,140,131), Color.FromRgb(141,177,171)];
        UsageBucket[] buckets = entries.GroupBy(entry => entry.Game).OrderByDescending(group => group.Sum(entry => entry.Bytes)).Select((group, index) =>
        {
            UsageNode[] files = group.Select(entry => new UsageNode(Path.GetFileName(entry.Clip.Video), entry.Clip.Video, entry.Bytes, 1, [], entry.CreatedUtc, entry.Game)).ToArray();
            return new UsageBucket(group.Key, new(group.Key, null, files.Sum(file => file.Bytes), files.Length, files), _storageBranding.TryGetValue(group.Key, out var brand) ? brand.Color : colors[index % colors.Length]);
        }).ToArray();
        _usageSnapshot = new(entries.Sum(entry => entry.Bytes), entries.Length, buckets);
        StorageVolumeText.Text = "";
        StorageUsageTotal.Text = entries.Length == 0 ? "—" : FormatBytes(_usageSnapshot.Total);
        StorageUsageTotal.ToolTip = $"Файлов: {entries.Length}";
        StorageChart.Visibility = entries.Length == 0 ? Visibility.Hidden : Visibility.Visible;
        StorageChart.SetSlices(buckets.Select(bucket => new StorageSlice(bucket.Name, bucket.Node.Bytes, bucket.Color)).ToArray(), _usageSnapshot.Total, entries.Length);
        StorageBuckets.Children.Clear();
        foreach (UsageBucket bucket in buckets)
        {
            var row = new Grid(); row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(42) }); row.ColumnDefinitions.Add(new ColumnDefinition()); row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.Children.Add(StorageIcon(bucket.Name, bucket.Color, 34));
            var label = new TextBlock { Text = bucket.Name, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis }; Grid.SetColumn(label, 1); row.Children.Add(label);
            var value = new TextBlock { Text = FormatBytes(bucket.Node.Bytes), FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) }; Grid.SetColumn(value, 2); row.Children.Add(value);
            var button = new Button { Content = row, HorizontalContentAlignment = HorizontalAlignment.Stretch, Padding = new Thickness(6, 4, 6, 4) };
            System.Windows.Automation.AutomationProperties.SetName(button, bucket.Name); button.Click += (_, _) => SelectUsageBucket(bucket); StorageBuckets.Children.Add(button);
        }
        if (_usageGame is not null && !buckets.Any(bucket => bucket.Name == _usageGame)) _usageGame = null;
        ShowStorageFiles();
        LoadStorageBranding(buckets.Select(bucket => bucket.Name));
    }
    private async void LoadStorageBranding(IEnumerable<string> names)
    {
        string[] pending = names.Where(name => _storageBrandingRequested.Add(name)).ToArray();
        if (pending.Length == 0) return;
        try
        {
            var found = await Task.Run(() => StorageBranding.Load(pending));
            if (_closing || found.Count == 0) return;
            foreach (var pair in found) _storageBranding[pair.Key] = pair.Value;
            _usageEntries = null; ApplyStorageSnapshot([]);
        }
        catch (Exception error) { _telemetry.Event("storage-icon-error", new { error.Message }); }
    }
    private FrameworkElement StorageIcon(string name, Color color, double size)
    {
        var background = new Border { Width = size, Height = size, CornerRadius = new CornerRadius(7), Background = new SolidColorBrush(Color.FromArgb(55, color.R, color.G, color.B)), HorizontalAlignment = HorizontalAlignment.Left };
        System.Windows.Automation.AutomationProperties.SetName(background, "Значок " + name);
        if (_storageBranding.TryGetValue(name, out var brand)) background.Child = new Image { Source = brand.Icon, Width = size - 8, Height = size - 8, Stretch = Stretch.Uniform };
        else background.Child = new Wpf.Ui.Controls.SymbolIcon { Symbol = Wpf.Ui.Controls.SymbolRegular.Video24, FontSize = size * .48, Foreground = new SolidColorBrush(color), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        return background;
    }
    private void OnStorageChartSelected(object sender, int index)
    { if (_usageSnapshot is not null && index >= 0 && index < _usageSnapshot.Buckets.Length) SelectUsageBucket(_usageSnapshot.Buckets[index]); }
    private void SelectUsageBucket(UsageBucket bucket) { _usageGame = bucket.Name; ShowStorageFiles(); }
    private void OnStorageAll(object sender, RoutedEventArgs args) { _usageGame = null; ShowStorageFiles(); }
    private void OnStorageSort(object sender, RoutedEventArgs args)
    {
        string sort = (string)((Button)sender).Tag;
        _usageAscending = sort == _usageSort ? !_usageAscending : sort == "file";
        _usageSort = sort; ShowStorageFiles();
    }
    private void ShowStorageFiles(int visible = 100)
    {
        if (_usageSnapshot is null) return;
        UsageBucket? selected = _usageSnapshot.Buckets.FirstOrDefault(bucket => bucket.Name == _usageGame);
        StorageChart.Select(selected is null ? -1 : Array.IndexOf(_usageSnapshot.Buckets, selected));
        StorageAllButton.Background = (Brush)FindResource(selected is null ? "Hover" : "Surface");
        foreach (Button button in StorageBuckets.Children.OfType<Button>()) button.Background = (Brush)FindResource(System.Windows.Automation.AutomationProperties.GetName(button) == _usageGame ? "Hover" : "Surface");
        UsageNode[] files = selected?.Node.Children ?? _usageSnapshot.Buckets.SelectMany(bucket => bucket.Node.Children).ToArray();
        StorageDetailsTitle.Text = selected?.Name ?? "Все повторы"; StorageDetailsTotal.Text = FormatBytes(files.Sum(file => file.Bytes));
        StorageDetailsPath.Text = ""; StorageDetails.Children.Clear();
        IEnumerable<UsageNode> sorted = _usageSort switch
        {
            "file" => _usageAscending ? files.OrderBy(file => file.Name, StringComparer.CurrentCultureIgnoreCase) : files.OrderByDescending(file => file.Name, StringComparer.CurrentCultureIgnoreCase),
            "date" => _usageAscending ? files.OrderBy(file => file.Date) : files.OrderByDescending(file => file.Date),
            _ => _usageAscending ? files.OrderBy(file => file.Bytes) : files.OrderByDescending(file => file.Bytes)
        };
        foreach (Button header in StorageTableHeader.Children.OfType<Button>())
        {
            string label = (string)header.Tag switch { "file" => "Имя", "date" => "Дата", _ => "Размер" };
            header.ToolTip = "Сортировать: " + label + ((string)header.Tag == _usageSort ? _usageAscending ? " ↑" : " ↓" : "");
            System.Windows.Automation.AutomationProperties.SetName(header, header.ToolTip.ToString());
            header.Foreground = (Brush)FindResource((string)header.Tag == _usageSort ? "Ink" : "Muted");
        }
        foreach (UsageNode file in sorted.Take(visible))
        {
            var row = new Grid(); row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(58) }); row.ColumnDefinitions.Add(new ColumnDefinition());
            Color color = _usageSnapshot.Buckets.First(bucket => bucket.Name == file.Game).Color;
            FrameworkElement icon = StorageIcon(file.Game, color, 42);
            icon.VerticalAlignment = VerticalAlignment.Center; row.Children.Add(icon);
            var names = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            names.Children.Add(new TextBlock { Text = selected is null ? file.Game : file.Name, TextTrimming = TextTrimming.CharacterEllipsis, FontSize = 12 });
            names.Children.Add(new TextBlock { Text = file.Date.ToLocalTime().ToString("dd.MM HH:mm") + " · " + FormatBytes(file.Bytes), Foreground = (Brush)FindResource("Muted"), FontSize = 11, Margin = new Thickness(0, 4, 0, 0) });
            Grid.SetColumn(names, 1); row.Children.Add(names);
            var button = new Button { Content = row, Tag = file.Path, HorizontalContentAlignment = HorizontalAlignment.Stretch, Padding = new Thickness(4, 6, 4, 6), Margin = new Thickness(0, 0, 0, 4), ToolTip = file.Path };
            button.Click += async (_, _) =>
            {
                try { if (file.Path is string path) { LibraryEntry? entry = _libraryIndex.ReadSnapshot(CatalogRoots(), [], false).FirstOrDefault(entry => entry.Clip.Video == path); if (entry is not null) await OpenClipAsync(entry.Clip); } }
                catch (Exception error) { StorageUsageMessage.Text = "Не удалось открыть файл"; _telemetry.Event("storage-play-error", new { error.Message }); }
            };
            StorageDetails.Children.Add(button);
        }
        if (files.Length > visible)
        { var more = new Button { Content = $"Ещё {Math.Min(100, files.Length - visible)}" }; more.Click += (_, _) => ShowStorageFiles(visible + 100); StorageDetails.Children.Add(more); }
    }
}
