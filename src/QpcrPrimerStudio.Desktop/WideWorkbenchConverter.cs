using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace QpcrPrimerStudio.Desktop;

public sealed class WideWorkbenchConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture) =>
        values.Length == 3 && values[0] is double width && width >= 1000 && values[1] is double height && height >= 280 && values[2] is true
            ? Visibility.Visible : Visibility.Collapsed;
    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
