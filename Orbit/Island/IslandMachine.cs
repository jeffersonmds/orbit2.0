using System.Collections.Generic;
using System.Linq;

namespace Orbit.Island;

public enum IslandMode { Repouso, Atividade, Alerta, Expandida, Oculta }

// Valor maior = prioridade maior na pílula.
// Maior valor vence na pílula. TelaRemota (tela, controle e microfone pelo iPhone) é indicador de
// privacidade: nunca fica atrás de nada.
// CameraMic: algum app do PC usando câmera/microfone (indicador de privacidade, logo abaixo do iPhone).
public enum ActivityKind { Midia = 0, Pomodoro = 1, Claude = 2, Atualizacao = 3, CameraMic = 4, TelaRemota = 5 }

// Evento pontual que a pílula mostra por alguns segundos. TabId = aba aberta ao clicar.
// Classe (não record): dois alertas iguais seguidos são eventos distintos — igualdade por referência.
public sealed class Peek
{
    public Peek(string title, string? detail, string? tabId, string? openPath = null)
    {
        Title = title;
        Detail = detail;
        TabId = tabId;
        OpenPath = openPath;
    }

    public string Title { get; }
    public string? Detail { get; }
    public string? TabId { get; }
    public string? OpenPath { get; } // se houver, o clique abre isto no Explorer em vez da aba

    // Eventos do sistema (volume, carregador…): Key igual SUBSTITUI o alerta da vez em vez de enfileirar
    // (girar o volume não pode virar 30 alertas); Duration curta; Level desenha uma barra 0..1.
    public string? Key { get; init; }
    public System.TimeSpan? Duration { get; init; }
    public double? Level { get; init; }
    public bool HasLevel => Level is not null;
    // Botões no próprio alerta (ex.: print → "iPhone" / "Abrir"); clicar executa e fecha o alerta.
    public System.Collections.Generic.IReadOnlyList<PeekAction> Actions { get; init; } = System.Array.Empty<PeekAction>();
    public bool HasActions => Actions.Count > 0;
}

public sealed record PeekAction(string Label, System.Action Run);

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

    // Qual atividade está na pílula agora (a de maior prioridade), ou null.
    public ActivityKind? CurrentActivity =>
        _activities.Count == 0 ? null : _activities.MaxBy(kv => kv.Key).Key;

    public void Expand()
    {
        if (!Fullscreen) Expanded = true;
    }

    public void Collapse() => Expanded = false;

    public const string DeviceChoiceKey = "escolher-dispositivo";

    public void SetFullscreen(bool on)
    {
        Fullscreen = on;
        if (on)
        {
            Expanded = false;
            // O seletor de dispositivo não sobrevive à tela cheia: reapareceria depois, sem contexto.
            _pending.RemoveAll(p => p.Key == DeviceChoiceKey);
            if (CurrentPeek?.Key == DeviceChoiceKey) CurrentPeek = null;
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
        if (peek.Key is { } key)
        {
            _pending.RemoveAll(p => p.Key == key); // só o mais novo de cada tipo interessa
            if (CurrentPeek?.Key == key) { CurrentPeek = peek; return; }
        }
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
