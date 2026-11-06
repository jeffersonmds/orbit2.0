using System;
using System.Collections.ObjectModel;
using System.Windows.Media;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Orbit.Services;

namespace Orbit.Tabs.Hoje;

public enum PomodoroPhase { Focus, ShortBreak, LongBreak }

// Portado do PomodoroTileViewModel do 1.0. Não é aba: roda em segundo plano (inclusive com
// a ilha recolhida); a aba Hoje o exibe e a pílula mostra o tempo enquanto roda.
// Parado, não há timer nenhum.
public sealed partial class PomodoroViewModel : ObservableObject
{
    // Mantido em sincronia com a Ellipse do anel em HojeView.xaml.
    public const double RingDiameter = 112;
    public const double RingStroke = 6;

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

    // Uma bolinha por ciclo até a pausa longa; preenchida = ciclo de foco concluído.
    public ObservableCollection<bool> CycleDots { get; } = new();

    private int EffectiveCycles => Math.Max(1, _cyclesUntilLongBreak);

    // phaseChanged(novaFase, minutos): a ilha transforma em alerta.
    public PomodoroViewModel(int focusMinutes, int shortBreakMinutes, int longBreakMinutes,
        int cyclesUntilLongBreak, Action<PomodoroPhase, int> phaseChanged)
    {
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
    }

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

    public static (PomodoroPhase Phase, int CompletedCycles) NextPhase(
        PomodoroPhase phase, int completedCycles, int cyclesUntilLongBreak)
    {
        int total = Math.Max(1, cyclesUntilLongBreak);
        if (phase != PomodoroPhase.Focus) return (PomodoroPhase.Focus, completedCycles);
        int done = completedCycles + 1;
        return done >= total ? (PomodoroPhase.LongBreak, 0) : (PomodoroPhase.ShortBreak, done);
    }
}
