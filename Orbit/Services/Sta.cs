using System;
using System.Threading;
using System.Threading.Tasks;

namespace Orbit.Services;

// COM do shell (Shell.Application, IShellItemImageFactory) quer thread STA. Roda numa thread
// STA dedicada, fora da UI thread — senão listar ~300 apps/ícones travaria a ilha.
public static class Sta
{
    public static Task<T> Run<T>(Func<T> work)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { tcs.SetResult(work()); }
            catch (Exception ex) { tcs.SetException(ex); }
        })
        {
            IsBackground = true,
            Name = "Orbit STA",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return tcs.Task;
    }
}
