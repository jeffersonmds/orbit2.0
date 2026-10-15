using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Serilog;

namespace Orbit.Services;

// Uso de GPU como o Gerenciador de Tarefas: soma da "Utilization Percentage" dos motores
// 3D (contadores "GPU Engine"). Uma leitura da categoria por amostra; a 1ª leitura após
// abrir a aba é 0 (sem amostra anterior). Só é lido com a aba Stats aberta.
public sealed class GpuStats
{
    private Dictionary<string, CounterSample> _prev = new();
    private bool _warned;

    public double Read()
    {
        try
        {
            var data = new PerformanceCounterCategory("GPU Engine").ReadCategory()["Utilization Percentage"];
            var now = new Dictionary<string, CounterSample>();
            var samples = new List<float>();
            foreach (InstanceData d in data.Values)
            {
                // ReadCategory devolve nomes em minúsculas ("engtype_3d"): comparar sem caixa.
                if (!d.InstanceName.Contains("engtype_3d", StringComparison.OrdinalIgnoreCase)) continue;
                now[d.InstanceName] = d.Sample;
                if (_prev.TryGetValue(d.InstanceName, out var p)) samples.Add(CounterSample.Calculate(p, d.Sample));
            }
            _prev = now;
            return Aggregate(samples);
        }
        catch (Exception ex)
        {
            if (!_warned) Log.Warning(ex, "Contadores de GPU indisponíveis");
            _warned = true;
            return 0;
        }
    }

    // Aba fechada: a próxima leitura recomeça (sem média do tempo parado).
    public void Reset() => _prev = new();

    public static double Aggregate(IEnumerable<float> engine3DSamples) =>
        Math.Clamp(engine3DSamples.Sum(), 0, 100);
}
