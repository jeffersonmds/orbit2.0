using System;
using System.Linq;
using System.Threading.Tasks;
using Serilog;
using Windows.Devices.Radios;

namespace Orbit.Services;

// Wi‑Fi/Bluetooth via Windows.Devices.Radios (API pública). Se o Windows negar acesso
// a um app desktop, devolve null e a aba abre a página do Configurações no lugar.
public static class Radios
{
    // null = rádio inexistente ou acesso negado.
    public static async Task<bool?> IsOnAsync(RadioKind kind)
    {
        var radio = await FindAsync(kind);
        return radio is null ? null : radio.State == RadioState.On;
    }

    // Novo estado, ou null se não deu (sem rádio/sem permissão).
    public static async Task<bool?> ToggleAsync(RadioKind kind)
    {
        var radio = await FindAsync(kind);
        if (radio is null) return null;
        var target = radio.State == RadioState.On ? RadioState.Off : RadioState.On;
        try
        {
            var result = await radio.SetStateAsync(target);
            if (result != RadioAccessStatus.Allowed)
            {
                Log.Warning("Rádio {Kind}: {Result}", kind, result);
                return null;
            }
            return target == RadioState.On;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Falha ao alternar rádio {Kind}", kind);
            return null;
        }
    }

    private static async Task<Radio?> FindAsync(RadioKind kind)
    {
        try
        {
            if (await Radio.RequestAccessAsync() != RadioAccessStatus.Allowed) return null;
            return (await Radio.GetRadiosAsync()).FirstOrDefault(r => r.Kind == kind);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Rádios indisponíveis");
            return null;
        }
    }
}
