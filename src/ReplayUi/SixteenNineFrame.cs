using System.Windows;
using System.Windows.Controls;

namespace Rewind.Preview;

public sealed class SixteenNineFrame : Decorator
{
    protected override Size MeasureOverride(Size constraint)
    {
        double width = double.IsFinite(constraint.Width) ? constraint.Width : 320;
        if (double.IsFinite(constraint.Height)) width = Math.Min(width, constraint.Height * 16 / 9);
        var size = new Size(width, width * 9 / 16);
        Child?.Measure(size);
        return size;
    }

    protected override Size ArrangeOverride(Size arrangeSize)
    {
        double width = Math.Min(arrangeSize.Width, arrangeSize.Height * 16 / 9);
        double height = width * 9 / 16;
        Child?.Arrange(new Rect((arrangeSize.Width - width) / 2, (arrangeSize.Height - height) / 2, width, height));
        return arrangeSize;
    }
}
