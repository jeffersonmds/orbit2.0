using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Orbit.Services;

namespace Orbit.Tabs.Claude;

// Portado do ClaudeCodeTileViewModel do 1.0. Recebe eventos de hook já na UI thread
// (a ilha faz o BeginInvoke). Em vez de toast fixo, expõe Attention (vira alerta) e
// ActivityText (vira atividade na pílula).
public sealed partial class ClaudeViewModel : TabViewModel
{
    private readonly Dictionary<string, ClaudeSessionInfo> _sessions = new();

    public override string Id => "claude";
    public override string Title => "Claude";

    public ObservableCollection<ClaudeSessionInfo> Sessions { get; } = new();

    [ObservableProperty] private bool _hooksActive;
    [ObservableProperty] private string? _activityText;

    // Stop/Notification de uma sessão conhecida.
    public event Action<ClaudeSessionInfo>? Attention;

    public void HandleHookEvent(ClaudeHookEvent evt)
    {
        ClaudeSessionReducer.Apply(_sessions, evt);
        Publish();
        if (ClaudeSessionReducer.ShouldNotify(evt.HookEventName) && _sessions.TryGetValue(evt.SessionId, out var info))
            Attention?.Invoke(info);
    }

    // Terminal fechado / Claude caiu não manda SessionEnd: o ✕ tira a sessão (e a pílula).
    [RelayCommand]
    private void Dismiss(ClaudeSessionInfo session)
    {
        _sessions.Remove(session.SessionId);
        Publish();
    }

    private void Publish()
    {
        Sessions.Clear();
        foreach (var s in _sessions.Values) Sessions.Add(s);
        ActivityText = BuildActivity(_sessions.Values);
    }

    // Só checa o ~/.claude/settings.json com a aba aberta.
    public override void SetActive(bool active)
    {
        if (active) HooksActive = Environment.ProcessPath is { } exe && ClaudeHookInstaller.IsInstalled(exe);
    }

    [RelayCommand]
    private void InstallHooks()
    {
        if (Environment.ProcessPath is not { } exe) return;
        ClaudeHookInstaller.Install(exe);
        HooksActive = ClaudeHookInstaller.IsInstalled(exe);
    }

    // Pílula: só quando alguma sessão espera por você. Sessão rodando não ocupa a pílula
    // (senão esconderia mídia/Pomodoro o tempo todo).
    public static string? BuildActivity(IEnumerable<ClaudeSessionInfo> sessions)
    {
        var waiting = sessions.Where(s => s.State != ClaudeSessionState.Running).ToList();
        return waiting.Count switch
        {
            0 => null,
            1 => $"Claude · {waiting[0].ProjectName} — {waiting[0].StatusText}",
            _ => $"Claude · {waiting.Count} sessões aguardando você",
        };
    }
}
