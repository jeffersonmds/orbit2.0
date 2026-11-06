using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading;
using Serilog;
using PropertyType = Windows.Foundation.PropertyType;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace Orbit.Services;

// Captura pelo Windows.Graphics.Capture: a GPU copia a tela, o quadro só chega quando algo muda e já
// vem com o cursor. Redução e JPEG pelo WIC do Windows (bem mais rápido que o GDI+). Uma captura por
// monitor, aberta no 1º pedido e fechada depois de 10 s sem uso. Falhou? null e o ScreenCapture cai no GDI.
public static class WgcCapture
{
    private static readonly object Gate = new();
    private static readonly Dictionary<Rectangle, Monitor> Open = new();
    private static IDirect3DDevice? _device;
    private static bool _broken;             // sem suporte/placa: GDI até reiniciar
    private static DateTime _retryAt;        // falha ao abrir um monitor: tenta de novo depois
    private static Timer? _reaper;

    // JPEG do monitor; null = WGC indisponível (usa o GDI). Locked = PC bloqueado (tela congelada).
    public static byte[]? Capture(Rectangle bounds, ScreenQuality q, out bool locked)
    {
        locked = false;
        Monitor? m;
        lock (Gate)
        {
            if (_broken) return null;
            if (Open.TryGetValue(bounds, out m) && m.Closed) { Open.Remove(bounds); m.Dispose(); m = null; } // monitor saiu
            if (m is null)
            {
                if (DateTime.UtcNow < _retryAt) return null;
                IDirect3DDevice device;
                try { device = Device(); }
                catch (Exception ex)
                {
                    _broken = true;
                    Log.Warning(ex, "Tela: Windows.Graphics.Capture indisponível, usando GDI");
                    return null;
                }
                try { m = new Monitor(device, bounds); }
                catch (Exception ex)
                {
                    _retryAt = DateTime.UtcNow.AddSeconds(5); // monitor sumindo/voltando: GDI por enquanto
                    Log.Warning(ex, "Tela: não abri a captura do monitor {Bounds}", bounds);
                    return null;
                }
                Open[bounds] = m;
                _reaper ??= new Timer(_ => Reap(), null, 5000, 5000);
            }
        }
        try { return m.Jpeg(q, out locked); }
        catch (Exception ex)
        {
            Log.Warning(ex, "Tela: quadro do WGC falhou");
            lock (Gate) { if (Open.Remove(bounds)) m.Dispose(); }
            return null;
        }
    }

    private static void Reap()
    {
        lock (Gate)
            foreach (var (b, m) in new List<KeyValuePair<Rectangle, Monitor>>(Open))
                if (DateTime.UtcNow - m.LastUsed > TimeSpan.FromSeconds(10)) { Open.Remove(b); m.Dispose(); }
    }

