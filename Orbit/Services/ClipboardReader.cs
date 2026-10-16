using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
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
        // Uma tentativa só: o WPF já refaz internamente por cerca de 1 s.
        try
        {
            var data = System.Windows.Clipboard.GetDataObject();
            if (data is null) return null;
            var formats = data.GetFormats();
            if (ClipHistory.IsPrivate(formats, ReadDword(data, "CanIncludeInClipboardHistory")))
                return new ClipSnapshot(null, null, IsPrivate: true);
            string? text = data.GetDataPresent(DataFormats.UnicodeText) ? data.GetData(DataFormats.UnicodeText) as string : null;
            // Texto tem precedência (Excel/Word), exceto URL + imagem (Firefox "Copiar imagem").
            bool preferImage = text is not null && ClipHistory.ClassifyText(text) == ClipKind.Link;
            if (text is not null && !(preferImage && HasImage(data)))
                return new ClipSnapshot(text, null, false);
            var img = ReadImage(data);
            if (img is not null) return new ClipSnapshot(null, img, false);
            return text is null ? null : new ClipSnapshot(text, null, false); // arquivos, formatos próprios: fora do histórico
        }
        catch (COMException ex)
        {
            Log.Warning(ex, "Clipboard: ocupado por outro app");
            return null;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Clipboard: falha ao ler");
            return null;
        }
    }

    private static bool HasImage(IDataObject data) =>
        data.GetDataPresent("PNG") || System.Windows.Clipboard.ContainsImage();

    private static BitmapSource? ReadImage(IDataObject data)
    {
        // PNG preserva o alfa verdadeiro.
        if (data.GetDataPresent("PNG") && data.GetData("PNG") is MemoryStream png)
        {
            var frame = BitmapFrame.Create(png, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            frame.Freeze();
            return frame;
        }
        if (!System.Windows.Clipboard.ContainsImage()) return null;
        var img = System.Windows.Clipboard.GetImage();
        if (img is null) return null;
        // HBITMAP de print vem com alfa 0: Bgr32 descarta o canal (fica opaco).
        var opaque = new FormatConvertedBitmap(img, PixelFormats.Bgr32, null, 0);
        opaque.Freeze();
        return opaque;
    }

    // O clipboard é nosso (acabamos de colar um item do histórico)?
    public static bool OwnedByUs()
    {
        var owner = GetClipboardOwner();
        if (owner == IntPtr.Zero) return false;
        GetWindowThreadProcessId(owner, out var pid);
        return pid == (uint)Environment.ProcessId;
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
        try
        {
            action();
            return true;
        }
        catch (COMException ex)
        {
            Log.Warning(ex, "Clipboard: não consegui escrever (ocupado por outro app)");
            return false;
        }
    }

    [DllImport("user32.dll")] private static extern IntPtr GetClipboardOwner();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
}
