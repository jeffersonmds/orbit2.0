using System;
using System.Diagnostics;
using System.Linq;

namespace Orbit.Services;

// PC no limite: CPU (média de 30 s, que é o intervalo entre leituras) acima de 90% por 1 min, ou RAM acima de 90%.
// Avisa uma vez por cruzamento; só rearma quando cai bem abaixo (histerese), senão oscilar em 90% vira spam.
public sealed class PcLimit
{
    public const double CpuHigh = 90, CpuLow = 70, RamHigh = 90, RamLow = 80;
    public const int CpuSamples = 2; // 2 leituras de 30 s = 1 min

    private int _cpuHot;
    private bool _cpuArmed = true, _ramArmed = true;

    public (bool Cpu, bool Ram) Step(double cpu, double ram)
    {
        _cpuHot = cpu >= CpuHigh ? _cpuHot + 1 : 0;
        if (cpu < CpuLow) _cpuArmed = true;
        if (ram < RamLow) _ramArmed = true;
        var cpuAlert = _cpuArmed && _cpuHot >= CpuSamples;
        var ramAlert = _ramArmed && ram >= RamHigh;
        if (cpuAlert) _cpuArmed = false;
        if (ramAlert) _ramArmed = false;
        return (cpuAlert, ramAlert);
    }

    // Quem mais come RAM, só no momento do aviso (varrer processos a cada 30 s não vale).
    // ponytail: CPU sem culpado; achar exigiria duas leituras por processo.
    public static string? TopMemoryProcess()
    {
        try
        {
            var procs = Process.GetProcesses();
            var top = procs.GroupBy(p => p.ProcessName)
                .Select(g => (Name: g.Key, Bytes: g.Sum(p => { try { return p.WorkingSet64; } catch { return 0L; } })))
                .MaxBy(x => x.Bytes);
            foreach (var p in procs) p.Dispose();
            return top.Name is null ? null : $"{top.Name} · {top.Bytes / 1024.0 / 1024 / 1024:0.0} GB";
        }
        catch { return null; }
    }
}
