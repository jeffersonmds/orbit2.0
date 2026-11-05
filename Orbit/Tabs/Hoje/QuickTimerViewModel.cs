using System;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Orbit.Tabs.Hoje;

// Timer rápido (café, ligação), separado do Pomodoro. Conta pelo relógio (hora de terminar), não por
// ticks: suspender o PC ou a UI engasgar não atrasa. Roda com a ilha recolhida e alimenta a pílula.
public sealed partial class QuickTimerViewModel : ObservableObject
{
    private readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly Action<int> _finished;
    private DateTime _end;
    private TimeSpan _total;
    private int _minutes;

    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private string _display = "";
    [ObservableProperty] private string? _activityText;

    public QuickTimerViewModel(Action<int> finished)
    {
        _finished = finished;
        _tick.Tick += (_, _) => Update(DateTime.Now);
    }

    public TimeSpan Remaining(DateTime now) => IsRunning && _end > now ? _end - now : TimeSpan.Zero;
    public TimeSpan Elapsed(DateTime now) => IsRunning ? _total - Remaining(now) : TimeSpan.Zero;
    public double Fraction(DateTime now) => _total.TotalSeconds > 0 ? Elapsed(now).TotalSeconds / _total.TotalSeconds : 0;

    // Pílula fininha depois do 1º minuto, cheia de novo nos últimos 30 s (a contagem final importa).
    public bool Quiet(DateTime now) => Elapsed(now) >= TimeSpan.FromMinutes(1) && Remaining(now) > TimeSpan.FromSeconds(30);

    public static string Format(TimeSpan left)
    {
        var s = (int)Math.Ceiling(left.TotalSeconds);
        return s >= 3600 ? $"{s / 3600}:{s % 3600 / 60:00}:{s % 60:00}" : $"{s / 60:00}:{s % 60:00}";
    }

    [RelayCommand]
    private void Start(string minutes)
    {
        if (!int.TryParse(minutes, out var m) || m <= 0) return;
        _minutes = m;
        _total = TimeSpan.FromMinutes(m);
        _end = DateTime.Now + _total;
        IsRunning = true;
        _tick.Start();
        Update(DateTime.Now);
    }

    [RelayCommand]
    private void Cancel()
    {
        _tick.Stop();
        IsRunning = false;
        Display = "";
        ActivityText = null;
    }

    private void Update(DateTime now)
    {
        var left = Remaining(now);
        if (left <= TimeSpan.Zero)
        {
            Cancel();
            _finished(_minutes);
            return;
        }
        Display = Format(left);
        ActivityText = "Timer · " + Display;
    }
}
