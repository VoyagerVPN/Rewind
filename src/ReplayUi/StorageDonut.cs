using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace Rewind.Preview;
public sealed record StorageSlice(string Name, long Bytes, Color Color);
public sealed class StorageDonut : FrameworkElement
{
    private IReadOnlyList<StorageSlice> _slices = [];
    private long _total;
    private int _clips, _selected = -1;
    public event EventHandler<int>? Selected;
    public StorageDonut()
    {
        Cursor = Cursors.Hand;
        MouseMove += (_, args) => { int index = Hit(args.GetPosition(this)); ToolTip = index < 0 ? null : _slices[index].Name + " · " + OverlayWindow.FormatBytes(_slices[index].Bytes); };
        MouseLeftButtonDown += (_, args) => { int index = Hit(args.GetPosition(this)); if (index >= 0) { ActivateSlice(index); args.Handled = true; } };
    }
    public void SetSlices(IReadOnlyList<StorageSlice> slices, long total, int clips) { _slices = slices; _total = total; _clips = clips; _selected = -1; InvalidateVisual(); }
    public void Select(int index) { _selected = index; InvalidateVisual(); }
    public void ActivateSlice(int index) { if (index < 0 || index >= _slices.Count) throw new ArgumentOutOfRangeException(nameof(index)); Select(index); Selected?.Invoke(this, index); }
    private int Hit(Point point)
    {
        double x = point.X - ActualWidth / 2, y = point.Y - ActualHeight / 2;
        double radius = Math.Min(ActualWidth, ActualHeight) / 2 - 4, distance = Math.Sqrt(x * x + y * y);
        if (distance > radius || distance < radius - 26) return -1;
        double angle = (Math.Atan2(y, x) + Math.PI / 2 + Math.PI * 2) % (Math.PI * 2), sum = _slices.Sum(slice => (double)slice.Bytes), end = 0;
        for (int index = 0; index < _slices.Count; index++) { end += _slices[index].Bytes / Math.Max(1, sum) * Math.PI * 2; if (angle < end) return index; }
        return -1;
    }
    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc); var center = new Point(ActualWidth / 2, ActualHeight / 2);
        double radius = Math.Min(ActualWidth, ActualHeight) / 2 - 4, sum = _slices.Sum(slice => (double)slice.Bytes), angle = -Math.PI / 2;
        if (radius <= 0) return;
        for (int index = 0; index < _slices.Count; index++)
        {
            double span = _slices[index].Bytes / Math.Max(1, sum) * Math.PI * 2;
            if (span <= 0) continue;
            double end = angle + Math.Min(span, Math.PI * 2 - .0001), inner = radius - (index == _selected ? 24 : 18);
            Point At(double r, double a) => new(center.X + r * Math.Cos(a), center.Y + r * Math.Sin(a));
            var geometry = new StreamGeometry();
            using (StreamGeometryContext path = geometry.Open())
            { path.BeginFigure(At(radius, angle), true, true); path.ArcTo(At(radius, end), new Size(radius, radius), 0, span > Math.PI, SweepDirection.Clockwise, true, false);
                path.LineTo(At(inner, end), true, false); path.ArcTo(At(inner, angle), new Size(inner, inner), 0, span > Math.PI, SweepDirection.Counterclockwise, true, false); }
            geometry.Freeze(); dc.DrawGeometry(new SolidColorBrush(_slices[index].Color), null, geometry); angle += span;
        }
        void Text(string text, int size, double y, Brush brush)
        { var formatted = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), size, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip); dc.DrawText(formatted, new Point(center.X - formatted.Width / 2, center.Y + y)); }
        StorageSlice? selected = _selected >= 0 && _selected < _slices.Count ? _slices[_selected] : null;
        Text(OverlayWindow.FormatBytes(selected?.Bytes ?? _total), 21, -22, Brushes.Gainsboro);
        Text(selected is not null && _total > 0 ? $"{100d * selected.Bytes / _total:F1}% объёма" : $"Клипов: {_clips}", 11, 12, Brushes.Gray);
    }
}
