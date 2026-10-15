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
                Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "shutdown.exe"), ShutdownArgs(action)!)
                {
                    CreateNoWindow = true,
                    UseShellExecute = false,
                });
                break;
        }
    }

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
