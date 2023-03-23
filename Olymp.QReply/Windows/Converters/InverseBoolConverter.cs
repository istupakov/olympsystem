using System;
using System.Windows.Data;

namespace Olymp.QReply.Windows.Converters;


public class InverseBoolConverter : IValueConverter
{
    #region IValueConverter
    public object? Convert(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
    {
        return value is bool b ? !b : value;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
    {
        return Convert(value, targetType, parameter, culture);
    }

    #endregion
}
