using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows.Threading;

namespace Rewind.Preview;

internal static class CatalogCacheCheck
{
    public static async Task RunAsync(string diagnostics)
    {
        diagnostics = Path.GetFullPath(diagnostics);
        string run = Path.Combine(diagnostics, "catalog-cache-" + Guid.NewGuid().ToString("N"));
        string archive = Path.Combine(run, "Clips"), cache = Path.Combine(run, "cache");
        Directory.CreateDirectory(archive); Directory.CreateDirectory(cache);
        string video = Path.Combine(archive, "healthy.mp4");
        File.Copy(Path.Combine(diagnostics, "..", "filmora-qa", "three_audio.mp4"), video);
        string sourceHash = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(video)));
        using var telemetry = new UiTelemetry(Dispatcher.CurrentDispatcher, run, () => false);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        string cacheFile = Path.Combine(cache, "catalog-v1.json");
        using (var initial = new ReplayLibraryIndex(cache, telemetry))
        {
            LibraryEntry[] found = await initial.ScanAsync([archive], [], timeout.Token);
            if (found.Length != 1 || found[0].Clip.Video != video) throw new InvalidOperationException("Исходная сверка должна найти здоровый файл.");
        }
        var results = new List<object>();
        try
        {
            await File.WriteAllTextAsync(cacheFile, "{\"Version\":1,\"Files\":null}", timeout.Token);
            using (var reopened = new ReplayLibraryIndex(cache, telemetry))
            {
                LibraryEntry[] beforeScan = reopened.ReadSnapshot([archive], []);
                if (beforeScan.Length != 0) throw new InvalidOperationException("Некорректный снимок не должен создавать фиктивные записи.");
                LibraryEntry[] recovered = await reopened.ScanAsync([archive], [], timeout.Token);
                if (recovered.Length != 1 || recovered[0].Clip.Video != video) throw new InvalidOperationException("После неверной схемы кэша сверка должна восстановить реальный файл.");
            }
            using (var persisted = new ReplayLibraryIndex(cache, telemetry))
                if (persisted.ReadSnapshot([archive], []).Single().Clip.Video != video)
                    throw new InvalidOperationException("Восстановленный снимок должен сохраняться для следующего запуска.");
            if (Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(video))) != sourceHash)
                throw new InvalidOperationException("Восстановление кэша не должно изменять видео.");
            results.Add(new { phase = "null-file-list-recovers", passed = true, sourceUnchanged = true });
            using (JsonDocument valid = JsonDocument.Parse(await File.ReadAllTextAsync(cacheFile, timeout.Token)))
            {
                JsonElement healthy = valid.RootElement.GetProperty("Files")[0].Clone();
                await File.WriteAllTextAsync(cacheFile, JsonSerializer.Serialize(new { Version = 1, Files = new object?[] { null, healthy } }), timeout.Token);
            }
            using (var mixed = new ReplayLibraryIndex(cache, telemetry))
            {
                LibraryEntry[] available = mixed.ReadSnapshot([archive], []);
                if (available.Length != 1 || available[0].Clip.Video != video)
                    throw new InvalidOperationException("Повреждённая запись не должна скрывать следующий здоровый элемент снимка.");
                LibraryEntry[] rebuilt = await mixed.ScanAsync([archive], [], timeout.Token);
                if (rebuilt.Length != 1 || rebuilt[0].Clip.Video != video)
                    throw new InvalidOperationException("Сверка должна переписать смешанный повреждённый снимок.");
            }
            using (var persisted = new ReplayLibraryIndex(cache, telemetry))
                if (persisted.ReadSnapshot([archive], []).Single().Clip.Video != video)
                    throw new InvalidOperationException("После восстановления смешанного снимка должен открываться здоровый кэш.");
            results.Add(new { phase = "null-entry-keeps-valid-snapshot", passed = true });
            string beforeDeniedRead = await File.ReadAllTextAsync(cacheFile, timeout.Token);
            var cacheInfo = new FileInfo(cacheFile);
            FileSecurity originalAccess = cacheInfo.GetAccessControl(AccessControlSections.Access);
            var deniedAccess = new FileSecurity();
            deniedAccess.SetSecurityDescriptorBinaryForm(originalAccess.GetSecurityDescriptorBinaryForm(), AccessControlSections.Access);
            using WindowsIdentity identity = WindowsIdentity.GetCurrent();
            SecurityIdentifier user = identity.User ?? throw new InvalidOperationException("Не определён пользователь лаборатории.");
            deniedAccess.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.ReadData, AccessControlType.Deny));
            using (var denied = new ReplayLibraryIndex(cache, telemetry))
            {
                try
                {
                    cacheInfo.SetAccessControl(deniedAccess);
                    bool deniedByWindows = false;
                    try { _ = File.ReadAllText(cacheFile); }
                    catch (UnauthorizedAccessException) { deniedByWindows = true; }
                    if (!deniedByWindows) throw new InvalidOperationException("Отказ чтения кэша не воспроизведён.");
                    if (denied.ReadSnapshot([archive], []).Length != 0)
                        throw new InvalidOperationException("Недоступный снимок не должен создавать записи.");
                }
                finally
                {
                    // Новый дескриптор отмечает DACL изменённой и действительно записывает откат.
                    var restoredAccess = new FileSecurity();
                    restoredAccess.SetSecurityDescriptorBinaryForm(originalAccess.GetSecurityDescriptorBinaryForm(), AccessControlSections.Access);
                    cacheInfo.SetAccessControl(restoredAccess);
                    if (File.ReadAllText(cacheFile) != beforeDeniedRead)
                        throw new InvalidOperationException("Восстановление доступа к тестовому файлу не подтверждено.");
                }
                if (await File.ReadAllTextAsync(cacheFile, timeout.Token) != beforeDeniedRead)
                    throw new InvalidOperationException("Неудачное чтение не должно менять содержимое снимка.");
                if ((await denied.ScanAsync([archive], [], timeout.Token)).Single().Clip.Video != video)
                    throw new InvalidOperationException("После отказа кэша сверка должна найти здоровый файл.");
            }
            results.Add(new { phase = "denied-cache-read-recovers", passed = true, permissionsRestored = true });
            foreach (string corruption in new[] { "null-clip", "null-video", "blank-video" })
            {
                JsonNode document = JsonNode.Parse(await File.ReadAllTextAsync(cacheFile, timeout.Token))!;
                JsonNode healthy = document["Files"]![0]!.DeepClone(), damaged = healthy.DeepClone();
                if (corruption == "null-clip") damaged["Clip"] = null;
                else damaged["Clip"]!["Video"] = corruption == "null-video" ? null : JsonValue.Create(" ");
                document["Files"] = new JsonArray(damaged, healthy);
                await File.WriteAllTextAsync(cacheFile, document.ToJsonString(), timeout.Token);
                using var partial = new ReplayLibraryIndex(cache, telemetry);
                if (partial.ReadSnapshot([archive], []).Single().Clip.Video != video)
                    throw new InvalidOperationException("Невалидный ключ не должен скрывать здоровую часть снимка: " + corruption);
                if ((await partial.ScanAsync([archive], [], timeout.Token)).Single().Clip.Video != video)
                    throw new InvalidOperationException("Сверка должна восстановить каталог после неверного ключа: " + corruption);
                results.Add(new { phase = corruption + "-keeps-valid-snapshot", passed = true });
            }
            string ui = Path.Combine(run, "overlay-data"); Directory.CreateDirectory(Path.Combine(ui, "library-cache"));
            await File.WriteAllTextAsync(Path.Combine(ui, "library-cache", "catalog-v1.json"), "{\"Version\":1,\"Files\":null}", timeout.Token);
            CaptureMonitor monitor = MonitorCatalog.Read().First(item => item.Primary);
            new CaptureSettingsFile(Path.Combine(ui, "capture-settings.json")).Save(new CaptureSettings(monitor.DeviceName, 30, 120, 60, run, monitor.Identity,
                CaptureSystemAudio: false, CaptureMicrophone: false, SeparateDiscord: false, CaptureVideo: false));
            new HotkeySettingsFile(Path.Combine(ui, "hotkeys.json")).Save(new(null, null, null));
            await File.WriteAllTextAsync(Path.Combine(ui, "library-settings.json"), JsonSerializer.Serialize(new LibraryPreferences([])), timeout.Token);
            var window = new OverlayWindow([], ui);
            try
            {
                await window.OpenLibraryAsync();
                var cards = (System.Windows.Controls.WrapPanel)window.FindName("LibraryItems");
                if (cards.Children.OfType<System.Windows.Controls.Button>().Count() != 1)
                    throw new InvalidOperationException("Оверлей с повреждённым кэшем должен загрузиться и показать настоящий клип.");
            }
            finally { window.Close(); }
            if (Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(video))) != sourceHash)
                throw new InvalidOperationException("Восстановление каталога и UI не должно менять видео.");
            results.Add(new { phase = "overlay-initialization-and-library-recover", passed = true, sourceUnchanged = true });



            await File.WriteAllTextAsync(Path.Combine(diagnostics, "catalog-cache.json"), JsonSerializer.Serialize(new { run, passed = true, cases = results }, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception error)
        {
            await File.WriteAllTextAsync(Path.Combine(diagnostics, "catalog-cache.json"), JsonSerializer.Serialize(new { run, passed = false, cases = results, error = error.ToString() }, new JsonSerializerOptions { WriteIndented = true }));
            throw;
        }
    }
}
