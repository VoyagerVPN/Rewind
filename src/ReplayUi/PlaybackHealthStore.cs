using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Rewind.Preview;

internal sealed record PlaybackIssue(double PositionSeconds, string Kind, DateTime CheckedUtc);
internal sealed record PlaybackHealth(string File, long Bytes, DateTime ModifiedUtc, double? RecoveredStartSeconds, PlaybackIssue[] Issues, int Version = 1)
{
    public string Key => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{File.ToUpperInvariant()}\n{Bytes}\n{ModifiedUtc.Ticks}")));
}

// Результаты привязаны к версии исходника; оригинальный MP4 остаётся неизменным.
internal sealed class PlaybackHealthStore(string folder)
{
    private static readonly SemaphoreSlim Gate = new(1, 1);

    public static PlaybackHealth VersionOf(string file)
    {
        var info = new FileInfo(file);
        return new(info.FullName, info.Length, info.LastWriteTimeUtc, null, []);
    }
    public async Task<PlaybackHealth> ReadAsync(string file, CancellationToken cancellation)
    {
        PlaybackHealth version = VersionOf(file);
        await Gate.WaitAsync(cancellation);
        try { return await ReadVersionAsync(version, cancellation); }
        finally { Gate.Release(); }
    }
    private async Task<PlaybackHealth> ReadVersionAsync(PlaybackHealth version, CancellationToken cancellation)
    {
        string path = Path.Combine(folder, version.Key + ".json");
        if (!System.IO.File.Exists(path)) return version with { RecoveredStartSeconds = null, Issues = [] };
        PlaybackHealth? stored = JsonSerializer.Deserialize<PlaybackHealth>(await System.IO.File.ReadAllTextAsync(path, cancellation));
        if (stored is null || stored.Version != 1 || string.IsNullOrWhiteSpace(stored.File) || stored.Key != version.Key || stored.Issues is null ||
            stored.RecoveredStartSeconds is double start && (!double.IsFinite(start) || start < 0) ||
            stored.Issues.Any(issue => issue is null || !double.IsFinite(issue.PositionSeconds) || issue.PositionSeconds < 0))
            throw new InvalidDataException("Некорректные метаданные воспроизведения.");
        return stored;
    }
    public async Task SaveAsync(PlaybackHealth version, IReadOnlyList<double> failures, double? recoveredStart = null, string failureKind = "unavailable-at-position", bool invalidateKnownStart = true)
    {
        await Gate.WaitAsync();
        string? temporary = null;
        try
        {
            PlaybackHealth current;
            try { current = await ReadVersionAsync(version, CancellationToken.None); }
            catch (Exception error) when (error is JsonException or InvalidDataException)
            {
                // Неверное содержимое кэша не блокирует запись новых проверенных сведений. I/O-ошибки не маскируем.
                current = version;
            }
            var issues = current.Issues.ToList();
            foreach (double seconds in failures.Where(value => double.IsFinite(value) && value >= 0))
            {
                issues.RemoveAll(issue => issue.Kind == failureKind && Math.Abs(issue.PositionSeconds - seconds) < .005);
                issues.Add(new(seconds, failureKind, DateTime.UtcNow));
            }
            // Не объявляем промежутки между пробами повреждёнными: проверены только отдельные точки.
            double? start = recoveredStart ?? current.RecoveredStartSeconds;
            if (invalidateKnownStart && recoveredStart is null && start is double cached && failures.Any(seconds => Math.Abs(seconds - cached) < .005)) start = null;
            var updated = current with { RecoveredStartSeconds = start, Issues = issues.OrderByDescending(issue => issue.CheckedUtc).Take(128).ToArray() };
            Directory.CreateDirectory(folder);
            string path = Path.Combine(folder, version.Key + ".json");
            temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            await System.IO.File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(updated, new JsonSerializerOptions { WriteIndented = true }));
            System.IO.File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            try { if (temporary is not null) System.IO.File.Delete(temporary); }
            finally { Gate.Release(); }
        }
    }
}
