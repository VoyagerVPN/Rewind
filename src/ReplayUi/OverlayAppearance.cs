using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Rewind.Preview;

public partial class OverlayWindow
{
    private void RenderMarkerRows()
    {
        if (MarkerRows is null) return;
        MarkerRows.Children.Clear();
        foreach (int seconds in _intervals.Markers)
        {
            bool available = seconds <= _intervals.MaximumSeconds;
            var row = new Grid { Width = 202, Margin = new Thickness(0, 0, 12, 8), MinHeight = 38 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(82) });
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var primary = new Button { Content = seconds == _intervals.PreferredDefaultSeconds ? "★" : "☆", FontSize = 19,
                Style = (Style)FindResource("IconButton"), IsEnabled = available, ToolTip = "Сделать основным" };
            System.Windows.Automation.AutomationProperties.SetName(primary, "Основной " + ReplayIntervals.Format(seconds));
            primary.Click += (_, _) => { _intervals.SetDefault(seconds); IntervalTimeline.Redraw(); UpdateIntervalLabels(); };
            row.Children.Add(primary);
            var time = new TimeValueEditor { Value = seconds, VerticalAlignment = VerticalAlignment.Center };
            System.Windows.Automation.AutomationProperties.SetName(time, "Отметка " + ReplayIntervals.Format(seconds));
            time.ValueChanged += (_, args) =>
            {
                try { _intervals.Move(seconds, args.NewValue); SettingsMessageText.Text = ""; }
                catch (ArgumentException error) { SettingsMessageText.Text = error.Message; }
                IntervalTimeline.Redraw(); UpdateIntervalLabels();
            };
            Grid.SetColumn(time, 1); row.Children.Add(time);
            var label = new TextBlock { Text = !available ? "Вне истории" : "",
                Foreground = (Brush)FindResource("Muted"), FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) };
            Grid.SetColumn(label, 2); row.Children.Add(label);
            var remove = new Button { Style = (Style)FindResource("IconButton"), ToolTip = "Удалить отметку",
                IsEnabled = _intervals.Markers.Count > 1, Content = "delete", ContentTemplate = (DataTemplate)FindResource("DeleteMarkerIcon") };
            System.Windows.Automation.AutomationProperties.SetName(remove, "Удалить " + ReplayIntervals.Format(seconds));
            remove.Click += (_, _) => { _intervals.Remove(seconds); IntervalTimeline.Redraw(); UpdateIntervalLabels(); };
            Grid.SetColumn(remove, 3); row.Children.Add(remove);
            MarkerRows.Children.Add(row);
        }
        int inactive = _intervals.Markers.Count - _intervals.ActiveMarkers.Count;
        IntervalAvailabilityText.Text = inactive == 0 ? "" : $"Вне истории: {inactive}. Отметки сохранены. Сейчас: {ReplayIntervals.Format(_intervals.DefaultSeconds)}.";
        UpdateAddInterval();
    }
    public void ShowSettingsSection(string section)
    {
        if (section is not ("screens" or "audio" or "replay" or "storage" or "hotkeys")) throw new ArgumentException("Неизвестный раздел настроек.");
        _libraryVisible = false; LibraryView.Visibility = Visibility.Collapsed; _scanCancellation?.Cancel();
        HomeView.Visibility = Visibility.Collapsed; SettingsView.Visibility = Visibility.Visible;
        SettingsLabel.Text = "Назад"; SettingsGlyph.Symbol = Wpf.Ui.Controls.SymbolRegular.ArrowLeft24;
        foreach (RadioButton tab in SettingsTabs.Children.OfType<RadioButton>())
            if ((string)tab.Tag == section && tab.IsChecked != true) tab.IsChecked = true;
        ScreensSettings.Visibility = section == "screens" ? Visibility.Visible : Visibility.Collapsed;
        AudioSettings.Visibility = section == "audio" ? Visibility.Visible : Visibility.Collapsed;
        ReplaySettings.Visibility = section == "replay" ? Visibility.Visible : Visibility.Collapsed;
        StorageSettings.Visibility = section == "storage" ? Visibility.Visible : Visibility.Collapsed;
        SettingsScroll.Visibility = section == "storage" ? Visibility.Collapsed : Visibility.Visible;
        HotkeysSettings.Visibility = section == "hotkeys" ? Visibility.Visible : Visibility.Collapsed;
        CaptureSettingsFields.Visibility = section == "hotkeys" ? Visibility.Collapsed : Visibility.Visible;
        PanelHotkey.CancelCapture(); SaveHotkey.CancelCapture(); SaveAllHotkey.CancelCapture();
        CancelPlayerKeyCapture();
        if (section != "storage") _usageCancellation?.Cancel();
        QueueAudioMonitoring();
        if (section == "storage") _ = RefreshStorageUsageAsync();
        UpdateSettingsIndicator(true);
    }

    private void OnSettingsTab(object sender, RoutedEventArgs args)
    {
        if (!_captureUiReady) return;
        ShowSettingsSection((string)((RadioButton)sender).Tag);
    }

    private async void OnGallery(object sender, RoutedEventArgs args) => await OpenLibraryAsync();

    private void UpdateIntervalLabels()
    {
        HistoryTime.Value = (int)Math.Round(HistorySlider.Value);
        OnIntervalsChanged(this, EventArgs.Empty);
    }

    private void OnHistorySliderChanged(object sender, RoutedPropertyChangedEventArgs<double> args)
    {
        if (!_captureUiReady) return;
        if (HistorySlider.Value < 4) { HistorySlider.Value = 15; return; }
        OnHistoryChanged(sender, args); UpdateIntervalLabels();
    }

    private void OnHistoryTimeChanged(object sender, RoutedPropertyChangedEventArgs<int> args)
    {
        if (_captureUiReady && Math.Abs(HistorySlider.Value - args.NewValue) > .01) HistorySlider.Value = args.NewValue;
    }

    private void OnNewIntervalTimeChanged(object sender, RoutedPropertyChangedEventArgs<int> args) { if (_captureUiReady) UpdateAddInterval(); }

    private void UpdateAddInterval()
    {
        bool fits = NewIntervalTime.Value <= _intervals.MaximumSeconds;
        bool exists = _intervals.Markers.Contains(NewIntervalTime.Value);
        AddIntervalButton.IsEnabled = fits && !exists;
        AddIntervalButton.ToolTip = !fits ? "Увеличь историю до " + ReplayIntervals.Format(NewIntervalTime.Value) :
            exists ? "Такая отметка уже есть" : "Добавить отметку";
    }

    private void OnHistoryPreset(object sender, RoutedEventArgs args) =>
        HistorySlider.Value = int.Parse((string)((Button)sender).Tag, CultureInfo.InvariantCulture);

    private void OnChooseStorage(object sender, RoutedEventArgs args) => ChooseReplayFolder(StorageInput, "Папка для повторов");

    private void OnChooseShadowPlayStorage(object sender, RoutedEventArgs args) => ChooseReplayFolder(ShadowPlayPath, "Архив ShadowPlay");

    private void ChooseReplayFolder(System.Windows.Controls.TextBox field, string title)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = title };
        if (System.IO.Directory.Exists(field.Text)) dialog.InitialDirectory = field.Text;
        if (dialog.ShowDialog(this) != true) return;
        field.Text = dialog.FolderName;
        _ = RefreshStorageUsageAsync();
    }
}
