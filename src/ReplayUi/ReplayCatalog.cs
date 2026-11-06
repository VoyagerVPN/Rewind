using System.IO;
using System.Text.Json;

namespace Rewind.Preview;

internal sealed record CatalogProgress(int Files, int Folders, bool Complete, string? Warning = null);

internal sealed partial class ReplayLibraryIndex : IDisposable
{
    private sealed record CatalogSnapshot(int Version, DateTime UpdatedUtc, LibraryEntry[] Files);
    private readonly object _catalogGate = new();
    private Dictionary<string, LibraryEntry>? _catalog;
    private Task? _catalogScan;
    private string _activeRoots = "";
    private DateTime _lastScan;
    private long _changeVersion;
    private long _scannedVersion = -1;
    private long _saveVersion;
    private Task? _saveTask;
    private volatile bool _disposed;
    private readonly List<FileSystemWatcher> _catalogWatchers = [];
    public event Action? Invalidated;
    public event Action<CatalogProgress>? Progress;
    public event Action? SnapshotChanged;
    public string? Warning { get; private set; }

    private void LoadCatalog()
    {
        if (_catalog is not null) return;
        _catalog = new(StringComparer.OrdinalIgnoreCase);
        string file = Path.Combine(cache, "catalog-v1.json");
        try
        {
            if (!File.Exists(file) || new FileInfo(file).Length > 64 * 1024 * 1024) return;
            CatalogSnapshot? snapshot = JsonSerializer.Deserialize<CatalogSnapshot>(File.ReadAllText(file));
            if (snapshot?.Version == 1)
            {
                if (snapshot.Files is null) throw new JsonException(Localization.Text("Message.TheCatalogSnapshotIsMissingItsFileList"));
                int skipped = 0;
                foreach (LibraryEntry entry in snapshot.Files)
                {
                    if (entry?.Clip is null || string.IsNullOrWhiteSpace(entry.Clip.Video)) { skipped++; continue; }
                    _catalog[entry.Clip.Video] = entry;
                }
                if (skipped > 0) telemetry.Event("catalog-cache-entries-skipped", new { count = skipped });
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        { telemetry.Event("catalog-cache-error", new { error.Message }); }
    }

    private static bool Inside(string path, string root) => path.StartsWith(Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    private static string[] NormalizeRoots(string[] roots) => roots.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase)
        .OrderBy(root => root.Length).Aggregate(new List<string>(), (list, root) => { if (!list.Any(parent => Inside(root, parent))) list.Add(root); return list; }).ToArray();

    public LibraryEntry[] ReadSnapshot(string[] roots, Clip[] known, bool group = true)
    {
        lock (_catalogGate)
        {
            LoadCatalog();
            foreach (Clip clip in known.SelectMany(clip => clip.Screens ?? [clip]))
                if (File.Exists(clip.Video))
                {
                    string? root = NormalizeRoots(roots).FirstOrDefault(root => Inside(clip.Video, root));
                    if (root is not null) _catalog![clip.Video] = FileEntry(clip, root, new FileInfo(clip.Video));
                }
            IEnumerable<LibraryEntry> entries = _catalog!.Values.Where(entry => roots.Any(root => Inside(entry.Clip.Video, root)));
            return group ? GroupEntries(entries) : entries.ToArray();
        }
    }

    private static LibraryEntry[] GroupEntries(IEnumerable<LibraryEntry> entries) => entries.GroupBy(entry => entry.Clip.EventId ?? entry.Clip.Video, StringComparer.OrdinalIgnoreCase)
        .Select(group => { LibraryEntry first = group.First(); Clip[] screens = group.Select(entry => entry.Clip with { Screens = null }).ToArray();
            return screens.Length > 1 ? first with { Clip = first.Clip with { Screens = screens }, Bytes = group.Sum(entry => entry.Bytes) } : first; })
        .OrderByDescending(entry => entry.CreatedUtc).ToArray();

    private static LibraryEntry FileEntry(Clip clip, string root, FileInfo file)
    {
        string relative = Path.GetRelativePath(root, file.DirectoryName!);
        string rootName = Path.GetFileName(Path.TrimEndingDirectorySeparator(root));
        string[] parts = relative.Split(Path.DirectorySeparatorChar);
        int clipsFolder = Array.FindLastIndex(parts, part => part.Equals("Clips", StringComparison.OrdinalIgnoreCase));
        string game = relative == "." ? rootName is "" or "Clips" ? "Desktop" : rootName : clipsFolder >= 0 ? parts.ElementAtOrDefault(clipsFolder + 1) ?? "Desktop" : parts[0];
        return new(clip, string.IsNullOrWhiteSpace(clip.ApplicationName) ? game : clip.ApplicationName, root, clip.SavedAtUtc?.ToUniversalTime() ?? file.LastWriteTimeUtc, file.Length, file.LastWriteTimeUtc);
    }

    public async Task<LibraryEntry[]> ScanAsync(string[] roots, Clip[] known, CancellationToken cancellation, bool force = false)
    {
        roots = NormalizeRoots(roots);
        Task task;
        lock (_catalogGate)
        {
            ReadSnapshot(roots, known);
            string key = string.Join('|', roots);
            if (_catalogScan is { IsCompleted: false }) task = _catalogScan;
            else if (!force && _activeRoots == key && _scannedVersion == _changeVersion && DateTime.UtcNow - _lastScan < TimeSpan.FromSeconds(10))
                return ReadSnapshot(roots, []);
            else
            {
                _activeRoots = key; ConfigureWatchers(roots); long version = _changeVersion;
                task = _catalogScan = Task.Run(() => Reconcile(roots, version));
            }
        }
        await task.WaitAsync(cancellation);
        bool changed;
        lock (_catalogGate) changed = !_disposed && (_activeRoots != string.Join('|', roots) || _scannedVersion != _changeVersion);
        if (changed) return await ScanAsync(roots, known, cancellation, true);
        // Сверка публикует каталог; возвращаем защищённый снимок с последними сохранениями.
        return ReadSnapshot(roots, []);
    }

    private void Reconcile(string[] roots, long version)
    {
        var timer = System.Diagnostics.Stopwatch.StartNew();
        Dictionary<string, LibraryEntry> old;
        lock (_catalogGate) { LoadCatalog(); old = new(_catalog!, StringComparer.OrdinalIgnoreCase); }
        var next = new Dictionary<string, LibraryEntry>(StringComparer.OrdinalIgnoreCase);
        int folders = 0, files = 0; string? warning = null; long reported = -250;
        foreach (string root in roots)
        {
            var pending = new Stack<string>(); pending.Push(root);
            while (pending.TryPop(out string? folder))
            {
                if (_disposed) return;
                try
                {
                    var directory = new DirectoryInfo(folder);
                    if (!directory.Exists) throw new DirectoryNotFoundException(folder);
                    foreach (DirectoryInfo child in directory.EnumerateDirectories())
                        if (!Ignored.Contains(child.Name) && !child.Name.StartsWith('.') && (child.Attributes & FileAttributes.ReparsePoint) == 0) pending.Push(child.FullName);
                    foreach (FileInfo file in directory.EnumerateFiles())
                    {
                        if (!Extensions.Contains(file.Extension) || file.Name.EndsWith(".partial.mp4", StringComparison.OrdinalIgnoreCase) || (file.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                        try
                        {
                            Clip clip = old.TryGetValue(file.FullName, out LibraryEntry? cached) && cached.Bytes == file.Length && (cached.ModifiedUtc ?? cached.CreatedUtc) == file.LastWriteTimeUtc
                                ? cached.Clip : Unprobed(file.FullName);
                            // Старые JSON малы; контейнеры видео при сверке вообще не открываем.
                            if (clip.EventId is null && File.Exists(Path.ChangeExtension(file.FullName, ".json")))
                            {
                                SavedReplay? legacy = ReplayMetadata.ReadLegacy(file.FullName);
                                if (legacy is not null) clip = OverlayWindow.ClipFromReplay(legacy, false);
                            }
                            next[file.FullName] = FileEntry(clip, root, file); files++;
                        }
                        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
                        { warning = Localization.Text("Message.SomeFilesAreUnavailable"); if (old.TryGetValue(file.FullName, out LibraryEntry? previous)) next[file.FullName] = previous; }
                    }
                    folders++;
                    if (timer.ElapsedMilliseconds - reported >= 200)
                    {
                        reported = timer.ElapsedMilliseconds;
                        lock (_catalogGate)
                        {
                            foreach (var pair in next)
                            {
                                LibraryEntry value = pair.Value;
                                if (_catalog!.TryGetValue(pair.Key, out LibraryEntry? prepared) && prepared.Bytes == value.Bytes && (prepared.ModifiedUtc ?? prepared.CreatedUtc) == (value.ModifiedUtc ?? value.CreatedUtc) && prepared.Clip.Duration > 0)
                                    value = prepared with { Folder = value.Folder };
                                _catalog![pair.Key] = value;
                            }
                        }
                        Progress?.Invoke(new(files, folders, false, warning)); SnapshotChanged?.Invoke();
                    }
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    warning = Localization.Text("Message.SomeFoldersAreUnavailableShowingTheSavedSnapshot");
                    foreach (var pair in old.Where(pair => Inside(pair.Key, folder))) next.TryAdd(pair.Key, pair.Value);
                    telemetry.Event("catalog-folder-unavailable", new { folder, error.Message });
                }
            }
        }
        lock (_catalogGate)
        {
            // Обогащение видимых файлов могло завершиться во время перечисления.
            foreach (var pair in next.ToArray())
                if (_catalog!.TryGetValue(pair.Key, out LibraryEntry? prepared) && prepared.Bytes == pair.Value.Bytes && (prepared.ModifiedUtc ?? prepared.CreatedUtc) == (pair.Value.ModifiedUtc ?? pair.Value.CreatedUtc) && prepared.Clip.Duration > 0)
                    next[pair.Key] = prepared with { Folder = pair.Value.Folder };
            _catalog = next; _lastScan = DateTime.UtcNow; _scannedVersion = version;
        }
        QueueCatalogSave().GetAwaiter().GetResult();
        Warning = warning;
        Progress?.Invoke(new(files, folders, true, warning)); SnapshotChanged?.Invoke();
        telemetry.Event("catalog-reconciled", new { files, folders, milliseconds = timer.ElapsedMilliseconds, warning });
    }

    private Task QueueCatalogSave()
    {
        lock (_catalogGate)
        {
            _saveVersion++;
            if (_saveTask is { IsCompleted: false }) return _saveTask;
            _saveTask = Task.Run(async () =>
            {
                await Task.Delay(120).ConfigureAwait(false);
                while (true)
                {
                    CatalogSnapshot snapshot; long version;
                    lock (_catalogGate) { snapshot = new(1, DateTime.UtcNow, _catalog!.Values.ToArray()); version = _saveVersion; }
                    try
                    {
                        Directory.CreateDirectory(cache); string file = Path.Combine(cache, "catalog-v1.json");
                        await File.WriteAllTextAsync(file + ".new", JsonSerializer.Serialize(snapshot)).ConfigureAwait(false);
                        File.Move(file + ".new", file, true);
                    }
                    catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                    { telemetry.Event("catalog-save-error", new { error.Message }); lock (_catalogGate) _saveTask = null; return; }
                    lock (_catalogGate) { if (_saveVersion == version) { _saveTask = null; return; } }
                }
            });
            return _saveTask;
        }
    }

    private void StorePrepared(Clip clip)
    {
        bool grouped = false;
        lock (_catalogGate)
        {
            LoadCatalog();
            if (_catalog!.TryGetValue(clip.Video, out LibraryEntry? entry))
            {
                grouped = entry.Clip.EventId != clip.EventId || entry.Clip.SessionId != clip.SessionId || entry.Clip.ApplicationName != clip.ApplicationName || entry.Clip.SavedAtUtc != clip.SavedAtUtc;
                _catalog[clip.Video] = FileEntry(clip with { Screens = null }, entry.Folder, new FileInfo(clip.Video));
            }
        }
        _ = QueueCatalogSave();
        if (grouped) SnapshotChanged?.Invoke();
    }

    private void ConfigureWatchers(string[] roots)
    {
        if (_disposed) return;
        if (_catalogWatchers.Select(watcher => watcher.Path).SequenceEqual(roots.Where(Directory.Exists), StringComparer.OrdinalIgnoreCase)) return;
        foreach (FileSystemWatcher watcher in _catalogWatchers) watcher.Dispose(); _catalogWatchers.Clear();
        foreach (string root in roots.Where(Directory.Exists))
        {
            try
            {
                var watcher = new FileSystemWatcher(root) { IncludeSubdirectories = true, NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.Size | NotifyFilters.LastWrite };
                void Dirty(object? sender, FileSystemEventArgs args)
                {
                    bool Relevant(string path)
                    {
                        string cachePath = Path.GetFullPath(cache);
                        if (path.Equals(cachePath, StringComparison.OrdinalIgnoreCase) || Inside(path, cachePath)) return false;
                        string relative = Path.GetRelativePath(root, path);
                        if (relative.Split(Path.DirectorySeparatorChar).Any(part => Ignored.Contains(part) || part.StartsWith('.'))) return false;
                        if (path.EndsWith(".partial.mp4", StringComparison.OrdinalIgnoreCase)) return false;
                        if (Extensions.Contains(Path.GetExtension(path)) || Directory.Exists(path)) return true;
                        lock (_catalogGate) return _catalog!.ContainsKey(path) || _catalog.Keys.Any(file => Inside(file, path));
                    }
                    if (!Relevant(args.FullPath) && (args is not RenamedEventArgs renamed || !Relevant(renamed.OldFullPath))) return;
                    lock (_catalogGate) _changeVersion++;
                    Invalidated?.Invoke();
                }
                watcher.Changed += Dirty; watcher.Created += Dirty; watcher.Deleted += Dirty; watcher.Renamed += (sender, args) => Dirty(sender, args);
                watcher.Error += (_, _) => { lock (_catalogGate) _changeVersion++; Invalidated?.Invoke(); };
                watcher.EnableRaisingEvents = true; _catalogWatchers.Add(watcher);
            }
            catch (IOException error) { telemetry.Event("catalog-watcher-error", new { root, error.Message }); }
        }
    }
    public void Dispose()
    {
        FileSystemWatcher[] watchers;
        lock (_catalogGate) { _disposed = true; watchers = _catalogWatchers.ToArray(); _catalogWatchers.Clear(); }
        foreach (FileSystemWatcher watcher in watchers) watcher.Dispose();
    }
}
