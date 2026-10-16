using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Serilog;
using Orbit.Services;

namespace Orbit.Tabs.Stats;

// Fraction = proporção em relação ao maior (pra barrinha).
public sealed record TopApp(string Name, string Memory, double Fraction);

public sealed partial class StatsViewModel : TabViewModel
{
    // Mantidos em sincronia com as Ellipses dos anéis em StatsView.xaml.
    public const double RingDiameter = 84;
    public const double RingStroke = 7;

    private static readonly CultureInfo Pt = new("pt-BR");
    private readonly SystemStats _sys = new();
    private readonly GpuStats _gpu = new();
    private readonly NetStats _net = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(2) };
    private bool _busy;

    public override string Id => "stats";
    public override string Title => "Stats";

    [ObservableProperty] private string _cpuText = "--";
    [ObservableProperty] private string _ramText = "--";
    [ObservableProperty] private string _gpuText = "--";
    [ObservableProperty] private string _ramDetail = "";
    [ObservableProperty] private string _diskText = "";
    [ObservableProperty] private string _netText = "";
    [ObservableProperty] private string _uptimeText = "";
    [ObservableProperty] private DoubleCollection _cpuRing = Ring.DashArray(0, RingDiameter, RingStroke);
    [ObservableProperty] private DoubleCollection _ramRing = Ring.DashArray(0, RingDiameter, RingStroke);
    [ObservableProperty] private DoubleCollection _gpuRing = Ring.DashArray(0, RingDiameter, RingStroke);

    public ObservableCollection<TopApp> TopApps { get; } = new();

    public StatsViewModel() => _timer.Tick += async (_, _) => await SampleAsync();

    public override void SetActive(bool active)
    {
        if (!active)
        {
            _timer.Stop();
            _net.Reset();
            _gpu.Reset();
            return;
        }
        _ = SampleAsync();
        _timer.Start();
    }

    // As leituras (contadores de GPU, lista de processos, rede) rodam fora da UI thread:
    // a 1ª leitura de GPU a frio leva ~1 s e travava a ilha. _busy evita leituras sobrepostas.
    private async Task SampleAsync()
    {
        if (_busy) return;
        _busy = true;
        try
        {
            var r = await Task.Run(() => (
                Sys: _sys.Read(),
                Gpu: _gpu.Read(),
                Net: _net.Read(),
                Top: TopByMemory(ReadProcesses(), 5)));

            var s = r.Sys;
            CpuText = $"{s.CpuPercent:0}%";
            CpuRing = Ring.DashArray(s.CpuPercent / 100, RingDiameter, RingStroke);
            RamText = $"{s.Ram.Percent:0}%";
            RamRing = Ring.DashArray(s.Ram.Percent / 100, RingDiameter, RingStroke);
            RamDetail = string.Format(Pt, "{0:0.0} / {1:0.0} GB", s.Ram.UsedGb, s.Ram.TotalGb);
            DiskText = string.Format(Pt, "{0:0} / {1:0} GB", s.Disk.UsedGb, s.Disk.TotalGb);
            GpuText = $"{r.Gpu:0}%";
            GpuRing = Ring.DashArray(r.Gpu / 100, RingDiameter, RingStroke);
            NetText = $"↓ {NetStats.Format(r.Net.DownBps)}   ↑ {NetStats.Format(r.Net.UpBps)}";
            UptimeText = FormatUptime(TimeSpan.FromMilliseconds(Environment.TickCount64));

            double max = r.Top.Count > 0 ? r.Top[0].Bytes : 1;
            TopApps.Clear();
            foreach (var (name, bytes) in r.Top) TopApps.Add(new TopApp(name, FormatBytes(bytes), bytes / max));
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Stats: falha ao amostrar");
        }
        finally
        {
            _busy = false;
        }
    }

    private static List<(string Name, long Bytes)> ReadProcesses()
    {
        var list = new List<(string, long)>();
        foreach (var p in Process.GetProcesses())
        {
            using (p)
            {
                try { list.Add((p.ProcessName, p.WorkingSet64)); }
                catch { /* processo saiu no meio da leitura */ }
            }
        }
        return list;
    }

    // --- Lógica pura testável ---

    // Soma por nome (o Chrome tem dezenas de processos) e pega os n maiores.
    public static List<(string Name, long Bytes)> TopByMemory(IEnumerable<(string Name, long Bytes)> procs, int n) =>
        procs.GroupBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .Select(g => (Name: g.First().Name, Bytes: g.Sum(x => x.Bytes)))
            .OrderByDescending(x => x.Bytes)
            .Take(n)
            .ToList();

    public static string FormatBytes(long bytes) =>
        bytes >= 1L << 30
            ? string.Format(Pt, "{0:0.0} GB", bytes / (double)(1L << 30))
            : string.Format(Pt, "{0:0} MB", bytes / (double)(1L << 20));

    public static string FormatUptime(TimeSpan t) =>
        t.TotalDays >= 1 ? $"{(int)t.TotalDays}d {t.Hours}h" : $"{t.Hours}h {t.Minutes}m";
}
