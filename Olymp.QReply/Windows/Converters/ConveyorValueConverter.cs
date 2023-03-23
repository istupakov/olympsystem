using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Data;

namespace Olymp.QReply.Windows.Converters;

[ValueConversion(typeof(object), typeof(object))]
public class ConveyorValueConverter : IValueConverter
{
    public ObservableCollection<IValueConverter> Converters { get; set; }

    public ConveyorValueConverter()
    {
        Converters = new ObservableCollection<IValueConverter>();
    }

    public object? Convert(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
    {
        if (Converters == null)
        {
            return value;
        }
        object? result = value;
        foreach (var converter in Converters)
        {
            result = converter.Convert(result, targetType, parameter, culture);
        }
        return result;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
    {
        if (Converters == null)
        {
            return value;
        }
        object? result = value;
        foreach (var converter in Converters.Reverse())
        {
            result = converter.ConvertBack(result, targetType, parameter, culture);
        }
        return result;
    }
}
