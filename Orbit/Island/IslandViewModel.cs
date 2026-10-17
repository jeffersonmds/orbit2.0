using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Media;
using System.Threading.Tasks;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using Orbit.Bridge;
using Orbit.Models;
using Orbit.Services;
using Orbit.Tabs;
using Orbit.Tabs.Ajustes;
using Orbit.Tabs.Apps;
using Orbit.Tabs.Claude;
using Orbit.Tabs.Clips;
using Orbit.Tabs.Controles;
using Orbit.Tabs.Energia;
using Orbit.Tabs.Hoje;
using Orbit.Tabs.IPhone;
using Orbit.Tabs.Mercado;
using Orbit.Tabs.Noticias;
using Orbit.Tabs.Notas;
using Orbit.Tabs.Stats;
using Serilog;

namespace Orbit.Island;

// Orquestra a máquina de estados, as abas e os serviços de fundo que alimentam a pílula.
// A janela observa Mode/Peek/ActivityText.
public sealed partial class IslandViewModel : ObservableObject
{
    // Ordem fixa das abas no painel. Ids ainda não registrados são pulados.
    public static readonly string[] TabOrder =
        { "controles", "hoje", "stats", "clipboard", "notas", "apps", "mercado", "noticias", "claude", "iphone", "energia", "ajustes" };

    private readonly IslandMachine _machine = new();
    private readonly OrbitSettings _settings;

    public ClipsViewModel Clips { get; }
    public IPhoneViewModel IPhone { get; }
    public NotasViewModel Notas { get; } = new(NotasViewModel.FilePath);
    private readonly ApiService _api = new();
    private readonly TokenStore _tokens = new(TokenStore.FilePath);
    private readonly WebPush _push = new(WebPush.KeyPath, WebPush.SubsPath);
    private readonly ClaudeHookServer _claudeServer = new();
    private readonly List<TabViewModel> _allTabs = new();

    public ObservableCollection<TabViewModel> Tabs { get; } = new();
    public PomodoroViewModel Pomodoro { get; }
    public NowPlayingViewModel NowPlaying { get; } = new();

    [ObservableProperty] private TabViewModel? _selectedTab;
    [ObservableProperty] private IslandMode _mode = IslandMode.Repouso;
    [ObservableProperty] private string _activityText = "";
    [ObservableProperty] private Peek? _peek;

    // Monitor da ilha (Ajustes). A janela escuta MonitorChanged e se reposiciona.
    public string? MonitorDevice => _settings.IslandMonitor;
    public event Action? MonitorChanged;