    private static IDirect3DDevice Device()
    {
        if (_device is not null) return _device;
        if (!GraphicsCaptureSession.IsSupported()) throw new NotSupportedException("WGC");
        Check(D3D11CreateDevice(IntPtr.Zero, 1 /* HARDWARE */, IntPtr.Zero, 0x20 /* BGRA */, IntPtr.Zero, 0, 7, out var d3d, out _, out var ctx));
        try
        {
            var iid = new Guid("54ec77fa-1377-44e6-8c32-88fd5f44c84c"); // IDXGIDevice
            Check(Marshal.QueryInterface(d3d, in iid, out var dxgi));
            try
            {
                Check(CreateDirect3D11DeviceFromDXGIDevice(dxgi, out var insp));
                try { _device = WinRT.MarshalInterface<IDirect3DDevice>.FromAbi(insp); }
                finally { Marshal.Release(insp); }
            }
            finally { Marshal.Release(dxgi); }
        }
        finally { Marshal.Release(d3d); if (ctx != IntPtr.Zero) Marshal.Release(ctx); }
        // Sem a moldura amarela em volta do monitor (Windows 11; app sem pacote é liberado sem perguntar).
        if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 20348))
            try { GraphicsCaptureAccess.RequestAccessAsync(GraphicsCaptureAccessKind.Borderless).AsTask().Wait(2000); } catch { }
        return _device;
    }

    private static void Check(int hr) { if (hr < 0) Marshal.ThrowExceptionForHR(hr); }

    private sealed class Monitor : IDisposable
    {
        private readonly object _lock = new();
        private readonly IDirect3DDevice _device;
        private readonly Rectangle _bounds;
        private readonly Direct3D11CaptureFramePool _pool;
        private readonly GraphicsCaptureSession? _session;
        private readonly GraphicsCaptureItem _item;
        private bool _disposed;
        private Direct3D11CaptureFrame? _latest;
        private Windows.Graphics.SizeInt32 _size;
        private long _version, _encodedVersion = -1;
        private ScreenQuality? _encodedQuality;
        private byte[]? _encoded;
        private DateTime _lastFrame;
        public DateTime LastUsed = DateTime.UtcNow;
        public volatile bool Closed; // o Windows fechou o alvo (monitor desconectado)

        public Monitor(IDirect3DDevice device, Rectangle bounds)
        {
            _device = device;
            _bounds = bounds;
            var hmon = MonitorFromPoint(new POINT { X = bounds.X + bounds.Width / 2, Y = bounds.Y + bounds.Height / 2 }, 0);
            if (hmon == IntPtr.Zero) throw new InvalidOperationException("monitor sumiu");
            var factory = WinRT.ActivationFactory.Get("Windows.Graphics.Capture.GraphicsCaptureItem");
            var interop = factory.AsInterface<IGraphicsCaptureItemInterop>();
            var iid = new Guid("79C3F95B-31F7-4EC2-A464-632EF5D30760"); // IGraphicsCaptureItem
            var ptr = interop.CreateForMonitor(hmon, in iid);
            try { _item = GraphicsCaptureItem.FromAbi(ptr); }
            finally { Marshal.Release(ptr); }
            _item.Closed += OnClosed;
            _size = _item.Size;
            _pool = Direct3D11CaptureFramePool.CreateFreeThreaded(device, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, _size);
            _pool.FrameArrived += OnFrame;
            try
            {
                _session = _pool.CreateCaptureSession(_item);
                if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 20348))
                    try { _session.IsBorderRequired = false; } catch { /* Windows 10: fica a moldura */ }
                _session.StartCapture();
            }
            catch
            {
                Dispose(); // não deixa pool, sessão e inscrições pendurados
                throw;
            }
        }

        private void OnClosed(GraphicsCaptureItem _, object __) => Closed = true;

        private void OnFrame(Direct3D11CaptureFramePool pool, object _)
        {
            lock (_lock)
            {
                if (_disposed) return; // callback atrasado depois de fechar
                var f = pool.TryGetNextFrame();
                if (f is null) return;
                if (f.ContentSize.Width != _size.Width || f.ContentSize.Height != _size.Height)
                {
                    // Resolução mudou: a superfície ainda tem o tamanho velho. Joga fora e espera um quadro do novo.
                    _size = f.ContentSize;
                    f.Dispose();
                    _latest?.Dispose();
                    _latest = null;
                    pool.Recreate(_device, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, _size);
                    return;
                }
                _latest?.Dispose();
                _latest = f;
                _version++;
                _lastFrame = DateTime.UtcNow;
            }
        }

        public byte[]? Jpeg(ScreenQuality q, out bool locked)
        {
            locked = false;
            LastUsed = DateTime.UtcNow;
            // Tudo sob o lock: dois aparelhos (troca no meio) não publicam o JPEG de um quadro como se fosse de outro.
            lock (_lock)
            {
                if (_disposed) throw new ObjectDisposedException(nameof(Monitor));
                if (_latest is null) return ScreenCapture.Gdi(_bounds, q); // ainda sem o 1º quadro
                if (_version == _encodedVersion && q == _encodedQuality)
                {
                    // Nada mudou. Parado há mais de 1 s pode ser o PC bloqueado (a tela de bloqueio não é capturada).
                    if (DateTime.UtcNow - _lastFrame > TimeSpan.FromSeconds(1) && !InputDesktopIsDefault()) { locked = true; return null; }
                    return _encoded;
                }
                using var bmp = SoftwareBitmap.CreateCopyFromSurfaceAsync(_latest.Surface, BitmapAlphaMode.Ignore).AsTask().GetAwaiter().GetResult();
                _encoded = Encode(bmp, q);
                _encodedVersion = _version;
                _encodedQuality = q;
                return _encoded;
            }
        }

        private static byte[] Encode(SoftwareBitmap bmp, ScreenQuality q)
        {
            using var stream = new InMemoryRandomAccessStream();
            var props = new BitmapPropertySet { ["ImageQuality"] = new BitmapTypedValue((float)(q.JpegQuality / 100.0), PropertyType.Single) };
            var enc = BitmapEncoder.CreateAsync(BitmapEncoder.JpegEncoderId, stream, props).AsTask().GetAwaiter().GetResult();
            enc.SetSoftwareBitmap(bmp);
            var size = ScreenCapture.Fit(new Size(bmp.PixelWidth, bmp.PixelHeight), q.MaxWidth);
            if (size.Width != bmp.PixelWidth)
            {
                enc.BitmapTransform.ScaledWidth = (uint)size.Width;
                enc.BitmapTransform.ScaledHeight = (uint)size.Height;
                enc.BitmapTransform.InterpolationMode = BitmapInterpolationMode.Linear;
            }
            enc.FlushAsync().AsTask().GetAwaiter().GetResult();
            var bytes = new byte[stream.Size];
            stream.Seek(0);
            stream.ReadAsync(bytes.AsBuffer(), (uint)bytes.Length, InputStreamOptions.None).AsTask().GetAwaiter().GetResult();
            return bytes;
        }

        public void Dispose()
        {
            lock (_lock)
            {
                if (_disposed) return;
                _disposed = true; // OnFrame atrasado vê isso e não mexe mais no pool
                _latest?.Dispose();
                _latest = null;
            }
            _item.Closed -= OnClosed;
            _pool.FrameArrived -= OnFrame;
            _session?.Dispose();
            _pool.Dispose();
        }
    }

    // "Default" = área de trabalho do usuário; bloqueado é "Winlogon" (ou nem dá pra abrir).
    private static bool InputDesktopIsDefault()
    {
        var h = OpenInputDesktop(0, false, 0);
        if (h == IntPtr.Zero) return false;
        try
        {
            var buf = new char[64];
            return GetUserObjectInformation(h, 2 /* UOI_NAME */, buf, buf.Length * 2, out _)
                && new string(buf).TrimEnd('\0').Equals("Default", StringComparison.OrdinalIgnoreCase);
        }
        finally { CloseDesktop(h); }
    }

    [ComImport, Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IGraphicsCaptureItemInterop
    {
        IntPtr CreateForWindow(IntPtr window, in Guid iid);
        IntPtr CreateForMonitor(IntPtr monitor, in Guid iid);
    }

    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }

    [DllImport("d3d11.dll")]
    private static extern int D3D11CreateDevice(IntPtr adapter, int driverType, IntPtr software, uint flags, IntPtr levels,
        uint numLevels, uint sdkVersion, out IntPtr device, out int level, out IntPtr context);
    [DllImport("d3d11.dll")] private static extern int CreateDirect3D11DeviceFromDXGIDevice(IntPtr dxgiDevice, out IntPtr graphicsDevice);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromPoint(POINT pt, uint flags);
    [DllImport("user32.dll")] private static extern IntPtr OpenInputDesktop(uint flags, bool inherit, uint access);
    [DllImport("user32.dll")] private static extern bool CloseDesktop(IntPtr desktop);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetUserObjectInformation(IntPtr obj, int index, [Out] char[] info, int length, out int needed);
}
