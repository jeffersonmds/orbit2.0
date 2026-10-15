using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Windows.Media;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
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

    public StatsViewModel() => _timer.Tick += (_, _) => Sample();

    public override void SetActive(bool active)
    {
        if (!active)
        {
            _timer.Stop();
            _net.Reset();
            _gpu.Reset();
            return;
        }
        Sample();
        _timer.Start();
    }

    private void Sample()
    {
        var s = _sys.Read();
        CpuText = $"{s.CpuPercent:0}%";
        CpuRing = Ring.DashArray(s.CpuPercent / 100, RingDiameter, RingStroke);
        RamText = $"{s.Ram.Percent:0}%";
        RamRing = Ring.DashArray(s.Ram.Percent / 100, RingDiameter, RingStroke);
        RamDetail = string.Format(Pt, "{0:0.0} / {1:0.0} GB", s.Ram.UsedGb, s.Ram.TotalGb);
        DiskText = string.Format(Pt, "{0:0} / {1:0} GB", s.Disk.UsedGb, s.Disk.TotalGb);

        double gpu = _gpu.Read();
        GpuText = $"{gpu:0}%";
        GpuRing = Ring.DashArray(gpu / 100, RingDiameter, RingStroke);

        var (down, up) = _net.Read();
        NetText = $"↓ {NetStats.Format(down)}   ↑ {NetStats.Format(up)}";
        UptimeText = FormatUptime(TimeSpan.FromMilliseconds(Environment.TickCount64));

        var top = TopByMemory(ReadProcesses(), 5);
        double max = top.Count > 0 ? top[0].Bytes : 1;
        TopApps.Clear();
        foreach (var (name, bytes) in top) TopApps.Add(new TopApp(name, FormatBytes(bytes), bytes / max));
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
