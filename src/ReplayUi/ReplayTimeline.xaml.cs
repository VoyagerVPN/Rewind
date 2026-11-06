using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace Rewind.Preview;

public partial class ReplayTimeline : UserControl
{
    private ReplayIntervals? _model;
    public event EventHandler? Changed;
    public ReplayTimeline() { InitializeComponent(); }
    public void SetModel(ReplayIntervals model) { _model = model; Redraw(); }
    private void OnSizeChanged(object sender, SizeChangedEventArgs args) => Redraw();

    public void Redraw()
    {
        Ruler.Children.Clear();
        if (_model is null || Ruler.ActualWidth < 40) return;
        double length = Ruler.ActualWidth - 40;
        Brush muted = new SolidColorBrush(Color.FromRgb(150, 150, 150));
        Ruler.Children.Add(new Line { X1 = 20, X2 = 20 + length, Y1 = 20, Y2 = 20, Stroke = muted, StrokeThickness = 2 });
        var laneEnds = new List<double>();
        foreach (int seconds in _model.ActiveMarkers)
        {
            bool primary = seconds == _model.DefaultSeconds;
            double x = 20 + seconds / (double)_model.MaximumSeconds * length;
            var label = new TextBlock { Text = ReplayIntervals.Format(seconds), Foreground = primary ? Brushes.White : muted,
                FontSize = 12, FontWeight = primary ? FontWeights.SemiBold : FontWeights.Normal, IsHitTestVisible = false };
            label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            double left = Math.Clamp(x - label.DesiredSize.Width / 2, 0, Ruler.ActualWidth - label.DesiredSize.Width);
            int lane = laneEnds.FindIndex(end => end + 8 <= left);
            if (lane < 0) { lane = laneEnds.Count; laneEnds.Add(0); }
            laneEnds[lane] = left + label.DesiredSize.Width;
            double labelY = 38 + lane * 23;
            Canvas.SetLeft(label, left); Canvas.SetTop(label, labelY); Ruler.Children.Add(label);
            var leader = new Line { X1 = x, X2 = x, Y1 = 29, Y2 = labelY - 2, Stroke = muted, StrokeThickness = 1, Opacity = .4, IsHitTestVisible = false };
            Ruler.Children.Add(leader);
            var template = new ControlTemplate(typeof(Thumb));
            var dot = new FrameworkElementFactory(typeof(Ellipse));
            dot.SetValue(FrameworkElement.WidthProperty, 13.0); dot.SetValue(FrameworkElement.HeightProperty, 13.0);
            dot.SetValue(Shape.StrokeProperty, Brushes.White); dot.SetValue(Shape.StrokeThicknessProperty, 1.5);
            dot.SetValue(Shape.FillProperty, primary ? Brushes.White : new SolidColorBrush(Color.FromRgb(28, 28, 28)));
            template.VisualTree = dot;
            var thumb = new Thumb { Style = null, Template = template, Width = 28, Height = 28, Cursor = Cursors.SizeWE,
                Focusable = true, ToolTip = ReplayIntervals.Format(seconds) + " · перетащить · шаг 15 с" };
            System.Windows.Automation.AutomationProperties.SetName(thumb, "Отметка " + ReplayIntervals.Format(seconds));
            double startX = x;
            double offset = 0;
            int target = seconds;
            thumb.DragStarted += (_, _) => { offset = 0; startX = x; target = seconds; };
            thumb.DragDelta += (_, args) =>
            {
                offset += args.HorizontalChange;
                target = ReplayIntervals.Snap((startX + offset - 20) / length * _model.MaximumSeconds, _model.MaximumSeconds);
                double targetX = 20 + target / (double)_model.MaximumSeconds * length;
                Canvas.SetLeft(thumb, targetX - 14);
                label.Text = ReplayIntervals.Format(target); thumb.ToolTip = label.Text;
                Canvas.SetLeft(label, Math.Clamp(targetX - label.DesiredSize.Width / 2, 0, Ruler.ActualWidth - label.DesiredSize.Width));
                leader.X1 = leader.X2 = targetX;
            };
            thumb.DragCompleted += (_, args) =>
            {
                if (!args.Canceled)
                {
                    try
                    {
                        if (Math.Abs(offset) < 3) _model.SetDefault(seconds);
                        else _model.Move(seconds, target);
                    }
                    catch (ArgumentException) { }
                }
                Redraw(); Changed?.Invoke(this, EventArgs.Empty);
            };
            thumb.KeyDown += (_, args) =>
            {
                int focusSeconds = seconds;
                try
                {
                    if (args.Key is Key.Left or Key.Right)
                    {
                        int next = Math.Clamp(seconds + (args.Key == Key.Right ? 15 : -15), 1, _model.MaximumSeconds);
                        _model.Move(seconds, next); focusSeconds = next;
                    }
                    else if (args.Key is Key.Space or Key.Enter) _model.SetDefault(seconds);
                    else return;
                }
                catch (ArgumentException) { }
                args.Handled = true; Redraw();
                Ruler.Children.OfType<Thumb>().FirstOrDefault(control => System.Windows.Automation.AutomationProperties.GetName(control) ==
                    "Отметка " + ReplayIntervals.Format(focusSeconds))?.Focus();
                Changed?.Invoke(this, EventArgs.Empty);
            };
            Canvas.SetLeft(thumb, x - 14); Canvas.SetTop(thumb, 6); Ruler.Children.Add(thumb);
        }
        double bottom = 38 + Math.Max(1, laneEnds.Count) * 23;
        Ruler.Height = bottom + 24;
        var zero = new TextBlock { Text = "0:00", Foreground = muted, FontSize = 11, IsHitTestVisible = false };
        Canvas.SetTop(zero, bottom); Ruler.Children.Add(zero);
        var endLabel = new TextBlock { Text = ReplayIntervals.Format(_model.MaximumSeconds), Foreground = muted, FontSize = 11, IsHitTestVisible = false };
        Canvas.SetRight(endLabel, 0); Canvas.SetTop(endLabel, bottom); Ruler.Children.Add(endLabel);
    }

    private void OnRulerClick(object sender, MouseButtonEventArgs args)
    {
        if (_model is null || args.ClickCount != 2 || args.OriginalSource is not (Canvas or Line)) return;
        Point point = args.GetPosition(Ruler);
        if (point.Y is < 6 or > 34) return;
        int seconds = ReplayIntervals.Snap((point.X - 20) / (Ruler.ActualWidth - 40) * _model.MaximumSeconds, _model.MaximumSeconds);
        _model.Add(seconds); Redraw(); Changed?.Invoke(this, EventArgs.Empty);
    }
}
