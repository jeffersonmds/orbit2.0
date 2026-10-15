using System;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using Serilog;

namespace Orbit.Services;

// Registra Ctrl+Alt+O global e dispara Pressed quando acionado.
// ponytail: atalho fixo; vira configuração em Ajustes se alguém precisar trocar.
public sealed class GlobalHotKey : IDisposable
{
    private const int WM_HOTKEY = 0x0312;
    private const uint MOD_ALT = 0x0001, MOD_CONTROL = 0x0002, MOD_NOREPEAT = 0x4000;
    private const uint VK_O = 0x4F;
    private const int Id = 0xB0C;

    private readonly IntPtr _hwnd;
    private readonly HwndSource _source;
    public event Action? Pressed;

    public GlobalHotKey(IntPtr hwnd)
    {
        _hwnd = hwnd;
        _source = HwndSource.FromHwnd(hwnd)!;
        _source.AddHook(WndProc);
        // Se falhar (combinação em uso), só loga: ainda dá pra abrir pelo hover.
        if (!RegisterHotKey(hwnd, Id, MOD_CONTROL | MOD_ALT | MOD_NOREPEAT, VK_O))
            Log.Warning("Hotkey Ctrl+Alt+O indisponível (Win32 {Erro}) — outro app (Orbit 1.0?) já usa", Marshal.GetLastWin32Error());
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY && wParam.ToInt32() == Id)
        {
            Pressed?.Invoke();
            handled = true;
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        _source.RemoveHook(WndProc);
        UnregisterHotKey(_hwnd, Id);
    }

    [DllImport("user32.dll", SetLastError = true)] private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}
