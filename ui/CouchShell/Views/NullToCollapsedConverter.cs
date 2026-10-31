using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace CouchShell.Views;

// Hides an element when the value it shows is missing, so it takes up no space.
public sealed class NullToCollapsedConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value == null ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
