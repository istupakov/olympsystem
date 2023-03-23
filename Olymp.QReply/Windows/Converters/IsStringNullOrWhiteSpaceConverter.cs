using System;
using System.Windows.Data;

namespace Olymp.QReply.Windows.Converters;

[ValueConversion(typeof(string), typeof(bool))]
public class IsStringNullOrWhiteSpaceConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
    {
        return ConvertValue(value);
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
    {
        return null;
    }

    public static object? ConvertValue(object? value)
    {
        if (value == null)
            return true;

        var str = value as string;
        if (str == null)
            return value;

        if (string.IsNullOrWhiteSpace(str))
            return true;

        return false;
    }
}
