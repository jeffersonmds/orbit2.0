using System;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using Serilog;

namespace Orbit.Services;

// Avisa quando o conteúdo do clipboard muda (WM_CLIPBOARDUPDATE). Zero polling.
// Criar em OnSourceInitialized (precisa do HWND da ilha; funciona com a janela oculta).
public sealed class ClipboardWatcher : IDisposable
{
    private const int WM_CLIPBOARDUPDATE = 0x031D;

    private readonly IntPtr _hwnd;
    private readonly HwndSource _source;

    public event Action? Changed;

    public ClipboardWatcher(IntPtr hwnd)
    {
        _hwnd = hwnd;
        _source = HwndSource.FromHwnd(hwnd) ?? throw new InvalidOperationException("ClipboardWatcher precisa do HWND do WPF.");
        _source.AddHook(WndProc);
        if (!AddClipboardFormatListener(hwnd))
            Log.Warning("Clipboard: listener não registrado (Win32 {Erro})", Marshal.GetLastWin32Error());
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_CLIPBOARDUPDATE) Changed?.Invoke();
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        _source.RemoveHook(WndProc);
        RemoveClipboardFormatListener(_hwnd);
    }

    [DllImport("user32.dll", SetLastError = true)] private static extern bool AddClipboardFormatListener(IntPtr hwnd);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool RemoveClipboardFormatListener(IntPtr hwnd);
}
