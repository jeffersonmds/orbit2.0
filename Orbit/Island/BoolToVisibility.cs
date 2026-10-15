using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Orbit.Island;

// true → Visible, resto → Collapsed. Invert=True inverte (pra "mostrar quando falso").
public sealed class BoolToVisibility : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        (value is true) ^ Invert ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
