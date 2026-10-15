using System.Diagnostics;
using System.Runtime.InteropServices;

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
                LockWorkStation();
                break;
            case PowerAction.Suspender:
                SetSuspendState(hibernate: false, forceCritical: false, disableWakeEvent: false);
                break;
            default:
                Process.Start(new ProcessStartInfo("shutdown", ShutdownArgs(action)!)
                {
                    CreateNoWindow = true,
                    UseShellExecute = false,
                });
                break;
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool LockWorkStation();

    [DllImport("powrprof.dll", SetLastError = true)]
    private static extern bool SetSuspendState(bool hibernate, bool forceCritical, bool disableWakeEvent);
}
