using System;
using System.Collections.ObjectModel;
using System.Windows.Media;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Orbit.Services;

namespace Orbit.Tabs.Foco;

public enum PomodoroPhase { Focus, ShortBreak, LongBreak }

// Portado do PomodoroTileViewModel do 1.0. Não é aba: roda em segundo plano (inclusive com
// a ilha recolhida); a aba Foco o exibe e a pílula mostra o tempo enquanto roda.
// Parado, não há timer nenhum.
public sealed partial class PomodoroViewModel : ObservableObject
{
    // Mantido em sincronia com a Ellipse do anel em FocoView.xaml.
    public const double RingDiameter = 168;
    public const double RingStroke = 8;

    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly Action<PomodoroPhase, int> _phaseChanged;

    // Mutáveis: Ajustes atualiza via UpdateConfig sem recriar.
    private int _focusMinutes, _shortBreakMinutes, _longBreakMinutes, _cyclesUntilLongBreak;

    private PomodoroPhase _phase = PomodoroPhase.Focus;
    private int _completedCycles;
    private TimeSpan _remaining;
    private TimeSpan _phaseTotal;
    // Rodando: conta pelo relógio (UI travada ou PC suspenso não atrasam o fim), não por ticks.
    private DateTime _endsAt;

    [ObservableProperty] private string _display = "25:00";
    [ObservableProperty] private string _mode = "Foco";
    [ObservableProperty] private string _playGlyph = "\uE768";
    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private DoubleCollection _ringDashArray = new() { 0, 0 };
    [ObservableProperty] private string? _activityText;
    [ObservableProperty] private string _cycleText = "";
    [ObservableProperty] private string _todayText = "";

    // Focos concluídos hoje; o dono (IslandViewModel) grava nos settings a cada um que fecha.
    public int FocusToday { get; private set; }
    public DateTime FocusDay { get; private set; }

    // Uma bolinha por ciclo até a pausa longa; preenchida = ciclo de foco concluído.
    public ObservableCollection<bool> CycleDots { get; } = new();

    private int EffectiveCycles => Math.Max(1, _cyclesUntilLongBreak);

    // phaseChanged(novaFase, minutos): a ilha transforma em alerta.
    public PomodoroViewModel(int focusMinutes, int shortBreakMinutes, int longBreakMinutes,
        int cyclesUntilLongBreak, Action<PomodoroPhase, int> phaseChanged, int focusToday = 0, DateTime focusDay = default)
    {
        FocusToday = focusToday;
        FocusDay = focusDay.Date;
        _focusMinutes = focusMinutes;
        _shortBreakMinutes = shortBreakMinutes;
        _longBreakMinutes = longBreakMinutes;
        _cyclesUntilLongBreak = cyclesUntilLongBreak;
        _phaseChanged = phaseChanged;
        _timer.Tick += (_, _) => Tick();
        EnterPhase(PomodoroPhase.Focus);
    }

    // Novas durações valem a partir da próxima fase; nº de ciclos vale já.
    public void UpdateConfig(int focusMinutes, int shortBreakMinutes, int longBreakMinutes, int cyclesUntilLongBreak)
    {
        _focusMinutes = focusMinutes;
        _shortBreakMinutes = shortBreakMinutes;
        _longBreakMinutes = longBreakMinutes;
        _cyclesUntilLongBreak = cyclesUntilLongBreak;
        RebuildCycleDots();
        // Parado no começo de uma fase: já mostra a duração nova.
        if (!IsRunning && _remaining == _phaseTotal) EnterPhase(_phase);
    }

    [RelayCommand]
    private void Toggle()
    {
        if (_timer.IsEnabled) { _remaining = Left(); _timer.Stop(); }
        else { _endsAt = DateTime.UtcNow + _remaining; _timer.Start(); }
        IsRunning = _timer.IsEnabled;
        PlayGlyph = IsRunning ? "\uE769" : "\uE768";
        UpdateActivity();
    }

    [RelayCommand]
    private void Reset()
    {
        _timer.Stop();
        IsRunning = false;
        PlayGlyph = "\uE768";
        _completedCycles = 0;
        EnterPhase(PomodoroPhase.Focus);
    }