    public IslandViewModel(OrbitSettings settings)
    {
        _settings = settings;
        // Serviços de fundo (rodam com a ilha recolhida e alimentam a pílula).
        Pomodoro = new PomodoroViewModel(settings.PomodoroFocusMinutes, settings.PomodoroShortBreakMinutes,
            settings.PomodoroLongBreakMinutes, settings.PomodoroCyclesUntilLongBreak, OnPomodoroPhase);
        Pomodoro.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(PomodoroViewModel.ActivityText))
                SetActivity(ActivityKind.Pomodoro, Pomodoro.ActivityText);
        };
        NowPlaying.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(NowPlayingViewModel.ActivityText))
                SetActivity(ActivityKind.Midia, NowPlaying.ActivityText);
        };
        NowPlaying.Start();
        // Sem DI: abas e serviços de fundo são criados aqui na mão.
        Register(new EnergiaViewModel());
        Register(new AjustesViewModel(settings, Pomodoro, () => _allTabs, ApplyTabVisibility, () => MonitorChanged?.Invoke()));
        Register(new HojeViewModel(_api, Pomodoro, TodoStore.FilePath));
        Register(new ControlesViewModel(NowPlaying));
        Register(new StatsViewModel());
        Register(new MercadoViewModel(_api, settings));
        Register(new NoticiasViewModel(_api, settings));
        var claude = new ClaudeViewModel();
        Register(claude);
        claude.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ClaudeViewModel.ActivityText))
                SetActivity(ActivityKind.Claude, claude.ActivityText);
        };
        claude.Attention += info =>
            Notify(new Peek($"Claude · {info.ProjectName}", info.StatusText, "claude"), _settings.ClaudeSoundEnabled);
        // O servidor roda em background: volta pra UI thread antes de mexer na aba.
        // ponytail: sem Dispose — o pipe morre com o processo.
        _claudeServer.HookReceived += evt => Application.Current?.Dispatcher.BeginInvoke(() => claude.HandleHookEvent(evt));
        _claudeServer.Start();
        // Clipboard: captura sempre por evento; aba escondida em Ajustes = não grava nada.
        Clips = new ClipsViewModel(ClipboardStore.FilePath, ClipboardStore.ImagesDir,
            () => !settings.HiddenTabs.Contains("clipboard"));
        Register(Clips);
        Register(new AppsViewModel(settings, AppIcons.CacheDir, Collapse));
        // Ponte com o iPhone: desligada até o usuário ligar na aba (BridgeEnabled).
        IPhone = new IPhoneViewModel(settings, _tokens, new AuditLog(AuditLog.FilePath),
            BuildBridgeHandlers, Path.Combine(AppContext.BaseDirectory, "wwwroot"));
        Register(IPhone);
        _ = IPhone.StartIfEnabledAsync();
        Register(Notas);
        // novas abas/serviços entram acima desta linha
        // Guarda antes: ApplyTabVisibility seleciona a 1ª aba e isso sobrescreveria LastTab.
        var last = settings.LastTab;
        ApplyTabVisibility();
        SelectedTab = Tabs.FirstOrDefault(t => t.Id == last) ?? Tabs.FirstOrDefault();
    }

    // O que a ponte faz de verdade. Roda em threads do Kestrel: o que toca UI/clipboard/SMTC
    // volta pra UI thread via Dispatcher.
    private BridgeHandlers BuildBridgeHandlers()
    {
        var ui = Application.Current.Dispatcher;
        var stats = new SystemStats();
        stats.Read(ram: false, disk: false); // 1ª leitura de CPU é sempre 0 (sem delta)
        var statsGate = new object();
        return new BridgeHandlers
        {
            OpenUrl = url =>
            {
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
                return Task.CompletedTask;
            },
            GetClipboard = () => ui.InvokeAsync(() =>
                System.Windows.Clipboard.ContainsText() ? System.Windows.Clipboard.GetText() : "").Task,
            SetClipboard = text => ui.InvokeAsync(() =>
            {
                if (!ClipboardReader.SetText(text)) throw new InvalidOperationException("Clipboard ocupado");
            }).Task,
            GetNotes = () => ui.InvokeAsync(() => Notas.Text).Task,
            SetNotes = text => ui.InvokeAsync(() => Notas.Text = text).Task,
            Power = action =>
            {
                // Responde antes: suspender só retorna quando o PC acorda; desligar mataria a resposta.
                _ = Task.Run(async () =>
                {
                    await Task.Delay(500);
                    try { Orbit.Services.Power.Execute(action); }
                    catch (Exception ex) { Log.Warning(ex, "Ponte: falha em {Acao}", action); }
                });
                return Task.CompletedTask;
            },
            Media = command => ui.InvokeAsync(() => command switch
            {
                MediaCommand.PlayPause => NowPlaying.PlayPauseCommand.ExecuteAsync(null),
                MediaCommand.Next => NowPlaying.NextCommand.ExecuteAsync(null),
                _ => NowPlaying.PreviousCommand.ExecuteAsync(null),
            }).Task.Unwrap(),
            // Capa que o SMTC já entrega pra aba Controles (128 px), reencodada em PNG.
            GetCover = () => ui.InvokeAsync(() => NowPlaying.ThumbnailBytes).Task,
            GetMedia = () => ui.InvokeAsync(() =>
            {
                using var volume = new Volume();
                return new MediaState(
                    NowPlaying.HasSession ? NowPlaying.Title : null,
                    NowPlaying.HasSession ? NowPlaying.Subtitle : null,
                    NowPlaying.ActivityText is not null,
                    volume.Available ? volume.Percent : 0);
            }).Task,
            SetVolume = percent =>
            {
                using var volume = new Volume();
                volume.SetPercent(percent);
                return Task.CompletedTask;
            },
            GetStats = () =>
            {
                lock (statsGate)
                {
                    var s = stats.Read();
                    var (battery, charging) = Battery.Read();
                    return new StatsSnapshot(Math.Round(s.CpuPercent), Math.Round(s.Ram.UsedGb, 1), Math.Round(s.Ram.TotalGb, 1),
                        Math.Round(s.Disk.UsedGb), Math.Round(s.Disk.TotalGb), battery, charging);
                }
            },
            UploadFolder = () => BridgeRules.UploadFolder(_settings.BridgeUploadFolder),
            CameraEnabled = () => _settings.BridgeCameraEnabled,
            PcFilesEnabled = () => _settings.BridgePcFilesEnabled,
            PcRoots = () => PcFiles.DefaultRoots(BridgeRules.UploadFolder(_settings.BridgeUploadFolder)),
            Push = _push,
            ValidTokens = ValidTokenNames,
            Notify = e => ui.BeginInvoke(() => Notify(new Peek("iPhone · " + e.Title, e.Detail, "iphone", e.OpenPath), false)),
        };
    }

    private void OnPomodoroPhase(PomodoroPhase phase, int minutes) =>
        Notify(new Peek(PomodoroViewModel.PhaseLabel(phase), $"{minutes} min", "hoje"), _settings.PomodoroSoundEnabled);

    private void Register(TabViewModel tab) => _allTabs.Add(tab);

    // Ids visíveis na ordem de TabOrder. "ajustes" nunca some (senão não dá pra reverter).
    public static List<string> VisibleIds(ICollection<string> registered, ICollection<string> hidden) =>
        TabOrder.Where(id => registered.Contains(id) && (id == "ajustes" || !hidden.Contains(id))).ToList();

    // Sincroniza Tabs com as preferências sem recriar a coleção (a aba aberta continua aberta).
    public void ApplyTabVisibility()
    {
        var ids = VisibleIds(_allTabs.Select(t => t.Id).ToList(), _settings.HiddenTabs);
        for (int i = Tabs.Count - 1; i >= 0; i--)
            if (!ids.Contains(Tabs[i].Id)) Tabs.RemoveAt(i);
        for (int i = 0; i < ids.Count; i++)
            if (i >= Tabs.Count || Tabs[i].Id != ids[i])
                Tabs.Insert(i, _allTabs.First(t => t.Id == ids[i]));
        if (SelectedTab is null || !Tabs.Contains(SelectedTab)) SelectedTab = Tabs.FirstOrDefault();
    }

    partial void OnSelectedTabChanged(TabViewModel? oldValue, TabViewModel? newValue)
    {
        if (Mode == IslandMode.Expandida)
        {
            SafeSetActive(oldValue, false);
            SafeSetActive(newValue, true);
        }
        if (newValue is not null && newValue.Id != _settings.LastTab)
        {
            _settings.LastTab = newValue.Id;
            SettingsStore.Save(_settings);
        }
    }

    public void Expand() { _machine.Expand(); Refresh(); }
    public void Collapse() { _machine.Collapse(); Refresh(); }
    public void SetFullscreen(bool on) { _machine.SetFullscreen(on); Refresh(); }
    public void SetActivity(ActivityKind kind, string? text) { _machine.SetActivity(kind, text); Refresh(); }
    public void RaisePeek(Peek peek) { _machine.RaisePeek(peek); Refresh(); }
    public void PeekExpired() { _machine.PeekDone(); Refresh(); }

    // Ponto único de alerta dos serviços/abas. Em tela cheia com a preferência ligada vira
    // toast do Windows; senão é peek na pílula (com bip curto, já que o peek é silencioso).
    public void Notify(Peek peek, bool sound)
    {
        // Também no iPhone (Web Push), com a ponte ligada. Eventos causados pelo próprio iPhone
        // (link aberto, arquivo recebido) não voltam pra ele.
        if (_settings.BridgeEnabled && peek.TabId != "iphone")
            _ = _push.SendAsync(peek.Title, peek.Detail ?? "", ValidTokenNames());
        if (ShouldToast(_machine.Fullscreen, _settings.AlertsAsToastInFullscreen))
        {
            ToastNotifier.Notify(peek.Title, peek.Detail ?? "", sound);
            return;
        }
        if (sound && !_machine.Fullscreen) SystemSounds.Asterisk.Play();
        RaisePeek(peek);
    }

    public static bool ShouldToast(bool fullscreen, bool preferToast) => fullscreen && preferToast;

    private IReadOnlyCollection<string> ValidTokenNames() => _tokens.List().Select(t => t.Name).ToHashSet();

    public void OpenTab(string id)
    {
        var tab = Tabs.FirstOrDefault(t => t.Id == id);
        if (tab is not null) SelectedTab = tab;
        Expand();
    }

    // Clique no alerta: consome o alerta e abre a aba relacionada (ou só expande).
    public void OpenPeekTab()
    {
        var tabId = _machine.CurrentPeek?.TabId;
        var openPath = _machine.CurrentPeek?.OpenPath;
        _machine.PeekDone();
        if (openPath is not null)
        {
            try { Process.Start(new ProcessStartInfo(openPath) { UseShellExecute = true }); }
            catch (Exception ex) { Log.Warning(ex, "Não abri {Caminho}", openPath); }
            Refresh();
            return;
        }
        if (tabId is null) Expand();
        else OpenTab(tabId);
    }

    private void Refresh()
    {
        var before = Mode;
        ActivityText = _machine.ActivityText ?? "";
        Peek = _machine.CurrentPeek;
        Mode = _machine.Mode; // por último: a janela reage a Mode e já lê Peek/ActivityText novos
        if (before != IslandMode.Expandida && Mode == IslandMode.Expandida) SafeSetActive(SelectedTab, true);
        if (before == IslandMode.Expandida && Mode != IslandMode.Expandida) SafeSetActive(SelectedTab, false);
    }

    // Uma aba quebrada não pode derrubar a ilha.
    public static void SafeSetActive(TabViewModel? tab, bool active)
    {
        if (tab is null) return;
        try
        {
            tab.SetActive(active);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Aba {Tab} falhou em SetActive({Active})", tab.Id, active);
        }
    }
}
