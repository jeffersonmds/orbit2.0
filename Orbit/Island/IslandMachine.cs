using System.Collections.Generic;
using System.Linq;

namespace Orbit.Island;

public enum IslandMode { Repouso, Atividade, Alerta, Expandida, Oculta }

// Valor maior = prioridade maior na pílula.
public enum ActivityKind { Midia = 0, Pomodoro = 1, Claude = 2 }

// Evento pontual que a pílula mostra por alguns segundos. TabId = aba aberta ao clicar.
public sealed record Peek(string Title, string? Detail, string? TabId);

// Lógica pura dos estados da ilha (sem WPF) — testada no harness.
public sealed class IslandMachine
{
    private readonly Dictionary<ActivityKind, string> _activities = new();
    private readonly List<Peek> _pending = new();

    public bool Expanded { get; private set; }
    public bool Fullscreen { get; private set; }
    public Peek? CurrentPeek { get; private set; }

    public IslandMode Mode =>
        Fullscreen ? IslandMode.Oculta
        : Expanded ? IslandMode.Expandida
        : CurrentPeek is not null ? IslandMode.Alerta
        : _activities.Count > 0 ? IslandMode.Atividade
        : IslandMode.Repouso;

    public string? ActivityText =>
        _activities.Count == 0 ? null : _activities.MaxBy(kv => kv.Key).Value;

    public void Expand()
    {
        if (!Fullscreen) Expanded = true;
    }

    public void Collapse() => Expanded = false;

    public void SetFullscreen(bool on)
    {
        Fullscreen = on;
        if (on)
        {
            Expanded = false;
            // Alerta interrompido volta pra frente da fila e reaparece ao sair da tela cheia.
            if (CurrentPeek is not null)
            {
                _pending.Insert(0, CurrentPeek);
                CurrentPeek = null;
            }
        }
        else
        {
            ShowNextPeek();
        }
    }

    // text null/vazio remove a atividade daquele tipo.
    public void SetActivity(ActivityKind kind, string? text)
    {
        if (string.IsNullOrEmpty(text)) _activities.Remove(kind);
        else _activities[kind] = text;
    }

    public void RaisePeek(Peek peek)
    {
        _pending.Add(peek);
        if (!Fullscreen) ShowNextPeek();
    }

    public void PeekDone()
    {
        CurrentPeek = null;
        if (!Fullscreen) ShowNextPeek();
    }

    private void ShowNextPeek()
    {
        if (CurrentPeek is not null || _pending.Count == 0) return;
        CurrentPeek = _pending[0];
        _pending.RemoveAt(0);
    }
}
