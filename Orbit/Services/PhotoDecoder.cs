using System;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Orbit.Services;

// Foto do aparelho pro clipboard. O iPhone grava a rotação só no EXIF (Orientation) e o WPF ignora:
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

    // Teto de memória: 48 MP do iPhone (~190 MB em BGRA) passa inteiro; acima disso reduz na decodificação.
    public const long MaxPixels = 50_000_000;
    // Acima disso nem tenta (formatos sem redução na decodificação, como PNG, gastariam a imagem inteira).
    public const long RefusePixels = 250_000_000;

    // Largura de decodificação: a original, ou reduzida pra caber em MaxPixels mantendo a proporção.
    public static int DecodeWidth(int width, int height) =>
        (long)width * height <= MaxPixels ? width : Math.Max(1, (int)(width * Math.Sqrt(MaxPixels / ((double)width * height))));

    // Decodifica (congelado, pronto pro clipboard) já na orientação certa. Lança se não for imagem ou se for
    // grande demais. Roda em qualquer thread (fora do Dispatcher, pra não travar a ilha).
    public static BitmapSource Decode(byte[] bytes)
    {
        // Só o cabeçalho: tamanho e EXIF antes de alocar os pixels.
        var header = BitmapDecoder.Create(new MemoryStream(bytes), BitmapCreateOptions.DelayCreation, BitmapCacheOption.None).Frames[0];
        int w = header.PixelWidth, h = header.PixelHeight;
        if ((long)w * h > RefusePixels) throw new InvalidOperationException($"imagem grande demais ({w}×{h})");
        int orientation = 1;
        try
        {
            if (header.Metadata is BitmapMetadata m && m.GetQuery("System.Photo.Orientation") is ushort o) orientation = o;
        }
        catch { /* sem EXIF (PNG, captura de tela) */ }
        BitmapSource frame;
        var dw = DecodeWidth(w, h);
        if (dw == w)
            frame = BitmapDecoder.Create(new MemoryStream(bytes), BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
        else
        {
            var img = new BitmapImage();
            img.BeginInit();
            img.StreamSource = new MemoryStream(bytes);
            img.DecodePixelWidth = dw; // JPEG reduz já na decodificação (não carrega os pixels todos)
            img.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            img.CacheOption = BitmapCacheOption.OnLoad;
            img.EndInit();
            frame = img;
        }
        BitmapSource result = frame;
        var angle = Rotation(orientation);
        if (angle != 0) result = new TransformedBitmap(frame, new RotateTransform(angle));
        result.Freeze();
        return result;
    }
}