    private void Tick()
    {
        _remaining = Left();
        if (_remaining <= TimeSpan.Zero)
        {
            var (next, cycles) = NextPhase(_phase, _completedCycles, EffectiveCycles);
            _completedCycles = cycles;
            if (_phase == PomodoroPhase.Focus)
            {
                FocusToday = CountToday(FocusToday, FocusDay, DateTime.Today) + 1;
                FocusDay = DateTime.Today;
            }
            EnterPhase(next);
            _endsAt = DateTime.UtcNow + _remaining; // ponytail: voltou de horas suspenso = avança uma fase só
            _phaseChanged(next, PhaseMinutes(next));
        }
        UpdateDisplay();
    }

    private TimeSpan Left() => _endsAt - DateTime.UtcNow;

    private void EnterPhase(PomodoroPhase phase)
    {
        _phase = phase;
        Mode = PhaseLabel(phase);
        _phaseTotal = TimeSpan.FromMinutes(PhaseMinutes(phase));
        _remaining = _phaseTotal;
        RebuildCycleDots();
        UpdateDisplay();
    }

    // Mínimo 1: um settings.json editado à mão com 0 trocaria de fase (e alertaria) a cada segundo.
    private int PhaseMinutes(PomodoroPhase phase) => Math.Max(1, phase switch
    {
        PomodoroPhase.Focus => _focusMinutes,
        PomodoroPhase.ShortBreak => _shortBreakMinutes,
        _ => _longBreakMinutes,
    });

    private void RebuildCycleDots()
    {
        CycleDots.Clear();
        for (int i = 0; i < EffectiveCycles; i++) CycleDots.Add(i < _completedCycles);
        CycleText = CycleLabel(_phase, _completedCycles, EffectiveCycles);
        RefreshToday();
    }

    // A aba chama ao abrir: virou o dia sem fechar foco nenhum → "Nenhum foco hoje".
    public void RefreshToday() => TodayText = TodayLabel(CountToday(FocusToday, FocusDay, DateTime.Today));

    private void UpdateDisplay()
    {
        Display = Format(_remaining);
        double progress = _phaseTotal.TotalSeconds > 0 ? _remaining.TotalSeconds / _phaseTotal.TotalSeconds : 0;
        RingDashArray = Ring.DashArray(progress, RingDiameter, RingStroke);
        UpdateActivity();
    }

    public TimeSpan Elapsed => _phaseTotal - _remaining;
    public double ElapsedFraction => _phaseTotal.TotalSeconds > 0 ? Elapsed.TotalSeconds / _phaseTotal.TotalSeconds : 0;

    private void UpdateActivity() => ActivityText = BuildActivity(IsRunning, Mode, Display);

    // --- Lógica pura testável ---

    // Arredonda pra cima: 24:59,4 mostra 25:00 e só chega a 00:00 no fim de verdade.
    public static string Format(TimeSpan left)
    {
        var s = TimeSpan.FromSeconds(Math.Max(0, Math.Ceiling(left.TotalSeconds)));
        return $"{(int)s.TotalMinutes:00}:{s.Seconds:00}";
    }

    public static string? BuildActivity(bool running, string mode, string display) =>
        running ? $"{mode} · {display}" : null;

    public static string PhaseLabel(PomodoroPhase phase) => phase switch
    {
        PomodoroPhase.ShortBreak => "Pausa",
        PomodoroPhase.LongBreak => "Pausa longa",
        _ => "Foco",
    };

    // Contagem salva de outro dia não vale hoje.
    public static int CountToday(int count, DateTime countedOn, DateTime today) => countedOn.Date == today.Date ? count : 0;

    public static string TodayLabel(int n) => n switch
    {
        0 => "Nenhum foco hoje",
        1 => "1 foco hoje",
        _ => $"{n} focos hoje",
    };

    // Na pausa longa o ciclo já fechou (contador zerado): mostra o último, não "1 de 4".
    public static string CycleLabel(PomodoroPhase phase, int completedCycles, int cyclesUntilLongBreak)
    {
        int total = Math.Max(1, cyclesUntilLongBreak);
        int current = phase switch
        {
            PomodoroPhase.Focus => completedCycles + 1,
            PomodoroPhase.ShortBreak => completedCycles,
            _ => total,
        };
        return $"Ciclo {Math.Clamp(current, 1, total)} de {total}";
    }

    public static (PomodoroPhase Phase, int CompletedCycles) NextPhase(
        PomodoroPhase phase, int completedCycles, int cyclesUntilLongBreak)
    {
        int total = Math.Max(1, cyclesUntilLongBreak);
        if (phase != PomodoroPhase.Focus) return (PomodoroPhase.Focus, completedCycles);
        int done = completedCycles + 1;
        return done >= total ? (PomodoroPhase.LongBreak, 0) : (PomodoroPhase.ShortBreak, done);
    }
}
