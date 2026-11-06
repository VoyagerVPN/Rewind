using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Rewind.Preview;

public partial class OverlayWindow
{
    private readonly Dictionary<string, MonitorRecordingProfile> _monitorProfiles = [];
    private readonly Dictionary<string, MonitorProfileRow> _monitorRows = [];
    private sealed record MonitorProfileRow(CheckBox Toggle, ListBox Resolution, ListBox Fps);
    private sealed record ResolutionChoice(int Height, string Label) { public override string ToString() => Label; }
    private static string ProfileKey(MonitorRecordingProfile profile) => profile.Identity ?? profile.DeviceName;
    private MonitorRecordingProfile ProfileFor(CaptureMonitor monitor) => _monitorProfiles[monitor.Identity];

    private void RefreshMonitorProfiles()
    {
        foreach (CaptureMonitor monitor in _monitors)
        {
            MonitorRecordingProfile? profile = _monitorProfiles.Values.FirstOrDefault(profile => profile.Identity == monitor.Identity ||
                profile.Identity is null && profile.DeviceName == monitor.DeviceName);
            if (profile is not null) _monitorProfiles.Remove(ProfileKey(profile));
            profile = profile is null ? new MonitorRecordingProfile(monitor.DeviceName, _captureSettings is null && monitor.Primary,
                _captureSettings is null && monitor.Primary ? 120 : 30, 0, monitor.Identity) : profile with { DeviceName = monitor.DeviceName, Identity = monitor.Identity };
            _monitorProfiles[monitor.Identity] = profile;
        }
        _draftVideoEnabled = _monitorProfiles.Values.Any(profile => profile.Enabled);
    }

    private string MonitorStateKey() =>
        (RecordingMonitorName() is null ? "idle" : "recording") + ":" + string.Join('|', _monitorProfiles.Values.OrderBy(profile => profile.DeviceName));

    private void UpdateMonitorRows()
    {
        CaptureMonitor[] ordered = _monitors.OrderBy(monitor => MonitorNumber(monitor.DeviceName)).ToArray();
        if (!MonitorSwitches.Children.OfType<FrameworkElement>().Select(row => (CaptureMonitor)row.Tag).SequenceEqual(ordered))
        {
            MonitorSwitches.Children.Clear(); _monitorRows.Clear();
            foreach (CaptureMonitor monitor in ordered)
            {
                var row = new StackPanel { Tag = monitor, Margin = new Thickness(0, 0, 0, 24) };
                var header = new Grid { MinHeight = 40, Margin = new Thickness(0, 0, 0, 10) };
                header.ColumnDefinitions.Add(new ColumnDefinition()); header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var labels = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
                labels.Children.Add(new TextBlock { Text = "Экран " + MonitorNumber(monitor.DeviceName), FontSize = 16, FontWeight = FontWeights.SemiBold });
                labels.Children.Add(new TextBlock { Text = monitor.Name + (monitor.Primary ? " · основной" : ""), FontSize = 12,
                    Foreground = (Brush)FindResource("Muted"), TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 3, 0, 0) });
                header.Children.Add(labels);
                var toggle = new CheckBox { Content = "Записывать", VerticalAlignment = VerticalAlignment.Center, ToolTip = "Включать этот экран в историю повторов" };
                System.Windows.Automation.AutomationProperties.SetName(toggle, "Запись " + monitor.DeviceName);
                void Change()
                {
                    if (_syncingMonitorSwitches) return;
                    _monitorProfiles[monitor.Identity] = ProfileFor(monitor) with { Enabled = toggle.IsChecked == true };
                    _draftVideoEnabled = _monitorProfiles.Values.Any(profile => profile.Enabled);
                    RefreshSettingsPresentation();
                }
                toggle.Checked += (_, _) => Change(); toggle.Unchecked += (_, _) => Change();
                Grid.SetColumn(toggle, 1); header.Children.Add(toggle); row.Children.Add(header);
                ListBox Selector(string name, object[] choices)
                {
                    var selector = new ListBox { Style = (Style)FindResource("SegmentedSelector"), ItemsSource = choices, MinHeight = 32 };
                    System.Windows.Automation.AutomationProperties.SetName(selector, name + " " + monitor.DeviceName);
                    return selector;
                }
                ResolutionChoice[] choices = new[] { new ResolutionChoice(0, monitor.Height + "p") }
                    .Concat(new[] { 1440, 1080, 720, 480, 360 }.Where(height => height < monitor.Height).Select(height => new ResolutionChoice(height, height + "p"))).Reverse().ToArray();
                var resolution = Selector("Разрешение", choices.Cast<object>().ToArray());
                resolution.ToolTip = $"Исходное: {monitor.Width} × {monitor.Height}. Уменьшение пока использует CPU.";
                var fps = Selector("Частота кадров", new object[] { 30, 60, 120 });
                var quality = new Grid(); quality.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(95) }); quality.ColumnDefinitions.Add(new ColumnDefinition());
                quality.RowDefinitions.Add(new RowDefinition()); quality.RowDefinitions.Add(new RowDefinition());
                TextBlock Label(string text) => new() { Text = text, FontSize = 12, Foreground = (Brush)FindResource("Muted"), VerticalAlignment = VerticalAlignment.Center };
                quality.Children.Add(Label("Разрешение")); Grid.SetColumn(resolution, 1); quality.Children.Add(resolution);
                var fpsLabel = Label("Кадры/с"); Grid.SetRow(fpsLabel, 1); quality.Children.Add(fpsLabel);
                fps.Margin = new Thickness(0, 6, 0, 0); Grid.SetColumn(fps, 1); Grid.SetRow(fps, 1); quality.Children.Add(fps);
                row.Children.Add(quality);
                void SaveQuality()
                {
                    if (_syncingMonitorSwitches || !_captureUiReady || resolution.SelectedItem is not ResolutionChoice size || fps.SelectedItem is not int rate) return;
                    _monitorProfiles[monitor.Identity] = ProfileFor(monitor) with { OutputHeight = size.Height, Fps = rate };
                    RefreshSettingsPresentation();
                }
                resolution.SelectionChanged += (_, _) => SaveQuality(); fps.SelectionChanged += (_, _) => SaveQuality();
                _monitorRows[monitor.Identity] = new(toggle, resolution, fps);
                MonitorSwitches.Children.Add(row);
            }
        }
        _syncingMonitorSwitches = true;
        try
        {
            foreach (CaptureMonitor monitor in ordered)
            {
                MonitorRecordingProfile profile = ProfileFor(monitor); MonitorProfileRow row = _monitorRows[monitor.Identity];
                row.Toggle.IsChecked = profile.Enabled;
                row.Resolution.SelectedItem = row.Resolution.Items.Cast<ResolutionChoice>().FirstOrDefault(choice => choice.Height == profile.OutputHeight) ?? row.Resolution.Items[row.Resolution.Items.Count - 1];
                row.Fps.SelectedItem = profile.Fps;
                row.Resolution.IsEnabled = row.Fps.IsEnabled = profile.Enabled;
                ((FrameworkElement)row.Resolution.Parent).Visibility = profile.Enabled ? Visibility.Visible : Visibility.Collapsed;
            }
        }
        finally { _syncingMonitorSwitches = false; }
    }
}
