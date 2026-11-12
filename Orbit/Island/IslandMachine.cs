using System;
using System.Collections.Generic;
using System.Linq;

namespace Orbit.Island;

// Discreta: a atividade da vez é "quieta" (mídia, Pomodoro já em curso) — pílula fininha, pra não cobrir as abas do navegador.
public enum IslandMode { Repouso, Atividade, Discreta, Alerta, Expandida, Oculta }

// Valor maior = prioridade maior na pílula.
// Maior valor vence na pílula. TelaRemota (tela, controle e microfone pelo aparelho) é indicador de
// privacidade: nunca fica atrás de nada.
// CameraMic: algum app do PC usando câmera/microfone (indicador de privacidade, logo abaixo do aparelho).
// Envio: copiando arquivos pra caixa de saída do aparelho (clicar na pílula cancela).
// Timer: timer rápido da aba Hoje (acima do Pomodoro: é o que acaba antes).
// Agenda: contagem pra próxima reunião (abaixo do Claude aguardando, acima do Pomodoro/timer).
public enum ActivityKind { Midia = 0, Pomodoro = 1, Timer = 2, Agenda = 3, Claude = 4, Envio = 5, Atualizacao = 6, CameraMic = 7, TelaRemota = 8 }

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
    public DateTime CreatedAt { get; init; } = DateTime.Now;

    // Na fila (tela cheia, outro alerta na frente) o aviso envelhece: sinal do sistema (Key) depois de
    // 1 min já não diz nada; o resto vale 10 min. A lista de recentes da bandeja guarda os perdidos.
    public bool IsStale(DateTime now) => now - CreatedAt > (Key is null ? TimeSpan.FromMinutes(10) : TimeSpan.FromMinutes(1));

    // Eventos do sistema (volume, carregador…): Key igual SUBSTITUI o alerta da vez em vez de enfileirar
    // (girar o volume não pode virar 30 alertas); Duration curta; Level desenha uma barra 0..1.
    public string? Key { get; init; }
    public System.TimeSpan? Duration { get; init; }
    public double? Level { get; init; }
    public bool HasLevel => Level is not null;
    // Botões no próprio alerta (ex.: print → "Aparelho" / "Abrir"); clicar executa e fecha o alerta.
    public System.Collections.Generic.IReadOnlyList<PeekAction> Actions { get; init; } = System.Array.Empty<PeekAction>();
    public bool HasActions => Actions.Count > 0;
    // Uma linha só (ex.: título da música): pílula baixinha em vez do alerta de duas linhas.
    public bool Compact { get; init; }
    // Miniatura à esquerda do título (ex.: capa da música; ImageSource, object pra máquina ficar sem WPF).
    public object? Image { get; init; }
    public string Bullet => Image is null ? "● " : "";
}

public sealed record PeekAction(string Label, System.Action Run);

// Lógica pura dos estados da ilha (sem WPF) — testada no harness.
public sealed class IslandMachine
{
    private readonly Dictionary<ActivityKind, string> _activities = new();
    private readonly HashSet<ActivityKind> _quiet = new();
    private readonly List<Peek> _pending = new();
    public const int MaxPending = 20;
    public Func<DateTime> Clock { get; init; } = () => DateTime.Now;

    public bool Expanded { get; private set; }
    public bool Fullscreen { get; private set; }
    public Peek? CurrentPeek { get; private set; }

    public IslandMode Mode =>
        Fullscreen ? IslandMode.Oculta
        : Expanded ? IslandMode.Expandida
        : CurrentPeek is not null ? IslandMode.Alerta
        : CurrentActivity is { } a && _quiet.Contains(a) ? IslandMode.Discreta
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

    // text null/vazio remove a atividade daquele tipo. quiet: na pílula vira a versão fininha (Discreta).
    public void SetActivity(ActivityKind kind, string? text, bool quiet = false)
    {
        if (string.IsNullOrEmpty(text)) _activities.Remove(kind);
        else _activities[kind] = text;
        if (quiet) _quiet.Add(kind); else _quiet.Remove(kind);
    }

    public void RaisePeek(Peek peek)
    {
        if (peek.Key is { } key)
        {
            _pending.RemoveAll(p => p.Key == key); // só o mais novo de cada tipo interessa
            if (CurrentPeek?.Key == key) { CurrentPeek = peek; return; }
        }
        _pending.Add(peek);
        if (_pending.Count > MaxPending) _pending.RemoveAt(0); // fila longa (tela cheia demorada): perde o mais velho
        if (!Fullscreen) ShowNextPeek();
    }

    // Alerta descartável (ex.: troca de música): nunca entra na fila — só aparece com a pílula livre,
    // e só substitui um do mesmo Key se ninguém estiver esperando. Na fila ficaria velho ou seguraria os outros.
    public void RaiseAmbientPeek(Peek peek)
    {
        if (Fullscreen) return;
        if (CurrentPeek is null || (CurrentPeek.Key == peek.Key && _pending.Count == 0)) CurrentPeek = peek;
    }

    public void PeekDone()
    {
        CurrentPeek = null;
        if (!Fullscreen) ShowNextPeek();
    }

    private void ShowNextPeek()
    {
        if (CurrentPeek is not null) return;
        var now = Clock();
        _pending.RemoveAll(p => p.IsStale(now));
        if (_pending.Count == 0) return;
        CurrentPeek = _pending[0];
        _pending.RemoveAt(0);
    }
}
