using System.IO;
using System.Text.Json;
using System.Windows.Threading;

namespace Rewind.Preview;

internal static class CatalogConcurrencyCheck
{
    public static async Task RunAsync(string diagnostics)
    {
        diagnostics = Path.GetFullPath(diagnostics);
        string run = Path.Combine(diagnostics, "catalog-concurrency-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(run);
        using var telemetry = new UiTelemetry(Dispatcher.CurrentDispatcher, run, () => false);
        var results = new List<object>();
        Exception? failure = null;
        for (int attempt = 0; attempt < 4; attempt++)
        {
            string folder = Path.Combine(run, "case-" + attempt), archive = Path.Combine(folder, "Clips"), late = Path.Combine(archive, "late");
            Directory.CreateDirectory(archive);
            // Здесь проверяется только каталог: медиапроцессы и контейнеры не нужны.
            for (int i = 0; i < 2000; i++) File.WriteAllBytes(Path.Combine(archive, $"existing-{i:0000}.mp4"), [0]);
            using var index = new ReplayLibraryIndex(Path.Combine(folder, "cache"), telemetry);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            using var updaterStarted = new ManualResetEventSlim();
            Task? updater = null;
            Clip[] known = [];
            int staged = 0, published = 0;
            index.Progress += progress =>
            {
                if (!progress.Complete || Interlocked.Exchange(ref staged, 1) != 0) return;
                Directory.CreateDirectory(late);
                known = Enumerable.Range(0, 512).Select(i =>
                {
                    string file = Path.Combine(late, $"saved-{i:0000}.mp4"); File.WriteAllBytes(file, [1]);
                    return new Clip($"saved-{i}", "", file, "", [null, null, null], 12, 120) with { EventId = "event-" + i / 2, ApplicationName = "Concurrency fixture" };
                }).ToArray();
                updater = Task.Run(() =>
                {
                    updaterStarted.Set();
                    foreach (Clip clip in known)
                    {
                        timeout.Token.ThrowIfCancellationRequested();
                        index.ReadSnapshot([archive], [clip], group: false);
                        Interlocked.Increment(ref published);
                    }
                }, timeout.Token);
                if (!updaterStarted.Wait(TimeSpan.FromSeconds(3))) throw new TimeoutException("Обновление каталога не запустилось.");
            };
            Exception? scanError = null;
            try { await index.ScanAsync([archive], [], timeout.Token); }
            catch (Exception error) { scanError = error; }
            if (updater is not null) await updater.WaitAsync(timeout.Token);
            LibraryEntry[] files = index.ReadSnapshot([archive], [], group: false);
            LibraryEntry[] groups = index.ReadSnapshot([archive], []);
            bool allKnown = known.Length == 512 && known.All(clip => files.Any(entry => entry.Clip.Video == clip.Video));
            bool grouped = groups.Count(entry => entry.Clip.EventId?.StartsWith("event-", StringComparison.Ordinal) == true && entry.Clip.Screens?.Length == 2) == 256;
            results.Add(new { attempt, published, files = files.Length, groups = groups.Length, allKnown, grouped, error = scanError?.ToString() });
            if (scanError is not null || files.Length != 2512 || groups.Length != 2256 || !allKnown || !grouped)
            {
                failure = scanError ?? new InvalidOperationException("Сверка должна сохранить старые файлы и все новые группы экранов."); break;
            }
        }
        await File.WriteAllTextAsync(Path.Combine(diagnostics, "catalog-concurrency.json"), JsonSerializer.Serialize(new { run, passed = failure is null, cases = results }, new JsonSerializerOptions { WriteIndented = true }));
        if (failure is not null) throw new InvalidOperationException("Сохранение во время завершения сверки не должно срывать обновление каталога.", failure);
    }
}
