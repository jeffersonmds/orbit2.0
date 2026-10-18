using System;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Orbit.Services;

// Foto do iPhone pro clipboard. O iPhone grava a rotação só no EXIF (Orientation) e o WPF ignora:
// sem girar aqui, foto tirada em pé colaria deitada.
public static class PhotoDecoder
{
    // EXIF Orientation → graus no sentido horário (as variantes espelhadas são raras em foto de câmera).
    public static int Rotation(int exifOrientation) => exifOrientation switch
    {
        3 => 180,
        6 => 90,
        8 => 270,
        _ => 0,
    };

    // Decodifica (congelado, pronto pro clipboard) já na orientação certa. Lança se não for imagem.
    public static BitmapSource Decode(byte[] bytes)
    {
        var frame = BitmapDecoder.Create(new MemoryStream(bytes), BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
        int orientation = 1;
        try
        {
            if (frame.Metadata is BitmapMetadata m && m.GetQuery("System.Photo.Orientation") is ushort o) orientation = o;
        }
        catch { /* sem EXIF (PNG, captura de tela) */ }
        BitmapSource result = frame;
        var angle = Rotation(orientation);
        if (angle != 0) result = new TransformedBitmap(frame, new RotateTransform(angle));
        result.Freeze();
        return result;
    }
}
