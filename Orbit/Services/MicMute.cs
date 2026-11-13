using System;
using System.Collections.Generic;
using System.Linq;
using NAudio.CoreAudioApi;
using Serilog;

namespace Orbit.Services;

// Mudo global do microfone: marca mudo em TODOS os microfones ativos (endpoint, não a sessão do app),
// então vale pra Teams, Discord, navegador… de uma vez. Ctrl+Alt+M e o cartão da aba Controles.
public static class MicMute
{
    // null = nenhum microfone ativo. Mudo só se todos estiverem mudos.
    public static bool? IsMuted()
    {
        try
        {
            using var en = new MMDeviceEnumerator();
            var states = new List<bool>();
            foreach (var d in en.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
                using (d) states.Add(d.AudioEndpointVolume.Mute);
            return states.Count == 0 ? null : states.All(m => m);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Mudo do microfone: não li o estado");
            return null;
        }
    }

    // Devolve o novo estado (true = mudo), ou null sem microfone.
    public static bool? Toggle()
    {
        try
        {
            using var en = new MMDeviceEnumerator();
            var devices = en.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active).ToList();
            try
            {
                if (devices.Count == 0) return null;
                bool mute = ShouldMute(devices.Select(d => d.AudioEndpointVolume.Mute));
                foreach (var d in devices) d.AudioEndpointVolume.Mute = mute;
                return mute;
            }
            finally
            {
                foreach (var d in devices) d.Dispose();
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Mudo do microfone: não troquei");
            return null;
        }
    }

    // Algum aberto → muta todos (um microfone esquecido ligado é o que não pode acontecer); todos mudos → libera.
    public static bool ShouldMute(IEnumerable<bool> muted) => muted.Any(m => !m);
}
