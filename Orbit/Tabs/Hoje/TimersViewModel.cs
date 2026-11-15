using System;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Orbit.Tabs.Hoje;

// Vários timers rápidos ao mesmo tempo ("forno 40", "chá 3", "5") e um cronômetro.
// A pílula mostra o que termina primeiro (+N se houver mais); cada timer avisa sozinho ao acabar.
public sealed partial class TimersViewModel : ObservableObject
{
    private readonly Action<string?, int> _finished;
    private readonly DispatcherTimer _watchTick = new() { Interval = TimeSpan.FromSeconds(1) };
    private DateTime? _watchStart;
    private TimeSpan _watchBank; // tempo acumulado antes da última pausa

    public ObservableCollection<QuickTimerViewModel> Running { get; } = new();

    [ObservableProperty] private string? _activityText;
    [ObservableProperty] private string _newTimer = "";
    [ObservableProperty] private string _watchDisplay = "00:00";
    [ObservableProperty] private bool _watchRunning;

    public TimersViewModel(Action<string?, int> finished)
    {
        _finished = finished;
        // ponytail: o cronômetro só aparece na aba (sem pílula); tick só enquanto corre.
        _watchTick.Tick += (_, _) => WatchDisplay = QuickTimerViewModel.Format(WatchElapsed(DateTime.Now));
    }

    // Só os que ainda correm: o que acabou de zerar sai da lista logo depois (sem piscar a pílula).
    public QuickTimerViewModel? Next(DateTime now) => Running.Where(t => t.IsRunning).OrderBy(t => t.Remaining(now)).FirstOrDefault();
    public bool Quiet(DateTime now) => Next(now)?.Quiet(now) ?? false;
    public double Fraction(DateTime now) => Next(now)?.Fraction(now) ?? 0;

    // "5" → (null, 5); "forno 40" → ("Forno", 40); "chá 3 min" → ("Chá", 3). Sem número no fim = inválido.
    private static readonly Regex Spec = new(@"^(.*?)\s*(\d{1,3})\s*(?:min|minutos?|m)?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static (string? Name, int Minutes) Parse(string? spec)
    {
        var m = Spec.Match((spec ?? "").Trim());
        if (!m.Success) return (null, 0);
        var minutes = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
        var name = m.Groups[1].Value.Trim();
        return (name.Length == 0 ? null : char.ToUpper(name[0], CultureInfo.CurrentCulture) + name[1..], minutes);
    }

    [RelayCommand]
    private void Start(string? spec)
    {
        var (name, minutes) = Parse(spec);
        if (minutes <= 0) return;
        QuickTimerViewModel? timer = null;
        timer = new QuickTimerViewModel(m =>
        {
            Running.Remove(timer!);
            Refresh();
            _finished(name, m);
        }, name);
        timer.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(QuickTimerViewModel.ActivityText)) Refresh(); };
        Running.Add(timer);
        timer.StartCommand.Execute(minutes.ToString(CultureInfo.InvariantCulture));
        NewTimer = "";
    }

    [RelayCommand]
    private void Cancel(QuickTimerViewModel timer)
    {
        Running.Remove(timer);
        timer.CancelCommand.Execute(null);
        Refresh();
    }

    private void Refresh()
    {
        var next = Next(DateTime.Now);
        var others = Running.Count(t => t.IsRunning) - 1;
        ActivityText = next?.ActivityText is { } text ? (others > 0 ? $"{text}  +{others}" : text) : null;
    }

    // --- Cronômetro (conta pelo relógio, igual ao timer: suspender não atrasa) ---

    public TimeSpan WatchElapsed(DateTime now) => _watchBank + (_watchStart is { } s ? now - s : TimeSpan.Zero);

    [RelayCommand]
    private void ToggleWatch()
    {
        var now = DateTime.Now;
        if (_watchStart is { } s) { _watchBank += now - s; _watchStart = null; _watchTick.Stop(); }
        else { _watchStart = now; _watchTick.Start(); }
        WatchRunning = _watchStart is not null;
        WatchDisplay = QuickTimerViewModel.Format(WatchElapsed(now));
    }

    [RelayCommand]
    private void ResetWatch()
    {
        _watchTick.Stop();
        _watchStart = null;
        _watchBank = TimeSpan.Zero;
        WatchRunning = false;
        WatchDisplay = "00:00";
    }
}
