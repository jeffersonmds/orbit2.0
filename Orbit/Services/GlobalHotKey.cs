using System;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using Serilog;

namespace Orbit.Services;

// Registra um Ctrl+Alt+<tecla> global e dispara Pressed. Usos: Ctrl+Alt+O (abrir a ilha),
// Ctrl+Alt+I (mandar a seleção pro iPhone), Ctrl+Alt+C (conta-gotas) e Ctrl+Alt+D (desenhar na tela).
// ponytail: atalhos fixos; viram configuração em Ajustes se alguém precisar trocar.
public sealed class GlobalHotKey : IDisposable
{
    private const int WM_HOTKEY = 0x0312;
    private const uint MOD_ALT = 0x0001, MOD_CONTROL = 0x0002, MOD_NOREPEAT = 0x4000;
    public const uint VK_O = 0x4F, VK_I = 0x49, VK_C = 0x43, VK_D = 0x44;

    private readonly IntPtr _hwnd;
    private readonly int _id;
    private readonly HwndSource _source;
    public event Action? Pressed;

    public GlobalHotKey(IntPtr hwnd, uint vk = VK_O)
    {
        _hwnd = hwnd;
        _id = 0xB00 + (int)vk; // um id por tecla
        _source = HwndSource.FromHwnd(hwnd)!;
        _source.AddHook(WndProc);
        // Se falhar (combinação em uso), só loga: ainda dá pra usar pelo mouse.
        if (!RegisterHotKey(hwnd, _id, MOD_CONTROL | MOD_ALT | MOD_NOREPEAT, vk))
            Log.Warning("Hotkey Ctrl+Alt+{Tecla} indisponível (Win32 {Erro}) — outro app (Orbit 1.0?) já usa", (char)vk, Marshal.GetLastWin32Error());
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY && wParam.ToInt32() == _id)
        {
            Pressed?.Invoke();
            handled = true;
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        _source.RemoveHook(WndProc);
        UnregisterHotKey(_hwnd, _id);
    }

    [DllImport("user32.dll", SetLastError = true)] private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}
