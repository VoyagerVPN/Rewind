using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows.Threading;

namespace Rewind.Preview;

internal static class CatalogMeasurement
{
    public static async Task RunAsync(string diagnostics)
    {
        string run = Path.Combine(diagnostics, "catalog-measurement"); Directory.CreateDirectory(run);
        CaptureSettings settings = new CaptureSettingsFile(Path.Combine(diagnostics, "capture-settings.json")).Load()!;
        LibraryPreferences preferences = JsonSerializer.Deserialize<LibraryPreferences>(File.ReadAllText(Path.Combine(diagnostics, "library-settings.json")))!;
        string[] roots = preferences.Folders.Append(Path.Combine(settings.StorageRoot, "Clips")).ToArray();
        using var telemetry = new UiTelemetry(Dispatcher.CurrentDispatcher, run, () => false);
        using var catalog = new ReplayLibraryIndex(Path.Combine(diagnostics, "library-cache"), telemetry);
        int discovered = 0; catalog.Progress += progress => { if (progress.Complete) discovered = progress.Files; };
        var timer = Stopwatch.StartNew();
        LibraryEntry[] entries = await catalog.ScanAsync(roots, [], CancellationToken.None, true);
        double scanMs = timer.Elapsed.TotalMilliseconds;
        if (catalog.ReadSnapshot(roots, [], false).Length != discovered) throw new InvalidDataException("Фильтр корня потерял файлы, найденные перечислением.");
        using var reopened = new ReplayLibraryIndex(Path.Combine(diagnostics, "library-cache"), telemetry);
        timer.Restart(); LibraryEntry[] cached = reopened.ReadSnapshot(roots, []); double snapshotMs = timer.Elapsed.TotalMilliseconds;
        if (cached.Length != entries.Length || cached.Sum(entry => entry.Bytes) != entries.Sum(entry => entry.Bytes)) throw new InvalidDataException("Сохранённый каталог не совпадает с результатом сверки.");
        foreach (string file in new[] { @"N:\Arc Raiders\Arc Raiders 2026.10.18 - 17.01.00.02.DVR.mp4", @"N:\Deadlock\Deadlock 2026.10.17 - 23.36.41.07.DVR.mp4" })
        {
            LibraryEntry? entry = entries.FirstOrDefault(entry => entry.Clip.Video.Equals(file, StringComparison.OrdinalIgnoreCase));
            if (entry is null) continue;
            Clip prepared = await catalog.PrepareAsync(entry.Clip, CancellationToken.None);
            if (prepared.Duration <= 0) throw new InvalidDataException("Не прочитана длительность старого NVIDIA-клипа.");
            if (string.IsNullOrEmpty(prepared.Thumbnail)) continue;
            var bitmap = new System.Windows.Media.Imaging.BitmapImage(); bitmap.BeginInit(); bitmap.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
            bitmap.UriSource = new Uri(prepared.Thumbnail); bitmap.EndInit();
            if (bitmap.PixelWidth != 480) throw new InvalidDataException("Неверный размер превью старого NVIDIA-клипа.");
        }
        File.WriteAllText(Path.Combine(diagnostics, "catalog-measurement.json"), JsonSerializer.Serialize(new { completedUtc = DateTime.UtcNow, entries = entries.Length, bytes = entries.Sum(entry => entry.Bytes), games = entries.Select(entry => entry.Game).Distinct().Count(), scanMs, snapshotMs }, new JsonSerializerOptions { WriteIndented = true }));
    }
}
