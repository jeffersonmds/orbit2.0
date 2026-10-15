using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Serilog;

namespace Orbit.Services;

// Uso de GPU como o Gerenciador de Tarefas: soma da "Utilization Percentage" dos motores
// 3D (contadores "GPU Engine"). Só é lido com a aba Stats aberta.
public sealed class GpuStats : IDisposable
{
    private readonly Dictionary<string, PerformanceCounter> _counters = new();
    private bool _warned;

    // ponytail: relista as instâncias a cada leitura (processos entram/saem) — custa alguns ms
    // a cada 2 s com a aba aberta. Cachear a lista por alguns ciclos se pesar.
    public double Read()
    {
        try
        {
            var names = new PerformanceCounterCategory("GPU Engine").GetInstanceNames()
                .Where(n => n.Contains("engtype_3D", StringComparison.Ordinal))
                .ToHashSet();
            foreach (var gone in _counters.Keys.Where(k => !names.Contains(k)).ToList())
            {
                _counters[gone].Dispose();
                _counters.Remove(gone);
            }
            var samples = new List<float>();
            foreach (var name in names)
            {
                if (!_counters.TryGetValue(name, out var c))
                {
                    c = new PerformanceCounter("GPU Engine", "Utilization Percentage", name, readOnly: true);
                    _counters[name] = c;
                }
                samples.Add(c.NextValue()); // 1ª leitura de um contador novo é 0; corrige no tick seguinte
            }
            return Aggregate(samples);
        }
        catch (Exception ex)
        {
            if (!_warned) Log.Warning(ex, "Contadores de GPU indisponíveis");
            _warned = true;
            return 0;
        }
    }

    public static double Aggregate(IEnumerable<float> engine3DSamples) =>
        Math.Clamp(engine3DSamples.Sum(), 0, 100);

    public void Dispose()
    {
        foreach (var c in _counters.Values) c.Dispose();
        _counters.Clear();
    }
}
