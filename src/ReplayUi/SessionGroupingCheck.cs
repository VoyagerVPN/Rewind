using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Rewind.Preview;

internal static class SessionGroupingCheck
{
    public static async Task RunAsync(string diagnostics)
    {
        string folder = Path.Combine(diagnostics, "session-groups-" + Guid.NewGuid().ToString("N"));
        string archive = Path.Combine(folder, "archive"); Directory.CreateDirectory(archive);
        DateTime today = DateTime.Now.Date;
        var definitions = new[]
        {
            (App: "Game A", File: "a-first", At: today.AddDays(-1).AddHours(23).AddMinutes(40), Id: "run-a", Start: today.AddDays(-1).AddHours(23)),
            (App: "Game A", File: "a-midnight", At: today.AddMinutes(20), Id: "run-a", Start: today.AddDays(-1).AddHours(23)),
            (App: "Game A", File: "a-last", At: today.AddHours(1).AddMinutes(10), Id: "run-a", Start: today.AddDays(-1).AddHours(23)),
            (App: "Game A", File: "a-after-pause", At: today.AddHours(5).AddMinutes(30), Id: "run-a", Start: today.AddDays(-1).AddHours(23)),
            (App: "Game A", File: "a-restarted", At: today.AddHours(5).AddMinutes(40), Id: "run-b", Start: today.AddHours(5).AddMinutes(35)),
            (App: "Game B", File: "b-only", At: today.AddMinutes(30), Id: "run-c", Start: today)
        };
        string fixture = Path.GetFullPath(Path.Combine(diagnostics, "..", "filmora-qa", "three_audio.mp4"));
        foreach (var item in definitions)
        {
            string directory = Path.Combine(archive, item.App); Directory.CreateDirectory(directory);
            string file = Path.Combine(directory, item.File + ".mp4"); File.Copy(fixture, file);
            File.SetLastWriteTimeUtc(file, item.At.ToUniversalTime());
            JsonObject metadata = JsonSerializer.SerializeToNode(new SavedReplay(file, "", 8, 640, 360, 30, 8, 8, 0, new FileInfo(file).Length, AudioSources: []))!.AsObject();
            metadata["ApplicationName"] = item.App; metadata["SessionId"] = item.Id;
            metadata["SessionStartedUtc"] = item.Start.ToUniversalTime(); metadata["SavedAtUtc"] = item.At.ToUniversalTime();
            File.WriteAllText(Path.ChangeExtension(file, ".json"), metadata.ToJsonString());
        }
        string legacy = Path.Combine(archive, "Legacy Game"); Directory.CreateDirectory(legacy);
        DateTime[] oldTimes = [today.AddDays(-2).AddHours(23).AddMinutes(40), today.AddDays(-1).AddMinutes(20), today.AddDays(-1).AddHours(1).AddMinutes(10), today.AddDays(-1).AddHours(4)];
        for (int index = 0; index < oldTimes.Length; index++)
        {
            string file = Path.Combine(legacy, $"old-{index}.mp4"); File.Copy(fixture, file); File.SetLastWriteTimeUtc(file, oldTimes[index].ToUniversalTime());
            File.WriteAllText(Path.ChangeExtension(file, ".json"), JsonSerializer.Serialize(new SavedReplay(file, "", 8, 640, 360, 30, 8, 8, 0, new FileInfo(file).Length, AudioSources: [])));
        }
        CaptureMonitor primary = MonitorCatalog.Read().First(monitor => monitor.Primary);
        new CaptureSettingsFile(Path.Combine(folder, "capture-settings.json")).Save(new CaptureSettings(primary.DeviceName, 30, 120, 60, folder,
            primary.Identity, CaptureSystemAudio: false, CaptureMicrophone: false, SeparateDiscord: false));
        new HotkeySettingsFile(Path.Combine(folder, "hotkeys.json")).Save(new(null, null, null));
        var window = new OverlayWindow([], folder);
        try
        {
            await window.AddLibraryFolderAsync(archive);
            ((Button)window.FindName("SettingsButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            ((ComboBox)window.FindName("RecentGroupingPicker")).SelectedIndex = 2;
            FrameworkElement[] groups = Descendants((DependencyObject)window.FindName("RecentItems")).OfType<FrameworkElement>()
                .Where(element => System.Windows.Automation.AutomationProperties.GetName(element) == "Сессия повторов").ToArray();
            if (groups.Length < 3 || Descendants(groups[1]).OfType<Button>().Count() != 4 ||
                !Descendants(groups[1]).OfType<TextBlock>().Any(text => text.Text.Contains("23:00", StringComparison.Ordinal) && text.Text.Contains("05:30", StringComparison.Ordinal)))
                throw new InvalidOperationException("Один запуск должен объединять повторы 23:40, 00:20 и 01:10; новый запуск и другое приложение остаются отдельными сессиями.");
            if (groups.Length != 5 || groups.Count(group => Descendants(group).OfType<TextBlock>().Any(text => text.Text.StartsWith("≈ ", StringComparison.Ordinal))) != 2 ||
                Descendants(groups[^1]).OfType<Button>().Count() != 3)
                throw new InvalidOperationException("Старые повторы через полночь должны объединяться по паузе до 90 минут; большая пауза разделяет сессии, обе отмечены как приблизительные.");
            await Task.Delay(400);
            var reopened = new OverlayWindow([], folder);
            try { if (((ComboBox)reopened.FindName("RecentGroupingPicker")).SelectedIndex != 2) throw new InvalidOperationException("Выбор группировки по сессиям должен восстанавливаться после перезапуска."); }
            finally { reopened.Close(); }
            File.WriteAllText(Path.Combine(diagnostics, "session-grouping-check.json"), JsonSerializer.Serialize(new
            { folder, result = "PASS application grouping, midnight continuity, long gap in same process, separate launches, approximate legacy sessions, grouping reload" }));
        }
        finally { window.Close(); }
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        foreach (object value in LogicalTreeHelper.GetChildren(parent))
            if (value is DependencyObject child) { yield return child; foreach (DependencyObject next in Descendants(child)) yield return next; }
    }
}
