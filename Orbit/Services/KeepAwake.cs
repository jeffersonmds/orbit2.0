using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace Orbit.Services;

// Impede suspensão automática enquanto algo está em uso (ex.: stream de câmera). Contado:
// várias chamadas On precisam do mesmo número de Off. Não impede fechar a tampa (política de energia).
public static class KeepAwake
{
    [Flags]
    private enum Es : uint { Continuous = 0x80000000, SystemRequired = 0x00000001 }

    private static int _count;

    public static void On()
    {
        if (Interlocked.Increment(ref _count) == 1)
            SetThreadExecutionState(Es.Continuous | Es.SystemRequired);
    }

    public static void Off()
    {
        if (Interlocked.Decrement(ref _count) <= 0)
        {
            Interlocked.Exchange(ref _count, 0);
            SetThreadExecutionState(Es.Continuous);
        }
    }

    [DllImport("kernel32.dll")]
    private static extern uint SetThreadExecutionState(Es flags);
}
