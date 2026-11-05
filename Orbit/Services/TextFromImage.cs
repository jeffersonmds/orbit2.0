using System;
using System.Linq;
using System.Threading.Tasks;
using Serilog;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage;

namespace Orbit.Services;

// OCR com o leitor de texto que já vem no Windows (Windows.Media.Ocr): sem pacote, sem internet.
// Usa os idiomas do perfil do usuário (português e inglês na prática).
public static class TextFromImage
{
    // null = OCR indisponível (nenhum idioma instalado) ou arquivo ilegível; "" = imagem sem texto.
    public static async Task<string?> FromFileAsync(string path)
    {
        try
        {
            var engine = OcrEngine.TryCreateFromUserProfileLanguages();
            if (engine is null) { Log.Warning("OCR: nenhum idioma de OCR instalado"); return null; }
            var file = await StorageFile.GetFileFromPathAsync(path);
            using var stream = await file.OpenAsync(FileAccessMode.Read);
            var decoder = await BitmapDecoder.CreateAsync(stream);
            // O OCR recusa imagem maior que MaxImageDimension: reduz mantendo a proporção.
            double k = Math.Min(1.0, OcrEngine.MaxImageDimension / (double)Math.Max(decoder.PixelWidth, decoder.PixelHeight));
            var transform = new BitmapTransform
            {
                ScaledWidth = (uint)Math.Max(1, decoder.PixelWidth * k),
                ScaledHeight = (uint)Math.Max(1, decoder.PixelHeight * k),
                InterpolationMode = BitmapInterpolationMode.Fant,
            };
            using var bmp = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied,
                transform, ExifOrientationMode.RespectExifOrientation, ColorManagementMode.DoNotColorManage);
            var result = await engine.RecognizeAsync(bmp);
            return string.Join(Environment.NewLine, result.Lines.Select(l => l.Text));
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "OCR: falhou em {Arquivo}", path);
            return null;
        }
    }

    // Texto pronto pra mensagem curta: "3 linhas copiadas" / "Nenhum texto na imagem".
    public static string Summary(string? text)
    {
        if (text is null) return "Não consegui ler a imagem";
        if (text.Length == 0) return "Nenhum texto na imagem";
        int n = text.Split('\n').Length;
        return n == 1 ? "1 linha copiada" : $"{n} linhas copiadas";
    }
}
