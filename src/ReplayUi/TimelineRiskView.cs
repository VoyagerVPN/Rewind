using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace Rewind.Preview;

internal sealed record PlaybackRiskRange(double Start, double End);

public sealed class TimelineRiskView : FrameworkElement
{
    private static readonly DrawingBrush Tape = CreateTape();
    private PlaybackRiskRange[] _ranges = [];
    private double[] _failures = [];
    private double _duration;
    private double _playhead;
    internal string Description { get; private set; } = "";
    internal bool IsRisky(double seconds) => _ranges.Any(range => seconds >= range.Start && seconds < range.End - .005);
    internal void SetHealth(PlaybackHealth? health, double duration)
    {
        _duration = duration;
        _failures = health?.Issues.Select(issue => issue.PositionSeconds).Where(seconds => seconds <= duration).Distinct().Order().ToArray() ?? [];
        var ranges = _failures.Select(seconds => new PlaybackRiskRange(Math.Max(0, seconds - .5), Math.Min(duration, seconds + .5))).ToList();
        if (_failures.Length > 0 && health?.RecoveredStartSeconds is double known && known > _failures[0] && known <= duration)
            ranges.Add(new(_failures[0] < 1 ? 0 : _failures[0], known));
        var merged = new List<PlaybackRiskRange>();
        foreach (PlaybackRiskRange range in ranges.Where(range => range.End > range.Start).OrderBy(range => range.Start))
        {
            if (merged.Count > 0 && range.Start <= merged[^1].End)
                merged[^1] = merged[^1] with { End = Math.Max(merged[^1].End, range.End) };
            else merged.Add(range);
        }
        _ranges = merged.ToArray();
        Visibility = _ranges.Length > 0 && duration > 0 ? Visibility.Visible : Visibility.Collapsed;
        Description = Localization.Text("Risk.Description");
        if (_failures.Length > 0) Description += Localization.Format("Risk.Failures", string.Join(", ", _failures.Take(6).Select(Format)));
        if (health?.RecoveredStartSeconds is double start) Description += Localization.Format("Risk.Recovered", Format(start));
        InvalidateVisual();
    }
    private static string Format(double seconds) => TimeSpan.FromSeconds(Math.Max(0, seconds)).ToString(@"m\:ss", CultureInfo.InvariantCulture);
    private static DrawingBrush CreateTape()
    {
        var drawing = new DrawingGroup();
        using (DrawingContext context = drawing.Open())
        {
            context.DrawRectangle(new SolidColorBrush(Color.FromRgb(99, 84, 48)), null, new Rect(0, 0, 6, 6));
            var stripe = new Pen(new SolidColorBrush(Color.FromRgb(224, 183, 90)), 2);
            context.DrawLine(stripe, new Point(-1, 7), new Point(7, -1));
        }
        var brush = new DrawingBrush(drawing)
        {
            TileMode = TileMode.Tile, ViewportUnits = BrushMappingMode.Absolute, Viewport = new Rect(0, 0, 6, 6),
            ViewboxUnits = BrushMappingMode.Absolute, Viewbox = new Rect(0, 0, 6, 6), Stretch = Stretch.None
        };
        brush.Freeze(); return brush;
    }
    internal void SetPlayhead(double seconds) { if (Math.Abs(_playhead - seconds) < .01) return; _playhead = seconds; InvalidateVisual(); }
    protected override void OnRender(DrawingContext context)
    {
        base.OnRender(context);
        if (_duration <= 0 || ActualWidth <= 0) return;
        // Время отсчитывается от центра бегунка шириной 12 px, но крайние участки покрывают всю шкалу.
        double inset = Math.Min(6, ActualWidth / 2), travel = ActualWidth - 2 * inset;
        double PositionX(double seconds) => inset + Math.Clamp(seconds / _duration, 0, 1) * travel;
        double thumb = PositionX(_playhead);
        context.PushClip(new CombinedGeometry(GeometryCombineMode.Exclude, new RectangleGeometry(new Rect(0, 0, ActualWidth, ActualHeight)), new RectangleGeometry(new Rect(thumb - 4, 0, 8, ActualHeight))));
        foreach (PlaybackRiskRange range in _ranges)
        {
            double start = range.Start <= 0 ? 0 : PositionX(range.Start);
            double end = range.End >= _duration ? ActualWidth : PositionX(range.End);
            var rect = new Rect(start, 0, Math.Max(2, end - start), ActualHeight);
            context.DrawRectangle(Tape, null, rect);
        }
        context.Pop();
    }
}
