using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;

namespace Rewind.Preview;

public partial class SetupWindow : Window
{
    private readonly string _installer;
    private readonly string _optionsFile;
    private bool _installing;
    private int _result = 1;
    private Func<string>? _statusText;

    private sealed class SetupFailure(string key, params object?[] arguments) : InvalidOperationException
    {
        public override string Message => Localization.Format(key, arguments);
    }

    private SetupWindow(string installer, string defaultsFile)
    {
        _installer = installer;
        _optionsFile = Path.Combine(Path.GetDirectoryName(defaultsFile)!, "setup-options.txt");
        string language = System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ru" ? "ru" : "en";
        UiLanguage.Apply(language);
        InitializeComponent();
        // Сквозная проверка открывает настоящее окно, не меняя активное приложение.
        TestWindowActivation.Configure(this);
        LanguagePicker.SelectedIndex = language == "ru" ? 1 : 0;
        SetupVersion.Text = typeof(AppVersion).Assembly.GetName().Version?.ToString(3);
        SetupVersion.ToolTip = AppVersion.InformationalVersion;
        string[] defaults = File.ReadAllLines(defaultsFile);
        InstallDirectory.Text = defaults[0];
        DataDirectory.Text = defaults[1];
        ReplaysDirectory.Text = defaults[2];
    }

    internal static int Run(string installer, string defaultsFile)
    {
        try
        {
            var app = new Application();
            var window = new SetupWindow(installer, defaultsFile);
            app.Run(window);
            return window._result;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return 2;
        }
    }

    private void OnDragHeader(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    private void OnBrowseInstall(object sender, RoutedEventArgs e) => Browse(InstallDirectory);
    private void OnBrowseData(object sender, RoutedEventArgs e) => Browse(DataDirectory);
    private void OnBrowseReplays(object sender, RoutedEventArgs e) => Browse(ReplaysDirectory);

    private void OnLanguageChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is ComboBox { SelectedItem: ComboBoxItem { Tag: string language } })
        {
            UiLanguage.Apply(language);
            if (_statusText is not null) Status.Text = _statusText();
        }
    }

    private void SetStatus(string key) => SetStatus(() => UiLanguage.Text(key));
    private void SetStatus(Func<string>? text) { _statusText = text; Status.Text = text?.Invoke() ?? ""; }

    private void Browse(TextBox field)
    {
        var dialog = new OpenFolderDialog { Title = UiLanguage.Text("Setup.ChooseFolder") };
        if (Directory.Exists(field.Text)) dialog.InitialDirectory = field.Text;
        if (dialog.ShowDialog(this) == true) field.Text = dialog.FolderName;
    }

    private async void OnInstall(object sender, RoutedEventArgs e)
    {
        if (_installing) return;
        if (_result == 0)
        {
            Close();
            return;
        }

        string[] folders = [InstallDirectory.Text.Trim(), DataDirectory.Text.Trim(), ReplaysDirectory.Text.Trim()];
        if (folders.Any(folder => folder.Length < 3 || folder[1] != ':' || folder[2] != '\\' ||
                                  folder.Any(char.IsControl) || !Path.IsPathFullyQualified(folder)))
        {
            SetStatus("Setup.InvalidFolders");
            return;
        }

        _installing = true;
        Options.IsEnabled = InstallButton.IsEnabled = CancelButton.IsEnabled = false;
        SetStatus("Setup.Installing");
        Progress.IsIndeterminate = true;
        Progress.Visibility = Visibility.Visible;
        try
        {
            if (!await FfmpegTools.EnsureAsync(() => FfmpegTools.Find(folders[0]), InstallFfmpegAsync))
            {
                SetStatus("Setup.MissingFfmpeg");
                return;
            }
            SetStatus("Setup.Installing");
            string setupLanguage = (string)((ComboBoxItem)LanguagePicker.SelectedItem).Tag;
            await File.WriteAllLinesAsync(_optionsFile, [..folders, AutoStart.IsChecked == true ? "1" : "0", setupLanguage], Encoding.Unicode);
            // GetOptions ожидает кавычки вокруг значения, если в пути есть пробелы.
            var start = new ProcessStartInfo(_installer)
            {
                Arguments = $"/S /OPTIONS=\"{_optionsFile}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using Process process = Process.Start(start) ?? throw new SetupFailure("Setup.NsisFailed");
            await process.WaitForExitAsync();
            string? status = process.ExitCode switch
            {
                0 => null,
                1 => "Setup.Occupied",
                3 => "Setup.InvalidFolders",
                _ => "Setup.InstallFailed",
            };
            if (status is null) SetStatus((Func<string>?)null); else SetStatus(status);
            if (process.ExitCode == 0)
            {
                _result = 0;
                try
                {
                    string language = (string)((ComboBoxItem)LanguagePicker.SelectedItem).Tag;
                    new LanguagePreferencesFile(Path.Combine(folders[1], "language.json")).SetInitialLanguage(language);
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    SetStatus(() => Localization.Format("Setup.LanguageSaveFailed", error.Message));
                }
                InstalledLocation.Text = folders[0];
                Options.Visibility = CancelButton.Visibility = Visibility.Collapsed;
                Complete.Visibility = Visibility.Visible;
                InstallButton.SetResourceReference(ContentControl.ContentProperty, "Setup.Done");
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or Win32Exception or InvalidOperationException)
        {
            SetStatus(() => Localization.Format("Setup.StartFailed", error.Message));
        }
        finally
        {
            _installing = false;
            Progress.Visibility = Visibility.Collapsed;
            Progress.IsIndeterminate = false;
            Options.IsEnabled = InstallButton.IsEnabled = CancelButton.IsEnabled = true;
        }
    }

    private async Task InstallFfmpegAsync()
    {
        string winget = Path.Combine(FfmpegTools.LocalAppData, "Microsoft", "WindowsApps", "winget.exe");
        if (!File.Exists(winget))
            throw new SetupFailure("Setup.NoWinget");

        SetStatus("Setup.InstallingFfmpeg");
        var start = new ProcessStartInfo(winget) { UseShellExecute = false, CreateNoWindow = true };
        string[] arguments = ["install", "--id", "Gyan.FFmpeg", "--exact", "--source", "winget",
            "--scope", "user", "--silent", "--disable-interactivity",
            "--accept-source-agreements", "--accept-package-agreements", "--force"];
        foreach (string argument in arguments) start.ArgumentList.Add(argument);

        using Process process = Process.Start(start) ?? throw new SetupFailure("Setup.WingetFailed");
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        try
        {
            await process.WaitForExitAsync(deadline.Token);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            throw new SetupFailure("Setup.FfmpegTimeout");
        }

        if (process.ExitCode != 0 && FfmpegTools.Find(InstallDirectory.Text.Trim()) is null)
            throw new SetupFailure("Setup.FfmpegFailed", process.ExitCode);
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
    private void OnClosing(object? sender, CancelEventArgs e) => e.Cancel = _installing;
}
