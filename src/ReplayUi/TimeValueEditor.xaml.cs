using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Rewind.Preview;

public partial class TimeValueEditor : UserControl
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(nameof(Value), typeof(int), typeof(TimeValueEditor),
        new FrameworkPropertyMetadata(90, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnValueChanged));
    public int Value { get => (int)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public int Minimum { get; set; } = 1;
    public int Maximum { get; set; } = 900;
    public event RoutedPropertyChangedEventHandler<int>? ValueChanged;
    private bool _editing;

    public TimeValueEditor() { InitializeComponent(); Refresh(); Loaded += (_, _) => Refresh(); }
    private static void OnValueChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        var control = (TimeValueEditor)sender;
        control.Refresh();
        control.ValueChanged?.Invoke(control, new RoutedPropertyChangedEventArgs<int>((int)args.OldValue, (int)args.NewValue));
    }
    private void Refresh()
    {
        if (Display is null) return;
        ValueText.Text = ReplayIntervals.Format(Value);
        System.Windows.Automation.AutomationProperties.SetName(Display,
            System.Windows.Automation.AutomationProperties.GetName(this) + " " + ReplayIntervals.Format(Value));
    }
    private void OnEdit(object sender, RoutedEventArgs args)
    {
        _editing = true;
        Input.Text = ReplayIntervals.Format(Value); Input.ClearValue(Border.BorderBrushProperty); Input.ToolTip = Localization.Text("Message.MSsOrSeconds"); ErrorText.Visibility = Visibility.Collapsed;
        Display.Visibility = Visibility.Collapsed; Input.Visibility = Visibility.Visible;
        Input.Focus(); Input.SelectAll();
    }
    private bool Commit()
    {
        if (!_editing) return true;
        try
        {
            int value = ReplayIntervals.Parse(Input.Text);
            if (value < Minimum || value > Maximum) throw new ArgumentException(Localization.Format("Format.Range", ReplayIntervals.Format(Minimum), ReplayIntervals.Format(Maximum)));
            Value = value; Finish(); return true;
        }
        catch (ArgumentException error)
        {
            Input.BorderBrush = new SolidColorBrush(Color.FromRgb(196, 123, 116)); Input.ToolTip = error.Message;
            ErrorText.Text = error.Message; ErrorText.Visibility = Visibility.Visible;
            return false;
        }
    }
    private void Finish()
    {
        _editing = false; Input.Visibility = Visibility.Collapsed; Display.Visibility = Visibility.Visible; ErrorText.Visibility = Visibility.Collapsed;
    }
    private void OnInputKey(object sender, KeyEventArgs args)
    {
        if (args.Key == Key.Enter) { if (Commit()) Display.Focus(); args.Handled = true; }
        else if (args.Key == Key.Escape) { Finish(); Display.Focus(); args.Handled = true; }
    }
    private void OnInputBlur(object sender, KeyboardFocusChangedEventArgs args) { if (_editing && !Commit()) Finish(); }
}
