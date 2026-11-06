using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Rewind.Preview;

public partial class OverlayWindow
{
    private ReplayLibraryIndex _libraryIndex = null!;
    private PlaybackHealthStore _playbackHealth = null!;
    private LibraryPreferences _libraryPreferences = new([]);
    private LibraryEntry[] _libraryEntries = [];
    private bool _libraryReady, _libraryVisible, _updatingLibrary;
    private bool _catalogApplyQueued;
    private sealed record GalleryLayoutState(bool Library, bool List, int Grouping, int Sort, double Width, DateTime Today, string Sessions);
    private sealed record GalleryItemState(string File, string Title, string Game, DateTime Date, DateTime? Modified, long Bytes,
        double Duration, double Fps, string Thumbnail, string? Event, string? Session, DateTime? SessionStart, string Screens, string Audio);
    private GalleryLayoutState? _lastGalleryLayout;
    private GalleryItemState[]? _lastGalleryItems;
    private int _libraryPage, _renderVersion;
    private CancellationTokenSource? _scanCancellation, _previewCancellation;
    private DispatcherTimer? _librarySaveTimer;
    private DispatcherTimer? _catalogRefreshTimer;
    private const int LibraryPageSize = 30;
    private bool _galleryReflowPending;
    private readonly List<(string[] Files, WeakReference<FrameworkElement> Icon)> _healthBadges = [];
    private sealed record GalleryPreview(long Bytes, DateTime? Modified, BitmapSource Image);
    private readonly Dictionary<string, GalleryPreview> _galleryPreviews = new(StringComparer.OrdinalIgnoreCase);
    private readonly Queue<string> _galleryPreviewOrder = [];
    private readonly List<Task> _galleryWarmLoads = [];
    private void MarkPlaybackIssue(string file)
    {
        _healthBadges.RemoveAll(badge => !badge.Icon.TryGetTarget(out _));
        foreach (var badge in _healthBadges)
            if (badge.Files.Contains(file, StringComparer.OrdinalIgnoreCase) && badge.Icon.TryGetTarget(out FrameworkElement? icon))
                icon.Visibility = Visibility.Visible;
    }
    private sealed record LibraryChoice(string? Key, string Label) { public override string ToString() => Label; }

