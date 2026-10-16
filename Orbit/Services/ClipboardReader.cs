using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Media.Imaging;
using Orbit.Tabs.Clips;
using Serilog;

namespace Orbit.Services;

// O que interessa de um conteúdo do clipboard. Image vem congelada (pode ir pra outra thread).
public sealed record ClipSnapshot(string? Text, BitmapSource? Image, bool IsPrivate);

// Acesso ao clipboard com retry: outro app pode estar segurando (CLIPBRD_E_CANT_OPEN).
// Sempre na UI thread (o clipboard do WPF exige STA).
public static class ClipboardReader
{
    public static ClipSnapshot? Read()
    {
        for (int attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                var data = System.Windows.Clipboard.GetDataObject();
                if (data is null) return null;
                var formats = data.GetFormats();
                if (ClipHistory.IsPrivate(formats, ReadDword(data, "CanIncludeInClipboardHistory")))
                    return new ClipSnapshot(null, null, IsPrivate: true);
                if (data.GetDataPresent(DataFormats.UnicodeText))
                    return new ClipSnapshot(data.GetData(DataFormats.UnicodeText) as string, null, false);
                if (System.Windows.Clipboard.ContainsImage())
                {
                    var img = System.Windows.Clipboard.GetImage();
                    img?.Freeze();
                    return img is null ? null : new ClipSnapshot(null, img, false);
                }
                return null; // arquivos, formatos próprios de apps etc.: fora do histórico
            }
            catch (COMException)
            {
                Thread.Sleep(40);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Clipboard: falha ao ler");
                return null;
            }
        }
        return null;
    }

    public static bool SetText(string text) => Retry(() => System.Windows.Clipboard.SetText(text));
    public static bool SetImage(BitmapSource image) => Retry(() => System.Windows.Clipboard.SetImage(image));

    private static int? ReadDword(IDataObject data, string format)
    {
        try
        {
            if (data.GetDataPresent(format) && data.GetData(format) is MemoryStream ms && ms.Length >= 4)
            {
                var b = new byte[4];
                ms.ReadExactly(b, 0, 4);
                return BitConverter.ToInt32(b, 0);
            }
        }
        catch
        {
            // formato estranho: trata como ausente
        }
        return null;
    }

    private static bool Retry(Action action)
    {
        for (int attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                action();
                return true;
            }
            catch (COMException)
            {
                Thread.Sleep(40);
            }
        }
        Log.Warning("Clipboard: não consegui escrever (ocupado por outro app)");
        return false;
    }
}
