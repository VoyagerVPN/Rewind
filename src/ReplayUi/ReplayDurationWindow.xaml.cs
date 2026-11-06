using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace Rewind.Preview;

internal sealed partial class ReplayDurationWindow : Window
{
    private const double LabelWidth = 44;
    private readonly ReplayDurationSelection _selection;
    private readonly TranslateTransform _markerPosition = new();

    public ReplayDurationWindow(ReplayDurationSelection selection)
    {
        _selection = selection;
        InitializeComponent();
        TimelineScroll.ScrollChanged += (_, args) =>
        {
            if (args.ViewportWidthChange != 0) Timeline.Width = Math.Max(args.ViewportWidth, selection.Markers.Count * LabelWidth);
        };
        Timeline.SizeChanged += (_, _) => DrawTimeline();
        SourceInitialized += (_, _) =>
        {
            nint handle = new WindowInteropHelper(this).Handle;
            long style = Native.GetWindowLongPtr(handle, -20).ToInt64();
            Native.SetWindowLongPtr(handle, -20, (nint)(style | 0x08000000 | 0x80 | 0x20));
        };
        UpdateSelection(animate: false);
    }

    public void ShowOnMonitor(nint monitor)
    {
        var info = new Native.MonitorInfo { Size = Marshal.SizeOf<Native.MonitorInfo>() };
        if (!Native.GetMonitorInfo(monitor, ref info)) throw new Win32Exception(Marshal.GetLastWin32Error());
        nint handle = new WindowInteropHelper(this).EnsureHandle();
        Native.SetWindowPos(handle, -1, info.Work.Left, info.Work.Top, 0, 0, 0x11);
        double scale = Math.Max(96, Native.GetDpiForWindow(handle)) / 96d;
        Width = Math.Min(Width, Math.Max(120, (info.Work.Right - info.Work.Left - 24) / scale));
        Opacity = 0;
        Show();
        UpdateLayout();
        int width = (int)Math.Ceiling(ActualWidth * scale), height = (int)Math.Ceiling(ActualHeight * scale);
        int left = info.Work.Left + (info.Work.Right - info.Work.Left - width) / 2;
        int top = Math.Max(info.Work.Top, info.Work.Bottom - height - (int)(24 * scale));
        if (!Native.SetWindowPos(handle, -1, left, top, 0, 0, 0x11)) throw new Win32Exception(Marshal.GetLastWin32Error());
        Opacity = 1;
    }

    public void UpdateSelection(bool animate = true)
    {
        SelectedDuration.Text = ReplayIntervals.Format(_selection.Seconds);
        double current = _markerPosition.X;
        double next = MarkerOffset(_selection.Index);
        _markerPosition.X = next;
        _markerPosition.BeginAnimation(TranslateTransform.XProperty, animate && SystemParameters.ClientAreaAnimation
            ? new DoubleAnimation(current, next, TimeSpan.FromMilliseconds(150))
                { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }, FillBehavior = FillBehavior.Stop }
            : null);
        double viewportWidth = TimelineScroll.ViewportWidth;
        if (viewportWidth > 0 && (next - LabelWidth / 2 < TimelineScroll.HorizontalOffset ||
            next + LabelWidth / 2 > TimelineScroll.HorizontalOffset + viewportWidth))
            TimelineScroll.ScrollToHorizontalOffset(next - viewportWidth / 2);
    }

    private double MarkerOffset(int index) => _selection.Markers.Count == 1 ? Timeline.ActualWidth / 2 :
        LabelWidth / 2 + Math.Max(0, Timeline.ActualWidth - LabelWidth) * index / (_selection.Markers.Count - 1);

    private void DrawTimeline()
    {
        Timeline.Children.Clear();
        if (_selection.Markers.Count > 1)
            Timeline.Children.Add(new Line { X1 = MarkerOffset(0), X2 = MarkerOffset(_selection.Markers.Count - 1),
                Y1 = 10, Y2 = 10, Stroke = (Brush)FindResource("Muted"), Opacity = .45 });
        var muted = (Brush)FindResource("Muted");
        var background = (Brush)FindResource("Raised");
        for (int index = 0; index < _selection.Markers.Count; index++)
        {
            double position = MarkerOffset(index);
            var dot = new Ellipse { Width = 8, Height = 8, Stroke = muted, StrokeThickness = 1.25, Fill = background };
            Canvas.SetLeft(dot, position - 4);
            Canvas.SetTop(dot, 6);
            Timeline.Children.Add(dot);
            var label = new TextBlock { Width = LabelWidth, TextAlignment = TextAlignment.Center, FontSize = 11,
                Text = ReplayIntervals.Format(_selection.Markers[index]), Foreground = muted };
            Canvas.SetLeft(label, position - LabelWidth / 2);
            Canvas.SetTop(label, 19);
            Timeline.Children.Add(label);
        }
        var marker = new Ellipse { Width = 10, Height = 10, Fill = (Brush)FindResource("Ink"), RenderTransform = _markerPosition };
        Canvas.SetLeft(marker, -5);
        Canvas.SetTop(marker, 5);
        Timeline.Children.Add(marker);
        UpdateSelection(animate: false);
    }
}
