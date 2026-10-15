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

    // true = esconder, false = mostrar, null = ignorar o aviso.
    // A área de trabalho/barra também dispara "tela cheia" (falso positivo → mostrar).
    // App em tela cheia em OUTRO monitor não diz respeito à ilha: ignorar, senão um vídeo
    // no secundário faria a ilha reaparecer sobre um jogo no monitor dela.
    // ponytail: o aviso de SAÍDA não diz de qual monitor veio; tratado sempre como "mostrar".
    public static bool? Decide(bool shellSaysFullscreen, string foregroundClass, bool sameMonitor)
    {
        if (!shellSaysFullscreen) return false;
        if (foregroundClass is "Progman" or "WorkerW" or "Shell_TrayWnd") return false;
        return sameMonitor ? true : null;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == (int)_callbackMsg && wParam.ToInt32() == ABN_FULLSCREENAPP)
        {
            var fg = GetForegroundWindow();
            var cls = new StringBuilder(256);
            GetClassName(fg, cls, cls.Capacity);
            bool same = MonitorFromWindow(fg, MONITOR_DEFAULTTONEAREST) == MonitorFromWindow(_hwnd, MONITOR_DEFAULTTONEAREST);
            if (Decide(lParam != IntPtr.Zero, cls.ToString(), same) is bool hide) Changed?.Invoke(hide);
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
