using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Orbit.Services;
using Serilog;

namespace Orbit.Tabs.Apps;

// Ícone de um app do shell:AppsFolder → PNG em cache (%AppData%\Orbit2\app-icons). Rodar em Sta.Run.
// Extração via IShellItemImageFactory (serve pra app desktop e da Store). O HBITMAP vem 32bpp
// pré-multiplicado; Imaging.CreateBitmapSourceFromHBitmap perderia o alfa (fundo preto),
// então os bytes são copiados à mão.
public static class AppIcons
{
    public static readonly string CacheDir = Path.Combine(SettingsStore.Dir, "app-icons");
    private const int Size = 48;
    private const int SIIGBF_BIGGERSIZEOK = 0x1, SIIGBF_ICONONLY = 0x4;

    // Caminho do PNG (do cache ou recém-extraído), ou null se não deu.
    public static string? GetOrCreate(string id, string cacheDir)
    {
        var path = Path.Combine(cacheDir, AppSearch.IconFileName(id));
        if (File.Exists(path)) return path;
        IShellItemImageFactory? factory = null;
        IntPtr hbmp = IntPtr.Zero;
        try
        {
            SHCreateItemFromParsingName("shell:AppsFolder\\" + id, IntPtr.Zero, typeof(IShellItemImageFactory).GUID, out factory);
            if (factory.GetImage(new SIZE { cx = Size, cy = Size }, SIIGBF_BIGGERSIZEOK | SIIGBF_ICONONLY, out hbmp) != 0 || hbmp == IntPtr.Zero)
                return null;
            var png = ToPng(hbmp);
            if (png is null) return null;
            Directory.CreateDirectory(cacheDir);
            File.WriteAllBytes(path, png);
            return path;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Apps: sem ícone para {Id}", id);
            return null;
        }
        finally
        {
            if (hbmp != IntPtr.Zero) DeleteObject(hbmp);
            if (factory is not null) Marshal.ReleaseComObject(factory);
        }
    }

    private static byte[]? ToPng(IntPtr hbmp)
    {
        var ds = new DIBSECTION();
        if (GetObject(hbmp, Marshal.SizeOf<DIBSECTION>(), ref ds) == 0 || ds.dsBm.bmBits == IntPtr.Zero || ds.dsBm.bmBitsPixel != 32)
            return null;
        int w = ds.dsBm.bmWidth, h = ds.dsBm.bmHeight, stride = ds.dsBm.bmWidthBytes;
        var px = new byte[stride * h];
        Marshal.Copy(ds.dsBm.bmBits, px, 0, px.Length);
        if (ds.dsBmih.biHeight > 0) FlipRows(px, stride, h); // DIB de baixo pra cima
        FixAlpha(px);
        var src = BitmapSource.Create(w, h, 96, 96, PixelFormats.Pbgra32, null, px, stride);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(src));
        using var ms = new MemoryStream();
        enc.Save(ms);
        return ms.ToArray();
    }

    // --- Lógica pura testável ---

    public static void FlipRows(byte[] px, int stride, int height)
    {
        var tmp = new byte[stride];
        for (int top = 0, bottom = height - 1; top < bottom; top++, bottom--)
        {
            Buffer.BlockCopy(px, top * stride, tmp, 0, stride);
            Buffer.BlockCopy(px, bottom * stride, px, top * stride, stride);
            Buffer.BlockCopy(tmp, 0, px, bottom * stride, stride);
        }
    }

    // Ícone sem canal alfa (todos os A = 0) sairia invisível: torna opaco.
    public static void FixAlpha(byte[] bgra)
    {
        for (int i = 3; i < bgra.Length; i += 4)
            if (bgra[i] != 0) return;
        for (int i = 3; i < bgra.Length; i += 4) bgra[i] = 255;
    }

    [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        [PreserveSig] int GetImage(SIZE size, int flags, out IntPtr phbm);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SIZE { public int cx, cy; }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAP
    {
        public int bmType, bmWidth, bmHeight, bmWidthBytes;
        public ushort bmPlanes, bmBitsPixel;
        public IntPtr bmBits;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public uint biSize;
        public int biWidth, biHeight;
        public ushort biPlanes, biBitCount;
        public uint biCompression, biSizeImage;
        public int biXPelsPerMeter, biYPelsPerMeter;
        public uint biClrUsed, biClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DIBSECTION
    {
        public BITMAP dsBm;
        public BITMAPINFOHEADER dsBmih;
        public uint dsBitfields0, dsBitfields1, dsBitfields2;
        public IntPtr dshSection;
        public uint dsOffset;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    private static extern void SHCreateItemFromParsingName(string pszPath, IntPtr pbc,
        [MarshalAs(UnmanagedType.LPStruct)] Guid riid, out IShellItemImageFactory ppv);

    [DllImport("gdi32.dll")] private static extern int GetObject(IntPtr h, int c, ref DIBSECTION pv);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr ho);
}
