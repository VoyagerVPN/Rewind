using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Runtime.CompilerServices;

namespace Rewind.Preview;

public static class SliderDragBehavior
{
    private sealed class DragState { public double Offset; }
    private static readonly ConditionalWeakTable<Slider, DragState> States = new();
    public static readonly DependencyProperty EnabledProperty = DependencyProperty.RegisterAttached("Enabled", typeof(bool), typeof(SliderDragBehavior),
        new PropertyMetadata(false, (sender, args) =>
        {
            if (sender is not Slider slider) return;
            if ((bool)args.NewValue)
            {
                slider.AddHandler(UIElement.PreviewMouseLeftButtonDownEvent, new MouseButtonEventHandler(Down), true);
                slider.PreviewMouseMove += Move; slider.PreviewMouseLeftButtonUp += Up;
            }
            else
            {
                slider.RemoveHandler(UIElement.PreviewMouseLeftButtonDownEvent, new MouseButtonEventHandler(Down));
                slider.PreviewMouseMove -= Move; slider.PreviewMouseLeftButtonUp -= Up;
            }
        }));
    public static bool GetEnabled(DependencyObject source) => (bool)source.GetValue(EnabledProperty);
    public static void SetEnabled(DependencyObject source, bool value) => source.SetValue(EnabledProperty, value);
    private static void Down(object sender, MouseButtonEventArgs args)
    {
        var slider = (Slider)sender;
        if (!slider.IsEnabled) return;
        slider.Focus();
        var state = States.GetOrCreateValue(slider); state.Offset = 0;
        if (slider.Template.FindName("PART_Track", slider) is Track track)
        {
            Point point = args.GetPosition(track.Thumb);
            if (point.X >= 0 && point.X <= track.Thumb.ActualWidth && point.Y >= 0 && point.Y <= track.Thumb.ActualHeight)
                state.Offset = point.X - track.Thumb.ActualWidth / 2;
        }
        slider.CaptureMouse(); Update(slider, args); args.Handled = true;
    }
    private static void Move(object sender, MouseEventArgs args)
    {
        var slider = (Slider)sender;
        if (Mouse.Captured != slider || args.LeftButton != MouseButtonState.Pressed) return;
        Update(slider, args); args.Handled = true;
    }
    private static void Up(object sender, MouseButtonEventArgs args)
    {
        var slider = (Slider)sender;
        if (Mouse.Captured != slider) return;
        Update(slider, args); slider.ReleaseMouseCapture(); args.Handled = true;
    }
    private static void Update(Slider slider, MouseEventArgs args)
    {
        if (slider.Template.FindName("PART_Track", slider) is not Track track) return;
        SetPointerValue(slider, args.GetPosition(track), States.GetOrCreateValue(slider).Offset);
    }

    public static void SetPointerValue(Slider slider, Point point, double grabOffset = 0)
    {
        if (slider.Template.FindName("PART_Track", slider) is not Track track) return;
        double thumb = track.Thumb.ActualWidth;
        double width = track.ActualWidth - thumb;
        if (width <= 0) return;
        // Абсолютная позиция не зависит от предыдущего Value и задержки layout.
        double fraction = Math.Clamp((point.X - grabOffset - thumb / 2) / width, 0, 1);
        if (slider.IsDirectionReversed) fraction = 1 - fraction;
        double value = slider.Minimum + fraction * (slider.Maximum - slider.Minimum);
        if (slider.IsSnapToTickEnabled)
        {
            if (slider.Ticks.Count > 0) value = slider.Ticks.Append(slider.Minimum).Append(slider.Maximum).MinBy(tick => Math.Abs(tick - value));
            else if (slider.TickFrequency > 0) value = slider.Minimum + Math.Round((value - slider.Minimum) / slider.TickFrequency, MidpointRounding.AwayFromZero) * slider.TickFrequency;
        }
        if (double.IsFinite(value)) slider.SetCurrentValue(RangeBase.ValueProperty, Math.Clamp(value, slider.Minimum, slider.Maximum));
    }
}
