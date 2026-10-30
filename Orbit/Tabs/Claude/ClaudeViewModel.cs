using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Threading;
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
    public ObservableCollection<ClaudeUsageRow> UsageRows { get; } = new();

    private ClaudeUsage? _usage;
    private DateTimeOffset _usageAt;
    private int _fiveHourLevel, _sevenDayLevel;
    private DispatcherTimer? _usageTimer;

    [ObservableProperty] private bool _hooksActive;
    [ObservableProperty] private bool _statusLineActive;
    [ObservableProperty] private string? _usageAge;
    [ObservableProperty] private string? _activityText;

    // Stop/Notification de uma sessão conhecida.
    public event Action<ClaudeSessionInfo>? Attention;

    // pid do processo dono da janela em primeiro plano (a ilha informa a cada troca de foco).
    public int ForegroundPid { get; set; }

    // Título e detalhe de um aviso de limite do plano.
    public event Action<string, string>? LimitReached;

    public void HandleHookEvent(ClaudeHookEvent evt)
    {
        if (evt.Usage is { } usage)
        {
            var before = _sessions.GetValueOrDefault(evt.SessionId);
            ClaudeSessionReducer.Apply(_sessions, evt);
            if (before != _sessions.GetValueOrDefault(evt.SessionId)) Publish();
            ApplyUsage(usage);
            return;
        }

        ClaudeSessionReducer.Apply(_sessions, evt);
        // já está olhando o terminal dessa sessão: nem aviso nem pílula (seria ruído)
        bool looking = evt.HostPid is not null && evt.HostPid == ForegroundPid;
        if (looking) ClaudeSessionReducer.MarkSeen(_sessions, ForegroundPid);
        Publish();
        if (!looking && ClaudeSessionReducer.ShouldNotify(evt.HookEventName) && _sessions.TryGetValue(evt.SessionId, out var info))
            Attention?.Invoke(info);
    }

    // Você trouxe uma janela pro primeiro plano: se é a de uma sessão aguardando, ela sai da pílula.
    public void OnForeground(int pid)
    {
        ForegroundPid = pid;
        if (ClaudeSessionReducer.MarkSeen(_sessions, pid)) Publish();
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

    private void ApplyUsage(ClaudeUsage usage)
    {
        _usage = usage.Merge(_usage);
        _usageAt = DateTimeOffset.Now;
        CheckLimit("5 horas", _usage.FiveHour, ClaudeUsageMath.FiveHours, ref _fiveHourLevel);
        CheckLimit("semanal", _usage.SevenDay, ClaudeUsageMath.SevenDays, ref _sevenDayLevel);
        RefreshUsage();
    }

    private void CheckLimit(string label, ClaudeWindow? window, TimeSpan length, ref int level)
    {
        if (window is null) return;
        var now = DateTimeOffset.Now;
        (level, bool notify) = ClaudeUsageMath.Crossing(level, ClaudeUsageMath.UsedNow(window, now));
        if (notify) LimitReached?.Invoke($"Claude · limite {label} em {window.UsedPct:0}%", ClaudeUsageMath.Describe(window, length, now));
    }

    private void RefreshUsage()
    {
        var now = DateTimeOffset.Now;
        UsageRows.Clear();
        if (_usage?.FiveHour is { } five) UsageRows.Add(ClaudeUsageMath.Row("5 horas", five, ClaudeUsageMath.FiveHours, now));
        if (_usage?.SevenDay is { } week) UsageRows.Add(ClaudeUsageMath.Row("Semanal", week, ClaudeUsageMath.SevenDays, now));
        UsageAge = UsageRows.Count == 0 ? "aguardando a próxima resposta do Claude Code"
            : now - _usageAt < TimeSpan.FromMinutes(1) ? "atualizado agora"
            : $"atualizado há {ClaudeUsageMath.Span(now - _usageAt)}";
    }

    // Só checa o ~/.claude/settings.json e atualiza as contagens com a aba aberta.
    public override void SetActive(bool active)
    {
        if (active)
        {
            HooksActive = Environment.ProcessPath is { } exe && ClaudeHookInstaller.IsInstalled(exe);
            StatusLineActive = Environment.ProcessPath is { } path && ClaudeHookInstaller.IsStatusLineInstalled(path);
            RefreshUsage();
            _usageTimer ??= new DispatcherTimer(TimeSpan.FromSeconds(30), DispatcherPriority.Background, (_, _) => RefreshUsage(), Dispatcher.CurrentDispatcher);
            _usageTimer.Start();
        }
        else _usageTimer?.Stop();
    }

    [RelayCommand]
    private void InstallStatusLine()
    {
        if (Environment.ProcessPath is not { } exe) return;
        ClaudeHookInstaller.InstallStatusLine(exe);
        StatusLineActive = ClaudeHookInstaller.IsStatusLineInstalled(exe);
    }

    [RelayCommand]
    private void RemoveStatusLine()
    {
        if (Environment.ProcessPath is not { } exe) return;
        ClaudeHookInstaller.RemoveStatusLine();
        StatusLineActive = ClaudeHookInstaller.IsStatusLineInstalled(exe);
    }

    [RelayCommand]
    private void InstallHooks()
    {
        if (Environment.ProcessPath is not { } exe) return;
        ClaudeHookInstaller.Install(exe);
        HooksActive = ClaudeHookInstaller.IsInstalled(exe);
    }

    // Pílula: só quando alguma sessão espera por você e você ainda não olhou. Sessão rodando não
    // ocupa a pílula (senão esconderia mídia/Pomodoro o tempo todo).
    public static string? BuildActivity(IEnumerable<ClaudeSessionInfo> sessions)
    {
        var waiting = sessions.Where(s => s.State != ClaudeSessionState.Running && !s.Seen).ToList();
        return waiting.Count switch
        {
            0 => null,
            1 => $"Claude · {waiting[0].ProjectName} — {waiting[0].StatusText}",
            _ => $"Claude · {waiting.Count} sessões aguardando você",
        };
    }
}
