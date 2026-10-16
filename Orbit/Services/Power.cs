using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Serilog;

namespace Orbit.Services;

public enum PowerAction { Bloquear, Suspender, Reiniciar, Desligar }

// Ações de energia. Usado pela aba Energia e, na fase 4, pelos endpoints /power/* da ponte.
public static class Power
{
    public static bool NeedsConfirmation(PowerAction action) =>
        action is PowerAction.Reiniciar or PowerAction.Desligar;

    public static string? ShutdownArgs(PowerAction action) => action switch
    {
        PowerAction.Reiniciar => "/r /t 0",
        PowerAction.Desligar => "/s /t 0",
        _ => null,
    };

    public static void Execute(PowerAction action)
    {
        switch (action)
        {
            case PowerAction.Bloquear:
                if (!LockWorkStation()) LogFalha(action);
                break;
            case PowerAction.Suspender:
                if (!SetSuspendState(hibernate: false, forceCritical: false, disableWakeEvent: false)) LogFalha(action);
                break;
            default:
                RunShutdown(ShutdownArgs(action)!, wait: false);
                break;
        }
    }

    // shutdown /s /t N. Atenção: com N > 0 o Windows implica /f (fecha programas sem perguntar).
    public static string ScheduleArgs(TimeSpan delay) => $"/s /t {(int)Math.Clamp(delay.TotalSeconds, 1, 315_360_000)}";

    public static void Schedule(TimeSpan delay)
    {
        RunShutdown("/a", wait: true); // já havia um agendado? o Windows recusaria o novo
        RunShutdown(ScheduleArgs(delay), wait: true);
    }

    public static void CancelScheduled() => RunShutdown("/a", wait: true);

    // Apaga o monitor (mexer o mouse/teclado acende). PostMessage: não trava se alguma janela estiver presa.
    public static void ScreenOff() => PostMessage(new IntPtr(0xFFFF), 0x0112, new IntPtr(0xF170), new IntPtr(2));

    private static void RunShutdown(string args, bool wait)
    {
        using var p = Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "shutdown.exe"), args)
        {
            CreateNoWindow = true,
            UseShellExecute = false,
        });
        if (wait) p?.WaitForExit(5000);
    }

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    private static void LogFalha(PowerAction action) =>
        Log.Warning("{Acao} falhou (Win32 {Erro})", action, Marshal.GetLastWin32Error());

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool LockWorkStation();

    [DllImport("powrprof.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.U1)]
    private static extern bool SetSuspendState(
        [MarshalAs(UnmanagedType.U1)] bool hibernate,
        [MarshalAs(UnmanagedType.U1)] bool forceCritical,
        [MarshalAs(UnmanagedType.U1)] bool disableWakeEvent);
}
