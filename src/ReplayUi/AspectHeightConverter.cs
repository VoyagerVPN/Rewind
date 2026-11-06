using System.Globalization;
using System.Windows.Data;

namespace Rewind.Preview;

public sealed class AspectHeightConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is double width && width > 0 ? width * 9 / 16 : 630d;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}
