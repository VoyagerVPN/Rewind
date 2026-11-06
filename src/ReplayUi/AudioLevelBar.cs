using System.Windows;
using System.Windows.Media;

namespace Rewind.Preview;

public sealed class AudioLevelBar : FrameworkElement
{
    public static readonly DependencyProperty PeakProperty = DependencyProperty.Register(nameof(Peak), typeof(double), typeof(AudioLevelBar),
        new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));
    public double Peak { get => (double)GetValue(PeakProperty); set => SetValue(PeakProperty, value); }
    public double FilledFraction => AudioLevelEnvelope.Fill(Peak);

    protected override void OnRender(DrawingContext drawing)
    {
        base.OnRender(drawing);
        if (ActualWidth <= 0 || ActualHeight <= 0) return;
        Brush track = (Brush)FindResource("Line");
        Brush ink = (Brush)FindResource("Accent");
        drawing.DrawRectangle(track, null, new Rect(0, 0, ActualWidth, ActualHeight));
        drawing.DrawRectangle(ink, null, new Rect(0, 0, ActualWidth * FilledFraction, ActualHeight));
        var pen = new Pen((Brush)FindResource("Surface"), 1);
        foreach (double tick in new[] { .2, .4, .6, .8, .9 })
            drawing.DrawLine(pen, new Point(Math.Round(ActualWidth * tick), 0), new Point(Math.Round(ActualWidth * tick), ActualHeight));
    }
}
