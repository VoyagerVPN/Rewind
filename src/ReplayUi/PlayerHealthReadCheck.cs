using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;

namespace Rewind.Preview;

internal static class PlayerHealthReadCheck
{
    public static async Task RunAsync(string diagnostics)
    {
        string run = Path.Combine(diagnostics, "player-health-read-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(run);
        string a = Path.Combine(run, "first.mp4"), b = Path.Combine(run, "second.mp4");
        File.Copy(Path.Combine(diagnostics, "player-timed-fixture.mp4"), a); File.Copy(a, b);
        string healthFolder = Path.Combine(run, "library-cache", "playback-health");
        var store = new PlaybackHealthStore(healthFolder);
        PlaybackHealth first = await store.ReadAsync(a, CancellationToken.None);
        await store.SaveAsync(first, [2], recoveredStart: 12);
        PlaybackHealth second = await store.ReadAsync(b, CancellationToken.None);
        await File.WriteAllTextAsync(Path.Combine(healthFolder, second.Key + ".json"), "{");
        using var telemetry = new UiTelemetry(Dispatcher.CurrentDispatcher, run, () => false);
        var player = new ReplayPlayerView(telemetry, run, headless: true);
        var host = new Window { Content = player, Width = 1000, Height = 700, Left = -20000, Top = -20000,
            ShowActivated = false, ShowInTaskbar = false, WindowStyle = WindowStyle.None };
        var results = new List<object>();
        Clip ClipFor(string file) => new(Path.GetFileName(file), "", file, "", [null, null, null], 24, 30);
        try
        {
            host.Show();
            await player.LoadClipAsync(ClipFor(a), autoPlay: false);
            results.Add(new { phase = "first-known-start", video = player.CurrentVideo, position = player.PositionSeconds, nativePosition = player.VideoPositionSeconds, error = player.HasPlaybackError });
            if (player.HasPlaybackError || Math.Abs(player.PositionSeconds - 12) > .15) throw new InvalidOperationException("Контрольный клип должен открыться с собственного известного начала12с.");
            await player.LoadClipAsync(ClipFor(b), autoPlay: false);
            results.Add(new { phase = "second-corrupt-health", video = player.CurrentVideo, position = player.PositionSeconds, nativePosition = player.VideoPositionSeconds, error = player.HasPlaybackError });
            if (player.HasPlaybackError || player.CurrentVideo != b || player.PositionSeconds > .15)
                throw new InvalidOperationException($"Следующий исправный клип с нечитаемым кэшем должен начинаться с0, а не с состояния предыдущего: {player.PositionSeconds:F3}с.");
            await player.LoadClipAsync(ClipFor(a), autoPlay: false);
            if (Math.Abs(player.PositionSeconds - 12) > .15) throw new InvalidOperationException("Не восстановился контроль12с перед сменой версии.");
            await File.WriteAllTextAsync(Path.Combine(healthFolder, first.Key + ".json"), "{");
            await player.LoadClipAsync(ClipFor(a), autoPlay: false);
            results.Add(new { phase = "same-version-corrupt-cache-keeps-memory", position = player.PositionSeconds, error = player.HasPlaybackError });
            if (player.HasPlaybackError || Math.Abs(player.PositionSeconds - 12) > .15) throw new InvalidOperationException("Неизменённая версия должна сохранить собственное проверенное начало при временной ошибке кэша.");
            File.SetLastWriteTimeUtc(a, File.GetLastWriteTimeUtc(a).AddSeconds(1));
            PlaybackHealth changed = await store.ReadAsync(a, CancellationToken.None);
            if (changed.Key == first.Key) throw new InvalidOperationException("Тест не сменил версию файла.");
            await File.WriteAllTextAsync(Path.Combine(healthFolder, changed.Key + ".json"), "{");
            await player.LoadClipAsync(ClipFor(a), autoPlay: false);
            results.Add(new { phase = "same-path-new-version-corrupt-health", video = player.CurrentVideo, position = player.PositionSeconds, nativePosition = player.VideoPositionSeconds, error = player.HasPlaybackError });
            if (player.HasPlaybackError || player.PositionSeconds > .15)
                throw new InvalidOperationException($"Новая версия файла не должна наследовать начало старой версии при ошибке кэша: {player.PositionSeconds:F3}с.");
            await File.WriteAllTextAsync(Path.Combine(healthFolder, second.Key + ".json"), JsonSerializer.Serialize(new
            { File = (string?)null, second.Bytes, second.ModifiedUtc, RecoveredStartSeconds = (double?)null, Issues = Array.Empty<PlaybackIssue>(), Version = 1 }));
            Exception? schemaError = null;
            try { await player.LoadClipAsync(ClipFor(b), autoPlay: false); } catch (Exception error) { schemaError = error; }
            results.Add(new { phase = "null-file-in-health-json", position = player.PositionSeconds, error = player.HasPlaybackError, exception = schemaError?.GetType().Name });
            if (schemaError is not null || player.HasPlaybackError || player.PositionSeconds > .15)
                throw new InvalidOperationException("Неверная структура health JSON не должна мешать открытию исправного клипа.", schemaError);
            await store.SaveAsync(second, [3], recoveredStart: 4);
            PlaybackHealth repaired = await store.ReadAsync(b, CancellationToken.None);
            results.Add(new { phase = "corrupt-cache-repaired-by-save", start = repaired.RecoveredStartSeconds, issues = repaired.Issues.Length });
            if (repaired.RecoveredStartSeconds != 4 || repaired.Issues.Single().PositionSeconds != 3)
                throw new InvalidOperationException("Следующее сохранение должно исправить повреждённый кэш собственной версии файла.");
            await player.LoadClipAsync(ClipFor(b), autoPlay: false);
            results.Add(new { phase = "reopened-repaired-cache", position = player.PositionSeconds, error = player.HasPlaybackError });
            if (player.HasPlaybackError || Math.Abs(player.PositionSeconds - 4) > .15) throw new InvalidOperationException("Плеер должен использовать собственное восстановленное начало4с.");
            string repairedPath = Path.Combine(healthFolder, second.Key + ".json");
            string beforeLock = await File.ReadAllTextAsync(repairedPath);
            bool denied = false;
            using (var locked = new FileStream(repairedPath, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                try { await store.SaveAsync(repaired, [], recoveredStart: 8); }
                catch (IOException) { denied = true; }
            }
            bool unchanged = beforeLock == await File.ReadAllTextAsync(repairedPath);
            results.Add(new { phase = "write-denial-not-masked", denied, unchanged });
            if (!denied || !unchanged) throw new InvalidOperationException("Отказ доступа должен остаться I/O-ошибкой и сохранить существующий кэш.");
            await File.WriteAllTextAsync(repairedPath, "{");
            await store.SaveAsync(repaired, [], recoveredStart: 6);
            PlaybackHealth syntaxRepaired = await store.ReadAsync(b, CancellationToken.None);
            results.Add(new { phase = "syntax-error-repaired", start = syntaxRepaired.RecoveredStartSeconds, issues = syntaxRepaired.Issues.Length });
            if (syntaxRepaired.RecoveredStartSeconds != 6 || syntaxRepaired.Issues.Length != 1) throw new InvalidOperationException("Восстановление после синтаксической ошибки должно сохранять новые проверенные сведения.");
        }
        finally
        {
            await File.WriteAllTextAsync(Path.Combine(diagnostics, "player-health-read.json"), JsonSerializer.Serialize(new
            { run, results, measurement = "Real ReplayPlayerView same instance; separate files, same-path new version, malformed and null-file health JSON, retained same-version state, repaired saves and real exclusive cache lock; paused native position; own .cache fixtures only." }, new JsonSerializerOptions { WriteIndented = true }));
            await player.ClosePlayerAsync(); host.Close();
        }
    }
}
