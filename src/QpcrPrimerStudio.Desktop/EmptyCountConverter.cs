using System.Globalization;
using System.Windows.Data;

namespace QpcrPrimerStudio.Desktop;

public sealed class EmptyCountConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is int count && count == 0;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
