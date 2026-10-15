using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Orbit.Island;

// 0..1 → GridLength em estrelas. ConverterParameter="rest" devolve o complemento (1 - f).
public sealed class FractionToStar : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        double f = value is double d ? Math.Clamp(d, 0, 1) : 0;
        if (parameter as string == "rest") f = 1 - f;
        return new GridLength(Math.Max(f, 0.0001), GridUnitType.Star);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
