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

    public EnergiaViewModel()
    {
        _disarm.Tick += (_, _) => SetPending(null);
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
        if (!active) SetPending(null);
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
