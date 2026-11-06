using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Security.Cryptography;

namespace Rewind.Preview;

internal static class VideoProbeCacheCheck
{
    private sealed record Result(string Phase, bool Opened, bool DamageConfirmed, string? Error, double OpenMs, double? ProbeMs, double? MpvMs, bool? ProbeCached);
    public static async Task RunAsync(string diagnostics, string source)
    {
        string run = Path.Combine(diagnostics, "video-probe-cache-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(run);
        Environment.SetEnvironmentVariable("REWIND_VIDEO_TRACE_FOLDER", run);
        string file = Path.Combine(run, "fixture.mp4"); File.Copy(source, file);
        if (new FileInfo(file).Length > 128 * 1024 * 1024) throw new InvalidOperationException("Для mutation-проверки нужен небольшой собственный файл до128MiB.");
        byte[] original = await File.ReadAllBytesAsync(file);
        string sourceHash = Convert.ToHexString(SHA256.HashData(original));
        DateTime modified = File.GetLastWriteTimeUtc(file);
        var results = new List<Result>();
        async Task<Result> OpenAsync(string phase, long position = 0, bool damaged = false)
        {
            var video = new VideoProcess(); var timer = Stopwatch.StartNew(); bool opened = false;
            string? error = null; bool confirmed = false;
            try
            {
                await video.OpenAsync(file, 0, position, true, CancellationToken.None);
                await video.WaitUntilReadyAsync(CancellationToken.None); opened = true;
            }
            catch (InvalidOperationException)
            { error = video.Error; confirmed = video.FailureMayIndicateDamage; if (!damaged) throw; }
            finally
            {
                double milliseconds = timer.Elapsed.TotalMilliseconds;
                int? pid = video.ProcessId; await video.StopAsync();
                JsonElement? timing = null;
                if (pid is int id && File.Exists(Path.Combine(run, "mpv-" + id + ".jsonl")))
                    foreach (string line in await File.ReadAllLinesAsync(Path.Combine(run, "mpv-" + id + ".jsonl")))
                    {
                        using JsonDocument entry = JsonDocument.Parse(line);
                        if (entry.RootElement.GetProperty("stage").GetString() == "open-stages") timing = entry.RootElement.Clone();
                    }
                results.Add(new(phase, opened, confirmed, error, milliseconds,
                    timing?.GetProperty("probeMs").GetDouble(), timing?.GetProperty("mpvMs").GetDouble(),
                    timing is JsonElement value && value.TryGetProperty("probeCached", out JsonElement hit) ? hit.GetBoolean() : null));
            }
            Result result = results[^1];
            if (damaged && (opened || !confirmed || error?.Contains("Этот участок не удалось воспроизвести", StringComparison.Ordinal) != true))
                throw new InvalidOperationException("Изменённый файл должен пройти строгую повторную проверку до mpv: " + phase);
            return result;
        }
        async Task ReplaceContentAsync(byte[] content)
        {
            await File.WriteAllBytesAsync(file, content); File.SetLastWriteTimeUtc(file, modified);
            if (new FileInfo(file).Length != original.Length || File.GetLastWriteTimeUtc(file) != modified)
                throw new InvalidOperationException("Mutation должна сохранить длину и LastWriteTime исходной версии.");
        }
        try
        {
            Result cold = await OpenAsync("cold");
            Result warm = await OpenAsync("unchanged-new-worker");
            Result other = await OpenAsync("different-position", 1000);
            using (var writer = new FileStream(file, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
            {
                Result activeWriter = await OpenAsync("active-writer-must-disable-cache");
                if (activeWriter.ProbeCached != false) throw new InvalidOperationException("Открытый writer должен запрещать повторное использование проверки.");
            }
            await ReplaceContentAsync(new byte[original.Length]);
            await OpenAsync("in-place-change-same-size-and-mtime", damaged: true);
            await OpenAsync("failed-probe-must-not-be-cached", damaged: true);
            await ReplaceContentAsync(original);
            Result restored = await OpenAsync("restored-in-place");
            string replacement = Path.Combine(run, "replacement.mp4");
            await File.WriteAllBytesAsync(replacement, new byte[original.Length]); File.SetLastWriteTimeUtc(replacement, modified);
            File.Move(replacement, file, overwrite: true);
            await OpenAsync("replacement-same-size-and-mtime", damaged: true);
            if (warm.ProbeCached != true || warm.ProbeMs is not double cached || cached > 15)
                throw new InvalidOperationException($"Неизменённый проверенный участок не должен повторно запускать полсекунды декодирования: {warm.ProbeMs:F1}мс.");
            if (cold.ProbeCached != false || other.ProbeCached != false || restored.ProbeCached != false || cold.ProbeMs is not > 15 || other.ProbeMs is not > 15 || restored.ProbeMs is not > 15)
                throw new InvalidOperationException("Новый участок и новая версия файла должны проходить настоящую строгую проверку.");
        }
        finally
        {
            bool sourceUnchanged = sourceHash == Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(source)));
            await File.WriteAllTextAsync(Path.Combine(diagnostics, "video-probe-cache.json"), JsonSerializer.Serialize(new
            { run, source, sourceHash, sourceUnchanged, results, measurement = "Real VideoProcess/VideoWorker + strict0.5s FFmpeg probe + headless mpv. New worker per open. Mutation fixtures only; source read-only. Component latency, not physical UI first-frame latency." }, new JsonSerializerOptions { WriteIndented = true }));
            if (!sourceUnchanged) throw new InvalidOperationException("Исходный файл изменился во время лаборатории.");
        }
    }
}
