using System;
using System.Text;
using System.Windows.Data;

namespace Olymp.Manager;

[ValueConversion(typeof(string), typeof(byte[]))]
public class TextToBinConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
    {
        return Encoding.Default.GetString((byte[])value);
    }

    public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
    {
        return Encoding.Default.GetBytes((string)value);
    }
}