    private void InitializeLibrary()
    {
        _libraryIndex = new ReplayLibraryIndex(Path.Combine(_diagnostics, "library-cache"), _telemetry);
        _playbackHealth = new PlaybackHealthStore(Path.Combine(_diagnostics, "library-cache", "playback-health"));
        string preferences = Path.Combine(_diagnostics, "library-settings.json");
        try { if (File.Exists(preferences)) _libraryPreferences = JsonSerializer.Deserialize<LibraryPreferences>(File.ReadAllText(preferences)) ?? new([]); }
        catch (Exception error) when (error is IOException or JsonException) { _telemetry.Event("library-settings-error", new { error.Message }); }
        _libraryPreferences = _libraryPreferences with { Zoom = Math.Round(Math.Clamp(_libraryPreferences.Zoom, 0, 1) * 2) / 2,
            Sort = Math.Clamp(_libraryPreferences.Sort, 0, 5), RecentSort = Math.Clamp(_libraryPreferences.RecentSort, 0, 5),
            RecentGrouping = Math.Clamp(_libraryPreferences.RecentGrouping, 0, 2) };
        GalleryZoom.Value = LibraryZoom.Value = _libraryPreferences.Zoom;
        RefreshLibraryLanguageOptions();
        _librarySaveTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(300) };
        _librarySaveTimer.Tick += (_, _) => SaveLibraryPreferences();
        _libraryReady = true;
        _catalogRefreshTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(700) };
        _catalogRefreshTimer.Tick += async (_, _) => { _catalogRefreshTimer.Stop(); if (!_closing) await RefreshCatalogAsync(); };
        _libraryIndex.Invalidated += () => Dispatcher.BeginInvoke(() => { if (!_closing) { _catalogRefreshTimer.Stop(); _catalogRefreshTimer.Start(); } });
        _libraryIndex.SnapshotChanged += () => Dispatcher.BeginInvoke(QueueCatalogSnapshot);
        _libraryIndex.Progress += progress => Dispatcher.BeginInvoke(() =>
        {
            if (_closing) return;
            LibraryCatalogProgress.Visibility = StorageCatalogProgress.Visibility = progress.Complete ? Visibility.Collapsed : Visibility.Visible;
            string status = progress.Complete ? progress.Warning ?? "" : Localization.Format("Format.CatalogProgress", progress.Files);
            LibraryMessage.Text = StorageUsageMessage.Text = status;
        });
        ApplyCatalogSnapshot();
        _catalogRefreshTimer.Start();
    }

    private void RefreshLibraryLanguageOptions()
    {
        _updatingLibrary = true;
        try
        {
            LibrarySort.ItemsSource = new[] { Localization.Text("Message.NewestFirst"), Localization.Text("Message.OldestFirst"), Localization.Text("Message.FileAZ"), Localization.Text("Message.LargestFirst"), Localization.Text("Message.FileZA"), Localization.Text("Message.SmallestFirst") };
            LibrarySort.SelectedIndex = _libraryPreferences.Sort;
            RecentSortPicker.ItemsSource = LibrarySort.ItemsSource;
            RecentSortPicker.SelectedIndex = _libraryPreferences.RecentSort;
            RecentGroupingPicker.ItemsSource = new[] { Localization.Text("Message.ByDate"), Localization.Text("Message.ByApplication"), Localization.Text("Message.BySession") };
            RecentGroupingPicker.SelectedIndex = _libraryPreferences.RecentGrouping;
        }
        finally { _updatingLibrary = false; }
    }

    private void SaveLibraryPreferences()
    {
        _librarySaveTimer?.Stop();
        try
        {
            string file = Path.Combine(_diagnostics, "library-settings.json");
            File.WriteAllText(file + ".new", JsonSerializer.Serialize(_libraryPreferences, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(file + ".new", file, true);
        }
        catch (IOException error) { _telemetry.Event("library-settings-save-error", new { error.Message }); }
    }

    private void QueueLibraryPreferences() { _librarySaveTimer?.Stop(); _librarySaveTimer?.Start(); }

    public async Task OpenLibraryAsync()
    {
        _libraryVisible = true; HomeView.Visibility = SettingsView.Visibility = Visibility.Collapsed;
        LibraryView.Visibility = Visibility.Visible; SettingsLabel.Text = Localization.Text("Message.Back"); SettingsGlyph.Symbol = Wpf.Ui.Controls.SymbolRegular.ArrowLeft24;
        _scanCancellation?.Cancel();
        using var cancellation = new CancellationTokenSource(); _scanCancellation = cancellation;
        ApplyCatalogSnapshot();
        LibraryMessage.Text = Localization.Text("Message.Refreshing"); LibraryCatalogProgress.Visibility = Visibility.Visible;
        string[] roots = CatalogRoots();
        try
        {
            LibraryEntry[] entries = await _libraryIndex.ScanAsync(roots, _clips.ToArray(), cancellation.Token);
            if (_closing || cancellation.IsCancellationRequested) return;
            _libraryEntries = entries; _libraryPage = 0;
            _updatingLibrary = true;
            string? game = (LibraryGames.SelectedItem as LibraryChoice)?.Key;
            LibraryGames.ItemsSource = new[] { new LibraryChoice(null, Localization.Text("Message.AllGamesAndFolders")) }
                .Concat(entries.Select(entry => entry.Game).Distinct().Order().Select(name => new LibraryChoice(name, name))).ToArray();
            LibraryGames.SelectedItem = LibraryGames.Items.Cast<LibraryChoice>().FirstOrDefault(choice => choice.Key == game) ?? LibraryGames.Items[0];
            LibraryFolders.ItemsSource = new[] { new LibraryChoice(null, Localization.Text("Message.AllLocations")) }
                .Concat(roots.Select(root => new LibraryChoice(root, root))).ToArray();
            LibraryFolders.SelectedIndex = 0; _updatingLibrary = false;
            string[] missing = roots.Where(root => !Directory.Exists(root)).ToArray();
            LibraryMessage.Text = missing.Length > 0 ? Localization.Text("Message.FolderUnavailable") + missing[0] : "";
            _catalogApplyQueued = false;
            RenderGallery();
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception error) { _telemetry.Event("library-open-error", new { error.Message }); LibraryMessage.Text = Localization.Text("Message.CouldNotReadTheLibrary"); }
        finally { if (ReferenceEquals(_scanCancellation, cancellation)) { _scanCancellation = null; LibraryCatalogProgress.Visibility = Visibility.Collapsed; } }
    }

    public async Task AddLibraryFolderAsync(string folder)
    {
        string full = Path.GetFullPath(folder);
        if (!Directory.Exists(full)) throw new DirectoryNotFoundException(Localization.Text("Message.FolderNotFound"));
        _libraryPreferences = _libraryPreferences with { Folders = _libraryPreferences.Folders.Append(full).Distinct(StringComparer.OrdinalIgnoreCase).ToArray() };
        SaveLibraryPreferences(); await OpenLibraryAsync();
    }

    private async void OnAddLibraryFolder(object sender, RoutedEventArgs args)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = Localization.Text("Ui.AddAReplayFolder") };
        if (dialog.ShowDialog(this) != true) return;
        try { await AddLibraryFolderAsync(dialog.FolderName); }
        catch (Exception error) { LibraryMessage.Text = error.Message; }
    }
    private async void OnRefreshLibrary(object sender, RoutedEventArgs args)
    { try { await _libraryIndex.ScanAsync(CatalogRoots(), _clips.ToArray(), CancellationToken.None, true); await OpenLibraryAsync(); } catch (Exception error) { LibraryMessage.Text = Localization.Text("Message.CouldNotRefreshTheLibrary"); _telemetry.Event("library-refresh-error", new { error.Message }); } }

    private string[] CatalogRoots()
    {
        IEnumerable<string> roots = _libraryPreferences.Folders.Append(Path.Combine(_captureSettings?.StorageRoot ?? _diagnostics, "Clips"));
        if (_captureSettings?.ShadowPlayDirectory is string archive) roots = roots.Append(archive);
        return roots.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private void ApplyCatalogSnapshot()
    {
        _catalogApplyQueued = false;
        _libraryEntries = _libraryIndex.ReadSnapshot(CatalogRoots(), _clips.ToArray());
        string? game = (LibraryGames.SelectedItem as LibraryChoice)?.Key;
        string? folder = (LibraryFolders.SelectedItem as LibraryChoice)?.Key;
        _updatingLibrary = true;
        LibraryGames.ItemsSource = new[] { new LibraryChoice(null, Localization.Text("Message.AllGames")) }.Concat(_libraryEntries.Select(entry => entry.Game).Distinct().Order().Select(name => new LibraryChoice(name, name))).ToArray();
        LibraryGames.SelectedItem = LibraryGames.Items.Cast<LibraryChoice>().FirstOrDefault(choice => choice.Key == game) ?? LibraryGames.Items[0];
        LibraryFolders.ItemsSource = new[] { new LibraryChoice(null, Localization.Text("Message.AllLocations")) }.Concat(CatalogRoots().Select(root => new LibraryChoice(root, root))).ToArray();
        LibraryFolders.SelectedItem = LibraryFolders.Items.Cast<LibraryChoice>().FirstOrDefault(choice => choice.Key == folder) ?? LibraryFolders.Items[0];
        _updatingLibrary = false;
        RenderGallery();
        if (StorageSettings.Visibility == Visibility.Visible) ApplyStorageSnapshot(_libraryEntries);
    }
    private void QueueCatalogSnapshot()
    {
        if (_closing || _catalogApplyQueued) return;
        _catalogApplyQueued = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            if (!_catalogApplyQueued || _closing) return;
            ApplyCatalogSnapshot();
        });
    }

    private async Task RefreshCatalogAsync()
    {
        try { await _libraryIndex.ScanAsync(CatalogRoots(), _clips.ToArray(), CancellationToken.None); if (!_closing) ApplyCatalogSnapshot(); }
        catch (Exception error) { _telemetry.Event("catalog-refresh-error", new { error.Message }); }
    }
    private void OnLibraryFilter(object sender, SelectionChangedEventArgs args)
    {
        if (!_libraryReady || _updatingLibrary) return;
        _libraryPage = 0;
        _libraryPreferences = _libraryPreferences with { Sort = Math.Max(0, LibrarySort.SelectedIndex) };
        QueueLibraryPreferences(); RenderGallery();
    }
    private void OnLibrarySearch(object sender, TextChangedEventArgs args) { if (_libraryReady) { _libraryPage = 0; RenderGallery(); } }
    private void OnRecentSort(object sender, SelectionChangedEventArgs args)
    {
        if (!_libraryReady || _updatingLibrary) return;
        _libraryPreferences = _libraryPreferences with { RecentSort = Math.Max(0, RecentSortPicker.SelectedIndex) };
        QueueLibraryPreferences(); RenderGallery();
    }
    private void OnRecentGrouping(object sender, SelectionChangedEventArgs args)
    {
        if (!_libraryReady || _updatingLibrary) return;
        _libraryPreferences = _libraryPreferences with { RecentGrouping = Math.Max(0, RecentGroupingPicker.SelectedIndex) };
        QueueLibraryPreferences(); RenderGallery();
    }
    private void OnTableSort(object sender, RoutedEventArgs args)
    {
        int current = GallerySort;
        int sort = (string)((Button)sender).Tag switch { "title" => current == 2 ? 4 : 2, "size" => current == 3 ? 5 : 3, _ => current == 0 ? 1 : 0 };
        _libraryPreferences = _libraryVisible ? _libraryPreferences with { Sort = sort } : _libraryPreferences with { RecentSort = sort }; _libraryPage = 0;
        _updatingLibrary = true; LibrarySort.SelectedIndex = _libraryPreferences.Sort; RecentSortPicker.SelectedIndex = _libraryPreferences.RecentSort; _updatingLibrary = false;
        QueueLibraryPreferences(); RenderGallery();
    }

    private int GallerySort => _libraryVisible ? _libraryPreferences.Sort : _libraryPreferences.RecentSort;
    private IEnumerable<LibraryEntry> SortGallery(IEnumerable<LibraryEntry> entries) => GallerySort switch
    {
        1 => entries.OrderBy(entry => entry.CreatedUtc), 2 => entries.OrderBy(GalleryTitle, StringComparer.CurrentCultureIgnoreCase),
        3 => entries.OrderByDescending(entry => entry.Bytes), 4 => entries.OrderByDescending(GalleryTitle, StringComparer.CurrentCultureIgnoreCase),
        5 => entries.OrderBy(entry => entry.Bytes), _ => entries.OrderByDescending(entry => entry.CreatedUtc)
    };
    private void OnGalleryLayout(object sender, RoutedEventArgs args)
    {
        if (!_libraryReady) return;
        RecentViewPopup.IsOpen = false;
        bool list = (string)((Button)sender).Tag == "list";
        if (list == _libraryPreferences.ListView) return;
        _libraryPreferences = _libraryPreferences with { ListView = list };
        QueueLibraryPreferences(); RenderGallery();
    }
    private void OnGalleryZoom(object sender, RoutedPropertyChangedEventArgs<double> args)
    {
        if (!_libraryReady || _updatingLibrary) return;
        double zoom = Math.Round(args.NewValue * 2) / 2;
        bool unchanged = zoom == _libraryPreferences.Zoom && !_libraryPreferences.ListView;
        _libraryPreferences = _libraryPreferences with { Zoom = zoom, ListView = false };
        _updatingLibrary = true; GalleryZoom.Value = LibraryZoom.Value = zoom; _updatingLibrary = false;
        if (unchanged) return;
        QueueLibraryPreferences(); RenderGallery();
    }
    private void OnLibraryPage(object sender, RoutedEventArgs args) { _libraryPage += (string)((Button)sender).Tag == "next" ? 1 : -1; RenderGallery(); }
    private void OnGallerySizeChanged(object sender, SizeChangedEventArgs args)
    {
        if (!_libraryReady || !args.WidthChanged || args.NewSize.Width <= 0 || _galleryReflowPending) return;
        _galleryReflowPending = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => { _galleryReflowPending = false; if (!_closing) RenderGallery(); });
    }

    private void CloseLibrary()
    {
        _libraryVisible = false; _scanCancellation?.Cancel(); LibraryView.Visibility = Visibility.Collapsed;
        HomeView.Visibility = Visibility.Visible; SettingsLabel.Text = Localization.Text("Ui.Settings"); SettingsGlyph.Symbol = Wpf.Ui.Controls.SymbolRegular.Settings24;
        RenderClipCards();
    }

    private void RenderClipCards()
    {
        if (!_libraryReady) return;
        RenderGallery();
    }

    private void RenderGallery()
    {
        if (!_libraryReady || _closing || IsLoaded && !IsVisible || !_libraryVisible && HomeView.Visibility != Visibility.Visible) return;
        IEnumerable<LibraryEntry> entries;
        Panel destination;
        if (_libraryVisible)
        {
            string? game = (LibraryGames.SelectedItem as LibraryChoice)?.Key, folder = (LibraryFolders.SelectedItem as LibraryChoice)?.Key;
            string search = LibrarySearch.Text.Trim();
            entries = _libraryEntries.Where(entry => (game is null || entry.Game == game) && (folder is null || entry.Folder.Equals(folder, StringComparison.OrdinalIgnoreCase)) &&
                (search.Length == 0 || (entry.Clip.Title + " " + entry.Game).Contains(search, StringComparison.OrdinalIgnoreCase)));
            entries = SortGallery(entries);
            LibraryEntry[] all = entries.ToArray();
            int pages = Math.Max(1, (int)Math.Ceiling(all.Length / (double)LibraryPageSize));
            _libraryPage = Math.Clamp(_libraryPage, 0, pages - 1);
            LibraryCount.Text = Localization.Count("Replay", all.Length);
            LibraryPageText.Text = $"{_libraryPage + 1} / {pages}";
            LibraryPrevious.IsEnabled = _libraryPage > 0; LibraryNext.IsEnabled = _libraryPage + 1 < pages;
            entries = all.Skip(_libraryPage * LibraryPageSize).Take(LibraryPageSize); destination = LibraryItems;
        }
        else
        {
            entries = _libraryEntries.OrderByDescending(entry => entry.CreatedUtc).ThenBy(entry => entry.Clip.Video, StringComparer.OrdinalIgnoreCase).Take(12);
            destination = RecentItems;
            entries = SortGallery(entries);
        }
        double available = Math.Max(240, destination.ActualWidth > 0 ? destination.ActualWidth : 472);
        int columns = _libraryPreferences.Zoom < .25 ? 3 : _libraryPreferences.Zoom < .75 ? 2 : 1;
        double width = _libraryPreferences.ListView ? available - 12 : Math.Floor((available - columns * 12) / columns);
        foreach (Grid header in new[] { HomeTableHeader, LibraryTableHeader })
        {
            int sort = header == HomeTableHeader ? _libraryPreferences.RecentSort : _libraryPreferences.Sort;
            header.Visibility = _libraryPreferences.ListView ? Visibility.Visible : Visibility.Collapsed;
            foreach (Button button in header.Children.OfType<Button>())
            {
                string key = (string)button.Tag;
                string direction = key == "title" ? sort == 2 ? " ↑" : sort == 4 ? " ↓" : "" :
                    key == "size" ? sort == 3 ? " ↓" : sort == 5 ? " ↑" : "" : sort == 0 ? " ↓" : sort == 1 ? " ↑" : "";
                button.Content = (key == "title" ? Localization.Text("Ui.File") : key == "size" ? Localization.Text("Ui.Size") : Localization.Text("Ui.Date")) + direction;
            }
        }
        LibraryEntry[] visibleEntries = entries.ToArray();
        string sessionHeaders = !_libraryVisible && _libraryPreferences.RecentGrouping == 2
            ? JsonSerializer.Serialize(ReplaySessions.Group(_libraryEntries).Select(session => new { session.Application, session.StartedUtc, session.LastSavedUtc, session.Estimated })) : "";
        var layout = new GalleryLayoutState(_libraryVisible, _libraryPreferences.ListView, _libraryPreferences.RecentGrouping, GallerySort, Math.Round(width, 2), DateTime.Now.Date, sessionHeaders);
        GalleryItemState[] items = visibleEntries.Select(entry => new GalleryItemState(entry.Clip.Video, entry.Clip.Title, entry.Game, entry.CreatedUtc, entry.ModifiedUtc,
            entry.Bytes, entry.Clip.Duration, entry.Clip.Fps, entry.Clip.Thumbnail, entry.Clip.EventId, entry.Clip.SessionId, entry.Clip.SessionStartedUtc,
            string.Join('|', (entry.Clip.Screens ?? [entry.Clip]).Select(screen => screen.Video)),
            JsonSerializer.Serialize(new { entry.Clip.Audio, entry.Clip.EmbeddedSystemAudio, entry.Clip.EmbeddedAudioSources }))).ToArray();
        // Сверка каталога и повторный layout не должны срывать загрузку уже видимых превью.
        if (_lastGalleryLayout == layout && _lastGalleryItems is not null && _lastGalleryItems.SequenceEqual(items) &&
            _previewCancellation is { IsCancellationRequested: false }) return;
        _lastGalleryLayout = layout; _lastGalleryItems = items;
        _previewCancellation?.Cancel(); _previewCancellation?.Dispose(); _previewCancellation = new();
        int version = ++_renderVersion;
        destination.Children.Clear(); entries = visibleEntries;
        if (!_libraryVisible)
        {
            if (_libraryPreferences.RecentGrouping == 2)
            {
                var visible = entries.Select(entry => entry.Clip.Video).ToHashSet(StringComparer.OrdinalIgnoreCase);
                ReplaySessionGroup[] sessions = ReplaySessions.Group(_libraryEntries);
                foreach (ReplaySessionGroup session in _libraryPreferences.RecentSort == 1 ? sessions.Reverse() : sessions)
                {
                    LibraryEntry[] members = session.Entries.Where(entry => visible.Contains(entry.Clip.Video)).ToArray();
                    if (members.Length == 0) continue;
                    var block = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
                    System.Windows.Automation.AutomationProperties.SetName(block, Localization.Text("Message.ReplaySession"));
                    block.Children.Add(new TextBlock { Text = session.Application, FontSize = 13, FontWeight = FontWeights.SemiBold, Margin = new Thickness(4, 0, 0, 5), TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = session.Application });
                    var culture = Localization.Culture;
                    string timeFormat = session.StartedUtc.ToLocalTime().Year == DateTime.Now.Year && session.LastSavedUtc.ToLocalTime().Year == DateTime.Now.Year ? "d MMM, HH:mm" : "d MMM yyyy, HH:mm";
                    string details = (session.Estimated ? "≈ " : "") + session.StartedUtc.ToLocalTime().ToString(timeFormat, culture) + " — " + session.LastSavedUtc.ToLocalTime().ToString(timeFormat, culture);
                    block.Children.Add(new TextBlock { Text = details, FontSize = 11, Foreground = (Brush)FindResource("Muted"), Margin = new Thickness(4, 0, 0, 12), TextWrapping = TextWrapping.Wrap,
                        ToolTip = session.Estimated ? Localization.Text("Message.ApproximateSessionGapsOfUpTo90MinutesBetweenReplays") : Localization.Text("Message.FromApplicationLaunchToTheLastSavedReplay") });
                    var cards = new WrapPanel();
                    foreach (LibraryEntry entry in SortGallery(members)) cards.Children.Add(LibraryCard(entry, width, _libraryPreferences.ListView, version, _previewCancellation.Token));
                    block.Children.Add(cards); destination.Children.Add(block);
                }
            }
            else if (_libraryPreferences.RecentGrouping == 1)
            {
                var applications = entries.GroupBy(entry => entry.Game, StringComparer.OrdinalIgnoreCase);
                foreach (var application in _libraryPreferences.RecentSort == 1 ? applications.OrderBy(group => group.Max(entry => entry.CreatedUtc)) : applications.OrderByDescending(group => group.Max(entry => entry.CreatedUtc)))
                {
                    var title = new TextBlock { Text = application.Key, FontSize = 13, FontWeight = FontWeights.SemiBold, Margin = new Thickness(4, 8, 0, 12), TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = application.Key };
                    System.Windows.Automation.AutomationProperties.SetName(title, Localization.Text("Message.ApplicationGroup")); destination.Children.Add(title);
                    var cards = new WrapPanel();
                    foreach (LibraryEntry entry in application) cards.Children.Add(LibraryCard(entry, width, _libraryPreferences.ListView, version, _previewCancellation.Token));
                    destination.Children.Add(cards);
                }
            }
            else
            {
                var days = entries.GroupBy(entry => entry.CreatedUtc.ToLocalTime().Date);
                foreach (var day in _libraryPreferences.RecentSort == 1 ? days.OrderBy(group => group.Key) : days.OrderByDescending(group => group.Key))
                {
                    var heading = new TextBlock { Text = RecentDateTitle(day.Key), FontSize = 13, FontWeight = FontWeights.SemiBold,
                        Margin = new Thickness(4, destination.Children.Count == 0 ? 4 : 8, 0, 12) };
                    System.Windows.Automation.AutomationProperties.SetName(heading, Localization.Text("Message.DateGroup"));
                    destination.Children.Add(heading);
                    var cards = new WrapPanel();
                    foreach (LibraryEntry entry in day) cards.Children.Add(LibraryCard(entry, width, _libraryPreferences.ListView, version, _previewCancellation.Token));
                    destination.Children.Add(cards);
                }
            }
        }
        else foreach (LibraryEntry entry in entries) destination.Children.Add(LibraryCard(entry, width, _libraryPreferences.ListView, version, _previewCancellation.Token));
        if (destination.Children.Count == 0)
            destination.Children.Add(new TextBlock { Text = _libraryVisible ? Localization.Text("Message.NoReplaysFound") : Localization.Text("Message.SavedMomentsWillAppearHere"), Foreground = (Brush)FindResource("Muted"), Margin = new Thickness(0, 16, 0, 0) });
        GridViewButton.Background = LibraryGridButton.Background = (Brush)FindResource(!_libraryPreferences.ListView ? "Hover" : "Surface");
        ListViewButton.Background = LibraryListButton.Background = (Brush)FindResource(_libraryPreferences.ListView ? "Hover" : "Surface");
    }

    private static string RecentDateTitle(DateTime date)
    {
        DateTime today = DateTime.Now.Date;
        return date == today ? Localization.Text("Message.Today") : date == today.AddDays(-1) ? Localization.Text("Message.Yesterday") :
            date.ToString(date.Year == today.Year ? "d MMMM" : "d MMMM yyyy", Localization.Culture);
    }

    private Button LibraryCard(LibraryEntry entry, double width, bool list, int version, CancellationToken cancellation)
    {
        Clip clip = entry.Clip;
        var preview = new Grid { Background = (Brush)FindResource("Raised"), ClipToBounds = true, ToolTip = Localization.Text("Message.LoadingPreview") };
        var placeholder = new Wpf.Ui.Controls.SymbolIcon { Symbol = Wpf.Ui.Controls.SymbolRegular.Video24, FontSize = 22,
            Foreground = (Brush)FindResource("Muted"), Opacity = .55, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        System.Windows.Automation.AutomationProperties.SetName(placeholder, Localization.Text("Message.LoadingPreview29f3bdf5"));
        preview.Children.Add(placeholder);
        var image = new Image { Stretch = Stretch.Uniform };
        preview.Children.Add(image);
        void ShowPreview(BitmapSource bitmap)
        {
            image.Source = bitmap;
            placeholder.Visibility = Visibility.Collapsed;
            preview.Background = Brushes.Black;
            preview.ToolTip = null;
        }
        if (_galleryPreviews.TryGetValue(clip.Video, out GalleryPreview? cached) && cached.Bytes == entry.Bytes && cached.Modified == entry.ModifiedUtc)
            ShowPreview(cached.Image);
        var duration = new TextBlock { Text = clip.Duration > 0 ? MediaDuration(clip.Duration) : "…", Foreground = Brushes.White, FontSize = 12 };
        System.Windows.Automation.AutomationProperties.SetName(duration, Localization.Text("Message.ReplayDuration"));
        var warning = new Wpf.Ui.Controls.SymbolIcon { Symbol = Wpf.Ui.Controls.SymbolRegular.Warning24, FontSize = 12,
            Foreground = new SolidColorBrush(Color.FromRgb(229, 181, 74)), VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 4, 0), Visibility = Visibility.Collapsed, ToolTip = Localization.Text("Message.PlaybackErrorsHaveOccurredInThisRecording") };
        System.Windows.Automation.AutomationProperties.SetName(warning, Localization.Text("Ui.ErrorsFoundInTheRecording"));
        _healthBadges.RemoveAll(badge => !badge.Icon.TryGetTarget(out _));
        _healthBadges.Add(((clip.Screens ?? [clip]).Select(screen => screen.Video).ToArray(), new(warning)));
        var durationBadge = new StackPanel { Orientation = Orientation.Horizontal };
        durationBadge.Children.Add(warning); durationBadge.Children.Add(duration);
        preview.Children.Add(new Border { Child = durationBadge, Background = new SolidColorBrush(Color.FromArgb(225, 20, 20, 20)),
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom, Padding = new Thickness(5, 2, 5, 2), Margin = new Thickness(5) });
        if (clip.Screens is { Length: > 1 }) preview.Children.Add(new Border { Child = new TextBlock { Text = Localization.Count("Screen", clip.Screens.Length), FontSize = 11 },
            Background = (Brush)FindResource("Raised"), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Padding = new Thickness(5, 2, 5, 2), Margin = new Thickness(5) });
        var frame = new SixteenNineFrame { Child = preview, Width = list ? 72 : width - 8, VerticalAlignment = VerticalAlignment.Center };
        var labels = new StackPanel { Margin = list ? new Thickness(0, 0, 8, 0) : new Thickness(0, 9, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        string title = _libraryVisible ? GalleryTitle(entry) : entry.Game;
        labels.Children.Add(new TextBlock { Text = title, FontSize = 13, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
        if (!list) labels.Children.Add(new TextBlock { Text = entry.CreatedUtc.ToLocalTime().ToString(!_libraryVisible && _libraryPreferences.RecentGrouping == 0 ? "HH:mm" : "dd.MM · HH:mm"), FontSize = 12, Foreground = (Brush)FindResource("Muted"), Margin = new Thickness(0, 4, 0, 0) });
        var content = new Grid(); content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        if (list)
        {
            content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(84) }); content.ColumnDefinitions.Add(new ColumnDefinition());
            content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(62) }); content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(88) }); Grid.SetColumn(labels, 1);
            var size = new TextBlock { Text = FormatBytes(entry.Bytes), FontSize = 11, Foreground = (Brush)FindResource("Muted"), VerticalAlignment = VerticalAlignment.Center };
            var date = new TextBlock { Text = entry.CreatedUtc.ToLocalTime().ToString("dd.MM HH:mm"), FontSize = 11, Foreground = (Brush)FindResource("Muted"), VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(size, 2); Grid.SetColumn(date, 3); content.Children.Add(size); content.Children.Add(date);
        }
        else { content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); Grid.SetRow(labels, 1); }
        content.Children.Add(frame); content.Children.Add(labels);
        var button = new Button { Content = content, Width = width, Padding = new Thickness(4), Margin = new Thickness(0, 0, 12, 14), HorizontalContentAlignment = HorizontalAlignment.Stretch, ToolTip = clip.Video };
        System.Windows.Automation.AutomationProperties.SetName(button, Localization.Text("Message.OpenReplay") + Path.GetFileName(clip.Video));
        button.Click += async (_, _) =>
        {
            try { await OpenClipAsync(clip); }
            catch (Exception error) { _telemetry.Event("library-play-error", new { error.Message }); if (_libraryVisible) LibraryMessage.Text = Localization.Text("Message.CouldNotOpenFile"); else SetCaptureMessage(() => Localization.Text("Message.CouldNotOpenFile")); }
        };
        async Task PreparePreviewAsync()
        {
            try
            {
                if (image.Source is not null && clip.Duration > 0) return;
                Clip ready = await _libraryIndex.PrepareAsync(clip, cancellation);
                if (version != _renderVersion || _closing || cancellation.IsCancellationRequested) return;
                duration.Text = MediaDuration(ready.Duration);
                if (string.IsNullOrEmpty(ready.Thumbnail)) { UnavailablePreview(); return; }
                // Декодируем JPEG вне UI-потока; готовый frozen-кадр переживает перестроение карточек.
                BitmapSource bitmap = await Task.Run(() =>
                {
                    var decoded = new BitmapImage(); decoded.BeginInit(); decoded.CacheOption = BitmapCacheOption.OnLoad; decoded.DecodePixelWidth = 480;
                    decoded.UriSource = new Uri(ready.Thumbnail); decoded.EndInit(); decoded.Freeze(); return decoded;
                }, cancellation);
                if (_closing) return;
                if (!_galleryPreviews.ContainsKey(clip.Video)) _galleryPreviewOrder.Enqueue(clip.Video);
                _galleryPreviews[clip.Video] = new(entry.Bytes, entry.ModifiedUtc, bitmap);
                // 48 изображений по 480 px: кэш ограничен и не растёт вместе со всем архивом.
                while (_galleryPreviews.Count > 48) _galleryPreviews.Remove(_galleryPreviewOrder.Dequeue());
                if (version != _renderVersion || cancellation.IsCancellationRequested) return;
                ShowPreview(bitmap);
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
            catch (Exception error)
            {
                _telemetry.Event("library-preview-error", new { clip.Video, error.Message });
                if (version != _renderVersion || _closing || cancellation.IsCancellationRequested) return;
                if (clip.Duration <= 0) duration.Text = "—";
                UnavailablePreview();
            }
        }
        void UnavailablePreview()
        {
            if (image.Source is not null) return;
            placeholder.Symbol = Wpf.Ui.Controls.SymbolRegular.VideoOff24;
            preview.ToolTip = Localization.Text("Message.PreviewUnavailable");
            System.Windows.Automation.AutomationProperties.SetName(placeholder, Localization.Text("Message.PreviewUnavailable"));
        }
        async Task RefreshHealthAsync()
        {
            try
            {
                foreach (Clip screen in clip.Screens is { Length: > 1 } screens ? screens : [clip])
                {
                    PlaybackHealth health = await _playbackHealth.ReadAsync(screen.Video, cancellation);
                    if (version != _renderVersion || _closing || cancellation.IsCancellationRequested) return;
                    if (health.Issues.Length == 0) continue;
                    warning.Visibility = Visibility.Visible; break;
                }
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
            catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException)
            { _telemetry.Event("library-health-read-error", new { clip.Video, error.Message }); }
        }
        bool prepareOnLoaded = !IsLoaded && !_libraryVisible && string.IsNullOrEmpty(clip.Thumbnail);
        // Конструктор вызывается до app.Run: async-операции с UI запускаем через Dispatcher явно.
        Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            if (_closing || version != _renderVersion || cancellation.IsCancellationRequested) return;
            _ = RefreshHealthAsync();
            if (!prepareOnLoaded)
            {
                Task loading = PreparePreviewAsync();
                _galleryWarmLoads.RemoveAll(task => task.IsCompleted);
                if (!loading.IsCompleted && !string.IsNullOrEmpty(clip.Thumbnail)) _galleryWarmLoads.Add(loading);
            }
        });
        if (prepareOnLoaded)
        {
            // В фоне прогреваем готовые JPEG; неизвестные ролики не запускают ffmpeg до показа карточки.
            void PrepareOnLoaded(object sender, RoutedEventArgs args)
            {
                button.Loaded -= PrepareOnLoaded;
                _ = PreparePreviewAsync();
            }
            button.Loaded += PrepareOnLoaded;
        }
        return button;
    }

    private static string MediaDuration(double seconds) => TimeSpan.FromSeconds(Math.Max(0, seconds)).ToString(seconds >= 3600 ? @"h\:mm\:ss" : @"m\:ss");
    private static string GalleryTitle(LibraryEntry entry) => Path.GetFileName(entry.Clip.Video);
    internal static string FormatBytes(long bytes) => bytes >= 1L << 30 ? Localization.Format("Format.SizeGb", bytes / (double)(1L << 30)) : bytes >= 1L << 20 ? Localization.Format("Format.SizeMb", bytes / (double)(1L << 20)) : Localization.Format("Format.SizeKb", Math.Ceiling(bytes / 1024d));
}
