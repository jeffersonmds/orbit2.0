using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
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
    // Cada consulta abre um `claude`, e o endpoint por trás dele tem rate limit.
    private static readonly TimeSpan UsageMaxAge = TimeSpan.FromMinutes(5);

    private readonly Dictionary<string, ClaudeSessionInfo> _sessions = new();
    private readonly Func<Task<ClaudeUsage?>>? _fetchUsage;

    private ClaudeUsage? _usage;
    private DateTimeOffset _usageAt;
    private DateTime? _usageTriedAt;
    private bool _usageFetching;
    private int _fiveHourLevel, _sevenDayLevel;
    private DispatcherTimer? _usageTimer;

    // Sem fetchUsage (testes) a cota nunca é buscada.
    public ClaudeViewModel(Func<Task<ClaudeUsage?>>? fetchUsage = null) => _fetchUsage = fetchUsage;

    public override string Id => "claude";
    public override string Title => "Claude";

    public ObservableCollection<ClaudeSessionInfo> Sessions { get; } = new();
    public ObservableCollection<ClaudeUsageRow> UsageRows { get; } = new();

    [ObservableProperty] private bool _hooksActive;
    [ObservableProperty] private string? _usageAge;
    [ObservableProperty] private string? _activityText;

    // Stop/Notification de uma sessão conhecida.
    public event Action<ClaudeSessionInfo>? Attention;

    // Título e detalhe de um aviso de limite do plano.
    public event Action<string, string>? LimitReached;

    // pid do processo dono da janela em primeiro plano (a ilha informa a cada troca de foco).
    public int ForegroundPid { get; set; }

    public void HandleHookEvent(ClaudeHookEvent evt)
    {
        ClaudeSessionReducer.Apply(_sessions, evt);
        // já está olhando o terminal dessa sessão: nem aviso nem pílula (seria ruído)
        bool looking = evt.HostPid is not null && evt.HostPid == ForegroundPid;
        if (looking) ClaudeSessionReducer.MarkSeen(_sessions, ForegroundPid);
        Publish();
        if (!looking && ClaudeSessionReducer.ShouldNotify(evt.HookEventName) && _sessions.TryGetValue(evt.SessionId, out var info))
            Attention?.Invoke(info);
        // Fim de resposta é quando cota e contexto mudam: atualiza por evento, sem timer de fundo.
        if (evt.HookEventName == "Stop")
        {
            _ = FetchUsage();
            if (evt.TranscriptPath is { Length: > 0 } path) _ = RefreshContext(evt.SessionId, path);
        }
    }

    private async Task RefreshContext(string sessionId, string transcriptPath)
    {
        var tokens = await Task.Run(() => ClaudeUsageClient.ReadContextTokens(transcriptPath));
        if (tokens is null || !_sessions.TryGetValue(sessionId, out var session)) return;
        _sessions[sessionId] = session with { ContextTokens = tokens };
        Publish();
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

    private async Task FetchUsage()
    {
        var tried = DateTime.Now;
        if (_fetchUsage is null || _usageFetching || !Freshness.IsStale(_usageTriedAt, UsageMaxAge, tried)) return;
        _usageFetching = true;
        _usageTriedAt = tried; // conta a tentativa, não o sucesso: falha não vira um claude por resposta
        var usage = await _fetchUsage();
        _usageFetching = false;
        if (usage is not null) ApplyUsage(usage);
        else RefreshUsage();
    }

    private void ApplyUsage(ClaudeUsage usage)
    {
        _usage = usage;
        _usageAt = DateTimeOffset.Now;
        CheckLimit("5 horas", usage.FiveHour, ClaudeUsageMath.FiveHours, ref _fiveHourLevel);
        CheckLimit("semanal", usage.SevenDay, ClaudeUsageMath.SevenDays, ref _sevenDayLevel);
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
        if (_usage?.Opus is { } opus) UsageRows.Add(ClaudeUsageMath.Row("Opus", opus, ClaudeUsageMath.SevenDays, now));
        if (_usage?.Sonnet is { } sonnet) UsageRows.Add(ClaudeUsageMath.Row("Sonnet", sonnet, ClaudeUsageMath.SevenDays, now));
        UsageAge = UsageRows.Count == 0 ? (_usageFetching ? "buscando…" : "cota indisponível")
            : now - _usageAt < TimeSpan.FromMinutes(1) ? "atualizado agora"
            : $"atualizado há {ClaudeUsageMath.Span(now - _usageAt)}";
    }

    // Só checa o ~/.claude/settings.json e atualiza as contagens com a aba aberta.
    public override void SetActive(bool active)
    {
        if (active)
        {
            HooksActive = Environment.ProcessPath is { } exe && ClaudeHookInstaller.IsInstalled(exe);
            _ = FetchUsage();
            RefreshUsage();
            _usageTimer ??= new DispatcherTimer(TimeSpan.FromSeconds(30), DispatcherPriority.Background, (_, _) => RefreshUsage(), Dispatcher.CurrentDispatcher);
            _usageTimer.Start();
        }
        else _usageTimer?.Stop();
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
