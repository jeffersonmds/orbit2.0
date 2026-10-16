using System;
using System.Collections.Generic;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Orbit.Services;
using Serilog;

namespace Orbit.Tabs.Energia;

public sealed partial class PowerButton : ObservableObject
{
    public PowerButton(PowerAction action, string glyph, string normal)
    {
        Action = action;
        Glyph = glyph;
        Normal = normal;
        _label = normal;
    }

    public PowerAction Action { get; }
    public string Glyph { get; }
    public string Normal { get; }

    [ObservableProperty] private string _label;
    [ObservableProperty] private bool _isPending;
}

public sealed partial class EnergiaViewModel : TabViewModel
{
    public override string Id => "energia";
    public override string Title => "Energia";

    // Glifos da Segoe Fluent Icons.
    public IReadOnlyList<PowerButton> Botoes { get; } = new[]
    {
        new PowerButton(PowerAction.Bloquear, "\uE72E", "Bloquear"),
        new PowerButton(PowerAction.Suspender, "\uE708", "Suspender"),
        new PowerButton(PowerAction.Reiniciar, "\uE72C", "Reiniciar"),
        new PowerButton(PowerAction.Desligar, "\uE7E8", "Desligar"),
    };

    private PowerAction? _pending;
    private DateTime _armedAt;
    private readonly DispatcherTimer _disarm = new() { Interval = TimeSpan.FromSeconds(3) };
    private readonly DispatcherTimer _refresh = new() { Interval = TimeSpan.FromSeconds(30) }; // só com a aba aberta
    private DateTime? _shutdownAt; // ponytail: só em memória; reiniciar o Orbit esquece (o Windows não)

    [ObservableProperty] private string _batteryText = "—";
    [ObservableProperty] private string _batteryDetail = "";
    [ObservableProperty] private string _scheduleText = "Nada agendado";
    [ObservableProperty] private bool _isScheduled;

    public EnergiaViewModel()
    {
        _disarm.Tick += (_, _) => SetPending(null);
        _refresh.Tick += (_, _) => RefreshInfo();
    }

    private void RefreshInfo()
    {
        var (percent, charging, secondsLeft) = Battery.ReadFull();
        (BatteryText, BatteryDetail) = Battery.Describe(percent, charging, secondsLeft);
        if (_shutdownAt is { } at && at > DateTime.Now)
        {
            IsScheduled = true;
            ScheduleText = $"Desliga às {at:HH:mm} (em {Battery.FormatDuration(at - DateTime.Now)})";
        }
        else
        {
            _shutdownAt = null;
            IsScheduled = false;
            ScheduleText = "Nada agendado";
        }
    }

    [RelayCommand]
    private void Schedule(string minutes)
    {
        if (!int.TryParse(minutes, out var m) || m <= 0) return;
        try
        {
            Power.Schedule(TimeSpan.FromMinutes(m));
            _shutdownAt = DateTime.Now.AddMinutes(m);
            Log.Information("Energia: desligamento agendado em {Minutos} min", m);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Falha ao agendar desligamento");
        }
        RefreshInfo();
    }

    [RelayCommand]
    private void CancelSchedule()
    {
        try { Power.CancelScheduled(); }
        catch (Exception ex) { Log.Error(ex, "Falha ao cancelar desligamento"); }
        _shutdownAt = null;
        RefreshInfo();
    }

    [RelayCommand]
    private async System.Threading.Tasks.Task ScreenOff()
    {
        await System.Threading.Tasks.Task.Delay(600); // senão o próprio clique (soltar o mouse) já acende a tela
        Power.ScreenOff();
    }

    // Ação destrutiva precisa de dois cliques no mesmo botão (sem MessageBox sobre a ilha).
    public static (bool Execute, PowerAction? Pending) Decide(PowerAction clicked, PowerAction? pending)
    {
        if (!Power.NeedsConfirmation(clicked)) return (true, null);
        return pending == clicked ? (true, null) : (false, clicked);
    }

    [RelayCommand]
    private void Click(PowerButton button)
    {
        var (execute, pending) = Decide(button.Action, _pending);
        // Duplo clique rápido não vale como confirmação.
        if (execute && Power.NeedsConfirmation(button.Action) && DateTime.UtcNow - _armedAt < TimeSpan.FromMilliseconds(400)) return;
        SetPending(pending);
        if (!execute) return;
        try
        {
            Log.Information("Energia: {Acao}", button.Action);
            Power.Execute(button.Action);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Falha ao executar {Acao}", button.Action);
        }
    }

    public override void SetActive(bool active)
    {
        if (!active)
        {
            SetPending(null);
            _refresh.Stop();
            return;
        }
        RefreshInfo();
        _refresh.Start();
    }

    private void SetPending(PowerAction? pending)
    {
        _pending = pending;
        if (pending is not null) _armedAt = DateTime.UtcNow;
        foreach (var b in Botoes)
        {
            b.IsPending = b.Action == pending;
            b.Label = b.IsPending ? "Confirmar?" : b.Normal;
        }
        _disarm.Stop();
        if (pending is not null) _disarm.Start();
    }
}
