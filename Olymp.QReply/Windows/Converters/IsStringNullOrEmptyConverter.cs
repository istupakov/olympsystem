using System;
using System.Windows.Data;

namespace Olymp.QReply.Windows.Converters;

[ValueConversion(typeof(string), typeof(bool))]
public class IsStringNullOrEmptyConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
    {
        if (value == null)
            return value;

        var str = value as string;
        if (str == null)
            return value;

        if (string.IsNullOrEmpty(str))
            return true;

        return false;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
    {
        return null;
    }
}
