using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Interop;
using Serilog;

namespace Orbit.Island;

// Registra a janela como appbar (ABM_NEW, sem ABM_SETPOS → não reserva espaço na tela)
// só pra receber ABN_FULLSCREENAPP do shell. Zero polling.
public sealed class FullscreenWatcher : IDisposable
{
    private const uint ABM_NEW = 0, ABM_REMOVE = 1;
    private const int ABN_FULLSCREENAPP = 2;
    private const uint MONITOR_DEFAULTTONEAREST = 2;

    private readonly IntPtr _hwnd;
    private readonly HwndSource _source;
    private readonly uint _callbackMsg;
    private readonly uint _taskbarCreatedMsg;

    public event Action<bool>? Changed;

    public FullscreenWatcher(IntPtr hwnd)
    {
        _hwnd = hwnd;
        _callbackMsg = RegisterWindowMessage("Orbit2.AppBar");
        _taskbarCreatedMsg = RegisterWindowMessage("TaskbarCreated");
        _source = HwndSource.FromHwnd(hwnd) ?? throw new InvalidOperationException("FullscreenWatcher precisa ser criado em OnSourceInitialized (HWND do WPF).");
        _source.AddHook(WndProc);
        Register();
    }

    private void Register()
    {
        var data = NewData();
        if (SHAppBarMessage(ABM_NEW, ref data) == UIntPtr.Zero)
            Log.Warning("Appbar não registrado; detecção de tela cheia pode não funcionar");
    }

    // O shell também avisa "tela cheia" quando a área de trabalho/barra ganha foco,
    // e quando o app está em outro monitor — os dois casos não devem esconder a ilha.
    public static bool ShouldHide(bool shellSaysFullscreen, string foregroundClass, bool sameMonitor) =>
        shellSaysFullscreen && sameMonitor &&
        foregroundClass is not ("Progman" or "WorkerW" or "Shell_TrayWnd");

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == (int)_callbackMsg && wParam.ToInt32() == ABN_FULLSCREENAPP)
        {
            var fg = GetForegroundWindow();
            var cls = new StringBuilder(256);
            GetClassName(fg, cls, cls.Capacity);
            bool same = MonitorFromWindow(fg, MONITOR_DEFAULTTONEAREST) == MonitorFromWindow(_hwnd, MONITOR_DEFAULTTONEAREST);
            Changed?.Invoke(ShouldHide(lParam != IntPtr.Zero, cls.ToString(), same));
            handled = true;
        }
        // Quando o explorer.exe reinicia, a lista de appbars se perde.
        else if (msg == (int)_taskbarCreatedMsg) Register();
        return IntPtr.Zero;
    }

    private APPBARDATA NewData() => new()
    {
        cbSize = Marshal.SizeOf<APPBARDATA>(),
        hWnd = _hwnd,
        uCallbackMessage = _callbackMsg,
    };

    public void Dispose()
    {
        _source.RemoveHook(WndProc);
        var data = NewData();
        SHAppBarMessage(ABM_REMOVE, ref data);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct APPBARDATA
    {
        public int cbSize;
        public IntPtr hWnd;
        public uint uCallbackMessage;
        public uint uEdge;
        public RECT rc;
        public IntPtr lParam;
    }

    [DllImport("shell32.dll")] private static extern UIntPtr SHAppBarMessage(uint dwMessage, ref APPBARDATA pData);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterWindowMessage(string lpString);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);
}
