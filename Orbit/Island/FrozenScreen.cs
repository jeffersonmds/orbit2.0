using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Orbit.Island;

// Foto da área de trabalho inteira (todos os monitores) numa janela por cima de tudo — base do
// conta-gotas e do desenhar na tela. Congelar em vez de janela transparente: a cor lida é a do pixel
// que se vê, e o desenho não paga uma janela em camadas gigante repintando a cada traço.
// Processo PerMonitorV2: tudo em pixels físicos; a imagem preenche a janela, que cobre o retângulo virtual.
public class FrozenScreen : Window
{
    public Rectangle Bounds { get; }           // retângulo virtual (pixels físicos)
    public BitmapSource Shot { get; }
    private readonly byte[] _pixels;           // BGRA, pra ler a cor sem ir à GPU
    private readonly int _stride;

    public FrozenScreen()
    {
        Bounds = System.Windows.Forms.SystemInformation.VirtualScreen;
        using var bmp = new Bitmap(Bounds.Width, Bounds.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp)) g.CopyFromScreen(Bounds.X, Bounds.Y, 0, 0, Bounds.Size, CopyPixelOperation.SourceCopy);
        var data = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height), ImageLockMode.ReadOnly, bmp.PixelFormat);
        _stride = data.Stride;
        _pixels = new byte[_stride * bmp.Height];
        Marshal.Copy(data.Scan0, _pixels, 0, _pixels.Length);
        bmp.UnlockBits(data);
        Shot = BitmapSource.Create(bmp.Width, bmp.Height, 96, 96, PixelFormats.Bgra32, null, _pixels, _stride);
        Shot.Freeze();

        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        Background = System.Windows.Media.Brushes.Black;
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { e.Handled = true; Close(); } };
        // Cada clique devolve o foco do teclado pra cá: o Esc sempre chega (cobrimos a tela inteira).
        PreviewMouseDown += (_, _) => { if (!IsActive) Activate(); };
        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            SetWindowPos(hwnd, new IntPtr(-1), Bounds.X, Bounds.Y, Bounds.Width, Bounds.Height, 0x0040); // HWND_TOPMOST, SWP_SHOWWINDOW
        };
        Loaded += (_, _) => { Activate(); Focus(); };
    }

    // Pixel físico do cursor dentro da foto (x, y), ou null fora dela.
    public (int X, int Y)? CursorPixel()
    {
        if (!GetCursorPos(out var p)) return null;
        int x = p.X - Bounds.X, y = p.Y - Bounds.Y;
        return x >= 0 && y >= 0 && x < Bounds.Width && y < Bounds.Height ? (x, y) : null;
    }

    public System.Windows.Media.Color ColorAt(int x, int y)
    {
        int i = y * _stride + x * 4;
        return System.Windows.Media.Color.FromRgb(_pixels[i + 2], _pixels[i + 1], _pixels[i]);
    }

    public static string Hex(System.Windows.Media.Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    // Ponto da tela física → coordenadas da janela (DIP), pra posicionar a lupa perto do cursor.
    public System.Windows.Point ToWindow(int physX, int physY) => PointFromScreen(new System.Windows.Point(physX, physY));

    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
}
