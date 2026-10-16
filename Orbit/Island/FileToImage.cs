using System;
using System.Globalization;
using System.IO;
using System.Windows.Data;
using System.Windows.Media.Imaging;

namespace Orbit.Island;

// Caminho de PNG → BitmapImage já reduzido (DecodeWidth) e sem segurar o arquivo.
// Usado nas miniaturas do Clipboard e nos ícones de Apps: só existe enquanto a view existe.
public sealed class FileToImage : IValueConverter
{
    public int DecodeWidth { get; set; } = 96;

    public object? Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not string path || !File.Exists(path)) return null;
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
            bmp.DecodePixelWidth = DecodeWidth;
            bmp.UriSource = new Uri(path);
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch
        {
            return null; // PNG corrompido: a view mostra o glifo
        }
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
