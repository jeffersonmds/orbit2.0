using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

namespace Orbit.Services;

// Id = Screen.DeviceName; Name numerado como em Ajustes (1 = principal).
public sealed record ScreenInfo(string Id, string Name, Rectangle Bounds);

// Qualidade do stream da tela: largura máxima, qualidade JPEG e intervalo entre quadros.
public sealed record ScreenQuality(int MaxWidth, long JpegQuality, int FrameMs)
{
    public static readonly ScreenQuality Leve = new(1280, 60, 100);
    public static readonly ScreenQuality Nitida = new(1920, 75, 150);
    // Resolução nativa: o único jeito de ler texto pequeno com zoom no aparelho (reduzir borra as letras).
    public static readonly ScreenQuality Original = new(int.MaxValue, 90, 250);

    public static ScreenQuality Parse(string? q) => q switch { "nitida" => Nitida, "original" => Original, _ => Leve };
}

// Captura do monitor por GDI (CopyFromScreen) com o cursor desenhado por cima — o GDI não o captura.
// Processo PerMonitorV2: Bounds e cursor em pixels físicos.
// ponytail: GDI a ~10 fps; Windows.Graphics.Capture se precisar de mais fps/menos CPU.
public static class ScreenCapture
{
    private static readonly ImageCodecInfo Jpeg = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);

    public static IReadOnlyList<ScreenInfo> Screens() =>
        Order(System.Windows.Forms.Screen.AllScreens.Select(s => (s.DeviceName, s.Bounds, s.Primary)));

    // Principal primeiro, depois a ordem do Windows; nome "1 · 2560×1600".
    public static IReadOnlyList<ScreenInfo> Order(IEnumerable<(string Id, Rectangle Bounds, bool Primary)> screens) =>
        screens.OrderByDescending(s => s.Primary)
            .Select((s, i) => new ScreenInfo(s.Id, $"{i + 1} · {s.Bounds.Width}×{s.Bounds.Height}", s.Bounds))
            .ToList();

    // Tamanho de saída mantendo a proporção, sem ampliar.
    public static Size Fit(Size source, int maxWidth) =>
        source.Width <= maxWidth ? source : new Size(maxWidth, Math.Max(1, (int)Math.Round(source.Height * (double)maxWidth / source.Width)));

    // JPEG do monitor, ou null se não deu (PC bloqueado: a área de trabalho é outra e o BitBlt falha).
    public static byte[]? Capture(Rectangle bounds, ScreenQuality q)
    {
        try
        {
            using var full = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format24bppRgb);
            using (var g = Graphics.FromImage(full))
            {
                g.CopyFromScreen(bounds.X, bounds.Y, 0, 0, bounds.Size, CopyPixelOperation.SourceCopy);
                DrawCursor(g, bounds);
            }
            var size = Fit(bounds.Size, q.MaxWidth);
            using var scaled = size == bounds.Size ? null : new Bitmap(size.Width, size.Height, PixelFormat.Format24bppRgb);
            if (scaled is not null)
            {
                using var g = Graphics.FromImage(scaled);
                g.InterpolationMode = InterpolationMode.HighQualityBilinear;
                g.DrawImage(full, 0, 0, size.Width, size.Height);
            }
            using var ms = new MemoryStream();
            using var ep = new EncoderParameters(1);
            ep.Param[0] = new EncoderParameter(Encoder.Quality, q.JpegQuality);
            (scaled ?? full).Save(ms, Jpeg, ep);
            return ms.ToArray();
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static void DrawCursor(Graphics g, Rectangle bounds)
    {
        var ci = new CURSORINFO { cbSize = Marshal.SizeOf<CURSORINFO>() };
        if (!GetCursorInfo(ref ci) || (ci.flags & CURSOR_SHOWING) == 0) return;
        if (!bounds.Contains(ci.ptScreenPos.X, ci.ptScreenPos.Y)) return;
        int hx = 0, hy = 0;
        if (GetIconInfo(ci.hCursor, out var ii))
        {
            hx = ii.xHotspot; hy = ii.yHotspot;
            if (ii.hbmMask != IntPtr.Zero) DeleteObject(ii.hbmMask);
            if (ii.hbmColor != IntPtr.Zero) DeleteObject(ii.hbmColor);
        }
        var hdc = g.GetHdc();
        try { DrawIconEx(hdc, ci.ptScreenPos.X - bounds.X - hx, ci.ptScreenPos.Y - bounds.Y - hy, ci.hCursor, 0, 0, 0, IntPtr.Zero, DI_NORMAL); }
        finally { g.ReleaseHdc(hdc); }
    }

    private const int CURSOR_SHOWING = 1, DI_NORMAL = 3;

    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct CURSORINFO { public int cbSize, flags; public IntPtr hCursor; public POINT ptScreenPos; }

    [StructLayout(LayoutKind.Sequential)]
    private struct ICONINFO { public bool fIcon; public int xHotspot, yHotspot; public IntPtr hbmMask, hbmColor; }

    [DllImport("user32.dll")] private static extern bool GetCursorInfo(ref CURSORINFO pci);
    [DllImport("user32.dll")] private static extern bool GetIconInfo(IntPtr hIcon, out ICONINFO piconinfo);
    [DllImport("user32.dll")]
    private static extern bool DrawIconEx(IntPtr hdc, int x, int y, IntPtr hIcon, int cx, int cy, int step, IntPtr brush, int flags);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr hObject);
}
