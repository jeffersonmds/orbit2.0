using System;
using System.Runtime.InteropServices;

namespace Orbit.Services;

// Impede suspensão automática enquanto algo está em uso (ex.: stream de câmera). Contado:
// várias chamadas On precisam do mesmo número de Off. Não impede fechar a tampa (política de energia).
public static class KeepAwake
{
    [Flags]
    private enum Es : uint { Continuous = 0x80000000, SystemRequired = 0x00000001 }

    private static int _count;
    private static readonly object Gate = new();

    public static void On()
    {
        lock (Gate) if (++_count == 1) Apply(Es.Continuous | Es.SystemRequired);
    }

    public static void Off()
    {
        lock (Gate)
        {
            if (_count == 0) return;
            if (--_count == 0) Apply(Es.Continuous);
        }
    }

    // SetThreadExecutionState vale pra thread que chama: On/Off vêm de continuations em threads
    // quaisquer, então os dois sempre rodam no Dispatcher (fila em ordem, dentro do lock).
    private static void Apply(Es flags)
    {
        var d = System.Windows.Application.Current?.Dispatcher;
        if (d is null) SetThreadExecutionState(flags);
        else d.BeginInvoke(() => SetThreadExecutionState(flags));
    }

    [DllImport("kernel32.dll")]
    private static extern uint SetThreadExecutionState(Es flags);
}
