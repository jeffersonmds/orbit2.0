using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace Orbit.Services;

public sealed record OpenWindow(long Id, string Title, string App);

// Janelas que o Alt+Tab mostraria: visíveis, com título, sem dono, não ferramenta, não "cloaked"
// (UWP suspenso, outra área de trabalho virtual). Pro aparelho trocar de janela sem Alt+Tab às cegas.
public static class OpenWindows
{
    public static IReadOnlyList<OpenWindow> List()
    {
        var list = new List<OpenWindow>();
        var self = (uint)Environment.ProcessId;
        EnumWindows((h, _) =>
        {
            try
            {
                if (!IsWindowVisible(h) || GetWindow(h, GW_OWNER) != IntPtr.Zero) return true;
                if ((GetWindowLong(h, GWL_EXSTYLE) & WS_EX_TOOLWINDOW) != 0) return true;
                if (DwmGetWindowAttribute(h, DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0) return true;
                int len = GetWindowTextLength(h);
                if (len == 0) return true;
                var sb = new StringBuilder(len + 1);
                GetWindowText(h, sb, sb.Capacity);
                GetWindowThreadProcessId(h, out var pid);
                if (pid == self) return true; // a própria ilha
                list.Add(new OpenWindow(h.ToInt64(), sb.ToString(), AppName((int)pid)));
            }
            catch { /* janela sumiu no meio: ignora */ }
            return true;
        }, IntPtr.Zero);
        return list;
    }

    private static string AppName(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            try { if (p.MainModule?.FileVersionInfo.FileDescription is { Length: > 0 } d) return d; } catch { }
            return p.ProcessName;
        }
        catch { return ""; }
    }

    // Só janelas que a lista mostraria (id qualquer vindo do aparelho não vira SetForegroundWindow).
    // O Windows só deixa trazer pra frente quem recebeu entrada por último: um Alt "fantasma" destrava.
    public static bool Focus(long id)
    {
        if (!System.Linq.Enumerable.Any(List(), w => w.Id == id)) return false;
        var h = new IntPtr(id);
        if (IsIconic(h)) ShowWindow(h, SW_RESTORE);
        keybd_event(VK_MENU, 0, 0, UIntPtr.Zero);
        keybd_event(VK_MENU, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
        return SetForegroundWindow(h);
    }

    private delegate bool EnumProc(IntPtr h, IntPtr l);
    private const int GW_OWNER = 4, GWL_EXSTYLE = -20, WS_EX_TOOLWINDOW = 0x80, DWMWA_CLOAKED = 14, SW_RESTORE = 9;
    private const byte VK_MENU = 0x12;
    private const uint KEYEVENTF_KEYUP = 0x2;

    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc cb, IntPtr l);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr h, int i);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowTextLength(IntPtr h);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr h, StringBuilder s, int max);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr h);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] private static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr h, int attr, out int value, int size);
}
