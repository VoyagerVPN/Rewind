using System.IO;
using System.Diagnostics;
using System.IO.Pipes;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using Rewind.Preview;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "/S") return;
        if (args is ["--default-language", string culture, string expected])
        {
            System.Globalization.CultureInfo.CurrentUICulture = System.Globalization.CultureInfo.GetCultureInfo(culture);
            string absent = Path.Combine(Path.GetTempPath(), "rewind-absent-" + Guid.NewGuid().ToString("N"), "language.json");
            if (new LanguagePreferencesFile(absent).Load() != expected) throw new InvalidOperationException("Incorrect system-language default for " + culture);
            return;
        }
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(app.Dispatcher));
        ushort systemLanguage = GetUserDefaultUILanguage();
        string directory = Path.Combine(Path.GetTempPath(), "rewind-language-ui-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            new LanguagePreferencesFile(Path.Combine(directory, "language.json")).Save("ru");
            new HotkeySettingsFile(Path.Combine(directory, "hotkeys.json")).Save(new HotkeySettings(null, null, null)
                { CaptureToggle = null, Settings = null, Player = new PlayerHotkeySettings(null, null, null, null, null) { GoToStart = null, Delete = null } });
            Environment.SetEnvironmentVariable("REWIND_TEST_NO_ACTIVATE", "1");
            var panel = new OverlayWindow([], directory);
            var status = (TextBlock)panel.FindName("CaptureStatusText");
            var language = (ComboBox)panel.FindName("InterfaceLanguagePicker");
            var grouping = (ComboBox)panel.FindName("RecentGroupingPicker");
            grouping.SelectedIndex = 2;
            panel.ShowSettingsSection("replay");
            if (status.Text != "Запись выключена" || language.SelectedIndex != 1)
                throw new InvalidOperationException("The saved Russian language must be applied on launch.");
            if (GetThreadUILanguage() != 0x0419) throw new InvalidOperationException("Russian must be preferred for native dialogs on the app's UI thread.");
            using (new FileStream(Path.Combine(directory, "language.json"), FileMode.Open, FileAccess.Read, FileShare.None))
            {
                language.SelectedIndex = 0;
                if (language.SelectedIndex != 1 || ((TextBlock)panel.FindName("SettingsMessageText")).Text is not string message ||
                    !message.StartsWith("Не удалось сохранить язык:", StringComparison.Ordinal))
                    throw new InvalidOperationException("A failed save must keep the selected language and report the error.");
            }
            nint foreground = GetForegroundWindow();
            var showToast = typeof(OverlayWindow).GetMethod("ShowLocalizedActionToast", BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("Localized notifications are unavailable.");
            showToast.Invoke(panel, ["language-test", "Message.SavingReplay", (Func<string>)(() => Rewind.Preview.Localization.Text("Message.PreparingVideo")),
                Wpf.Ui.Controls.SymbolRegular.Save24, null, false, true, null]);
            Window toast = app.Windows.OfType<Window>().Single(window => window.FindName("ToastTitle") is TextBlock title && title.Text == "Сохранение повтора…");
            if (GetForegroundWindow() != foreground) throw new InvalidOperationException("A notification must not activate another window.");
            language.SelectedIndex = 0;
            if (status.Text != "Recording is off" || (string)app.FindResource("Settings.Language") != "Language" ||
                ((TextBlock)panel.FindName("SettingsLabel")).Text != "Back" || grouping.SelectedIndex != 2 ||
                grouping.Items[2].ToString() != "By session")
                throw new InvalidOperationException("Changing the language must immediately update both dynamic and static interface text.");
            if (((TextBlock)toast.FindName("ToastTitle")).Text != "Saving replay…" ||
                ((TextBlock)toast.FindName("ToastDetail")).Text != "Preparing video")
                throw new InvalidOperationException("An existing notification must change language without being dismissed.");
            if (GetThreadUILanguage() != 0x0409 || GetUserDefaultUILanguage() != systemLanguage)
                throw new InvalidOperationException("Switching the app language must set the UI thread preference without changing the Windows language.");
            panel.Close();
            var restarted = new OverlayWindow([], directory);
            try
            {
                if (((ComboBox)restarted.FindName("InterfaceLanguagePicker")).SelectedIndex != 0 ||
                    ((TextBlock)restarted.FindName("CaptureStatusText")).Text != "Recording is off")
                    throw new InvalidOperationException("The selected English language must survive an application restart.");
            }
            finally { restarted.Close(); }
            Console.WriteLine("PASS language_changes_live_and_survives_restart");
            CheckSetupLanguage(directory, preserveExisting: false);
            CheckSetupLanguage(directory, preserveExisting: true);
            CheckSystemDefaults();
            CheckVideoWorkerLanguage("en", null, "The video player could not complete the action");
            CheckVideoWorkerLanguage("ru", null, "Видеоплеер не смог завершить действие");
            CheckVideoWorkerLanguage("en", "ru", "Видеоплеер не смог завершить действие");
            CheckVideoWorkerLanguage("ru", "en", "The video player could not complete the action");
        }
        finally { app.Shutdown(); Directory.Delete(directory, true); }
    }

    private static void CheckSystemDefaults()
    {
        foreach (var (culture, expected) in new[] { ("ru-RU", "ru"), ("en-GB", "en"), ("ja-JP", "en") })
        {
            var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            start.ArgumentList.Add("--default-language"); start.ArgumentList.Add(culture); start.ArgumentList.Add(expected);
            using Process child = Process.Start(start) ?? throw new InvalidOperationException("Could not start the system-language check.");
            if (!child.WaitForExit(15000)) { child.Kill(entireProcessTree: true); throw new TimeoutException("System-language check timed out."); }
            if (child.ExitCode != 0) throw new InvalidOperationException(child.StandardError.ReadToEnd());
        }
        Console.WriteLine("PASS supported_system_languages_and_english_fallback");
    }

    [DllImport("kernel32.dll")] private static extern ushort GetThreadUILanguage();
    [DllImport("kernel32.dll")] private static extern ushort GetUserDefaultUILanguage();
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern nint GetWindowLongPtr(nint handle, int index);

    private static void CheckSetupLanguage(string root, bool preserveExisting)
    {
        string fixture = Path.Combine(root, preserveExisting ? "update" : "first-install");
        string install = Path.Combine(fixture, "program"), data = Path.Combine(fixture, "data"), replays = Path.Combine(fixture, "replays");
        Directory.CreateDirectory(Path.Combine(install, "tools"));
        File.WriteAllText(Path.Combine(install, "tools", "ffmpeg.exe"), "fixture");
        File.WriteAllText(Path.Combine(install, "tools", "ffprobe.exe"), "fixture");
        if (preserveExisting) new LanguagePreferencesFile(Path.Combine(data, "language.json")).Save("ru");
        string defaults = Path.Combine(fixture, "defaults.txt");
        File.WriteAllLines(defaults, [install, data, replays]);
        var constructor = typeof(SetupWindow).GetConstructor(BindingFlags.Instance | BindingFlags.NonPublic, null, [typeof(string), typeof(string)], null)
            ?? throw new InvalidOperationException("Setup window constructor was not found.");
        string? previousNoActivate = Environment.GetEnvironmentVariable("REWIND_TEST_NO_ACTIVATE");
        Environment.SetEnvironmentVariable("REWIND_TEST_NO_ACTIVATE", "1");
        var window = (Window)constructor.Invoke([Environment.ProcessPath!, defaults]);
        try
        {
            if (window.ShowActivated) throw new InvalidOperationException("Setup inspection must not change the foreground window.");
            nint handle = new WindowInteropHelper(window).EnsureHandle();
            if ((GetWindowLongPtr(handle, -20).ToInt64() & 0x08000000) == 0)
                throw new InvalidOperationException("A setup inspection window must be protected against activation.");
            var selector = (ComboBox)window.FindName("LanguagePicker");
            var installButton = (Button)window.FindName("InstallButton");
            var dataInput = (TextBox)window.FindName("DataDirectory");
            var status = (TextBlock)window.FindName("Status");
            selector.SelectedIndex = 0;
            dataInput.Text = "invalid";
            installButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            if (status.Text != "Choose three different folders on local drives.") throw new InvalidOperationException("Setup validation must use the selected language.");
            selector.SelectedIndex = 1;
            if (status.Text != "Укажите три разные папки на локальных дисках.") throw new InvalidOperationException("An existing setup error must change language immediately.");
            dataInput.Text = data;
            selector.SelectedIndex = 0;
            installButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var frame = new DispatcherFrame();
            var watch = Stopwatch.StartNew();
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(20) };
            timer.Tick += (_, _) =>
            {
                if (((FrameworkElement)window.FindName("Complete")).Visibility == Visibility.Visible || watch.Elapsed > TimeSpan.FromSeconds(15))
                { timer.Stop(); frame.Continue = false; }
            };
            timer.Start(); Dispatcher.PushFrame(frame);
            if (((FrameworkElement)window.FindName("Complete")).Visibility != Visibility.Visible)
                throw new InvalidOperationException("Setup did not complete: " + ((TextBlock)window.FindName("Status")).Text);
            string actual = new LanguagePreferencesFile(Path.Combine(data, "language.json")).Load("fr-FR");
            if (actual != (preserveExisting ? "ru" : "en"))
                throw new InvalidOperationException("Setup must pass the first-install choice and preserve a previous explicit choice on update.");
        }
        finally { window.Close(); Environment.SetEnvironmentVariable("REWIND_TEST_NO_ACTIVATE", previousNoActivate); }
        Console.WriteLine(preserveExisting ? "PASS setup_preserves_existing_language" : "PASS setup_passes_selected_language_to_app");
    }

    private static void CheckVideoWorkerLanguage(string initial, string? changed, string expected)
    {
        string name = "rewind-language-check-" + Guid.NewGuid().ToString("N");
        using var pipe = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        var start = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "Rewind.exe"))
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add("--video-worker"); start.ArgumentList.Add(name); start.ArgumentList.Add(Environment.ProcessId.ToString());
        start.Environment["REWIND_UI_LANGUAGE"] = initial;
        using Process worker = Process.Start(start) ?? throw new InvalidOperationException("Could not start the test video worker.");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        try
        {
            pipe.WaitForConnectionAsync(timeout.Token).GetAwaiter().GetResult();
            using var reader = new StreamReader(pipe, leaveOpen: true);
            using var writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };
            if (changed is not null) writer.WriteLine(JsonSerializer.Serialize(new { Kind = "language", Language = changed }));
            writer.WriteLine("{\"Kind\":\"open\",\"Handle\":0,\"Headless\":false}");
            string line = reader.ReadLineAsync(timeout.Token).AsTask().GetAwaiter().GetResult() ?? throw new InvalidOperationException("Video worker closed without an error response.");
            using JsonDocument message = JsonDocument.Parse(line);
            string? error = message.RootElement.GetProperty("Error").GetString();
            if (error is null || !error.StartsWith(expected, StringComparison.Ordinal))
                throw new InvalidOperationException("The video worker must apply a language change before reporting the next error: " + error);
            Console.WriteLine("PASS video_worker_applies_language_changes_before_errors");
        }
        finally { if (!worker.HasExited) { worker.Kill(entireProcessTree: true); worker.WaitForExit(); } }
    }
}
