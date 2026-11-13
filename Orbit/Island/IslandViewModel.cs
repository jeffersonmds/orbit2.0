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
using Orbit.Tabs.Dispositivos;
using Orbit.Tabs.Mercado;
using Orbit.Tabs.Noticias;
using Orbit.Tabs.Notas;
using Orbit.Tabs.Stats;
using Serilog;

namespace Orbit.Island;

public enum DeviceChoice { None, Direct, Ask, Blocked }

// Orquestra a máquina de estados, as abas e os serviços de fundo que alimentam a pílula.
// A janela observa Mode/Peek/ActivityText.
public sealed partial class IslandViewModel : ObservableObject
{
    // Ordem fixa das abas no painel. Ids ainda não registrados são pulados.
    public static readonly string[] TabOrder =
        { "controles", "hoje", "stats", "clipboard", "notas", "apps", "mercado", "noticias", "claude", "dispositivos", "energia", "ajustes" };

    private readonly IslandMachine _machine = new();
    private readonly OrbitSettings _settings;

    public ClipsViewModel Clips { get; }
    public DispositivosViewModel Dispositivos { get; }
    public NotasViewModel Notas { get; } = new(NotasViewModel.FilePath);
    private readonly ApiService _api = new();
    private readonly TokenStore _tokens = new(TokenStore.FilePath);
    private readonly WebPush _push = new(WebPush.KeyPath, WebPush.SubsPath);
    private readonly Outbox _outbox = new(Outbox.DefaultDir);
    private readonly CameraMicWatcher _cameraMic = new();
    private readonly SystemSignals _signals = new();
    private readonly ScreenshotWatcher _screenshots = new();
    private readonly DownloadWatcher _downloads = new();
    private readonly RainAlert _rain = new();
    private readonly System.Windows.Threading.DispatcherTimer _rainTimer = new() { Interval = TimeSpan.FromMinutes(20) };
    private readonly System.Windows.Threading.DispatcherTimer _sweepTimer = new() { Interval = TimeSpan.FromMinutes(30) };
    private LocationResult? _rainLocation;
    private IReadOnlyList<CameraMicUse> _cameraMicUses = Array.Empty<CameraMicUse>();
    // só pra atualizar o "· 12 min" enquanto algo está em uso
    private readonly System.Windows.Threading.DispatcherTimer _cameraMicClock = new() { Interval = TimeSpan.FromSeconds(30) };
    private readonly System.Threading.CancellationTokenSource _sendToCts = new();
    private readonly ClaudeHookServer _claudeServer = new();
    private readonly List<TabViewModel> _allTabs = new();

    public ObservableCollection<TabViewModel> Tabs { get; } = new();
    public PomodoroViewModel Pomodoro { get; }
    public QuickTimerViewModel Timer { get; }
    public NowPlayingViewModel NowPlaying { get; } = new();

    [ObservableProperty] private TabViewModel? _selectedTab;
    [ObservableProperty] private IslandMode _mode = IslandMode.Repouso;
    [ObservableProperty] private string _activityText = "";
    [ObservableProperty] private Peek? _peek;
    // Barrinhas no ritmo do som: só com a pílula em Atividade e a música como atividade da vez.
    [ObservableProperty] private bool _showAudioBars;
    private string? _lastTrack;
    private string? _mediaText;
    private bool _mediaAudible = true;

    // O app diz que está tocando (a janela então mede se sai som de verdade).
    public bool MediaPlaying => _mediaText is not null;

    // Sem som saindo do PC: a pílula de mídia some, mesmo com o app dizendo "tocando".
    public void SetMediaAudible(bool audible)
    {
        if (_mediaAudible == audible) return;
        _mediaAudible = audible;
        UpdateMediaActivity();
    }

    private void UpdateMediaActivity() => SetActivity(ActivityKind.Midia, _mediaAudible ? _mediaText : null, quiet: true);
    // Câmera/microfone em uso: a pílula troca o texto pelo desenho próprio (bolinha, nome, ícones, tempo).
    [ObservableProperty] private bool _showCameraMic;
    [ObservableProperty] private bool _showPomodoroBar;
    [ObservableProperty] private bool _showUpdating;
    [ObservableProperty] private double _pomodoroProgress;
    private double TimerProgress { get; set; }
    [ObservableProperty] private CameraMicDisplay? _cameraMicView;

    // Formato notch (Ajustes): orelhas côncavas nos cantos de cima da pílula.
    public bool NotchShape => _settings.NotchShape;

    // Mudo global do microfone (Ctrl+Alt+M / Controles). Lido de novo a cada mudança de uso do microfone.
    [ObservableProperty] private bool _micMuted;

    // Faixa do jogo em tela cheia: amostra a cada 2 s, só enquanto ela aparece (contrato de leveza).
    [ObservableProperty] private string _gameStatsText = "";
    private readonly System.Windows.Threading.DispatcherTimer _gameTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly SystemStats _gameSys = new();
    private readonly GpuStats _gameGpu = new();
    private bool _gameBusy, _gameReset;

    // Monitor da ilha (Ajustes). A janela escuta MonitorChanged e se reposiciona.
    public string? MonitorDevice => _settings.IslandMonitor;
    public event Action? MonitorChanged;

    // Autoatualização: a janela fecha o app (sem perguntar) quando o script de troca já foi disparado.
    public event Action? ExitForUpdate;
    private bool _updating;
    private ClaudeViewModel _claude = null!;

    // A janela avisa toda troca de primeiro plano (pid do dono): sessões do Claude ali contam como vistas.
    public void OnForegroundProcess(int pid) => _claude.OnForeground(pid);

    public IslandViewModel(OrbitSettings settings)
    {
        // Tokens de antes das permissões por aparelho herdam as seis chaves globais.
        // Falha de escrita não derruba o app: sem semear, permissão nula nega tudo e o próximo início tenta de novo.
        try
        {
            _tokens.SeedPermissions(new DevicePermissions(settings.BridgeCameraEnabled, settings.BridgePcFilesEnabled,
                settings.BridgeScreenEnabled, settings.BridgeControlEnabled, settings.BridgeMicEnabled, settings.BridgeKeyboardEnabled));
        }
        catch (Exception ex) { Log.Warning(ex, "Tokens: não semeou as permissões"); }
        _push.MigrateTokenRefs(_tokens.List());
        _settings = settings;
        // Serviços de fundo (rodam com a ilha recolhida e alimentam a pílula).
        Pomodoro = new PomodoroViewModel(settings.PomodoroFocusMinutes, settings.PomodoroShortBreakMinutes,
            settings.PomodoroLongBreakMinutes, settings.PomodoroCyclesUntilLongBreak, OnPomodoroPhase);
        Pomodoro.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(PomodoroViewModel.ActivityText))
            {
                // Primeiro minuto da fase com a pílula cheia (dá pra ver o tempo); depois só a barrinha.
                SetActivity(ActivityKind.Pomodoro, Pomodoro.ActivityText, quiet: Pomodoro.Elapsed >= TimeSpan.FromMinutes(1));
                if (_machine.CurrentActivity != ActivityKind.Timer) PomodoroProgress = Pomodoro.ElapsedFraction;
            }
        };
        Timer = new QuickTimerViewModel(OnTimerFinished);
        Timer.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(QuickTimerViewModel.ActivityText)) return;
            var now = DateTime.Now;
            SetActivity(ActivityKind.Timer, Timer.ActivityText, quiet: Timer.Quiet(now));
            TimerProgress = Timer.Fraction(now);
        };
        NowPlaying.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(NowPlayingViewModel.ActivityText))
            {
                var text = NowPlaying.ActivityText;
                _mediaText = text;
                UpdateMediaActivity();
                // Pílula discreta não mostra o título: avisa por alguns segundos quando começa a tocar ou troca.
                if (text is not null && text != _lastTrack)
                {
                    _machine.RaiseAmbientPeek(new Peek(text.TrimStart('▶', ' '), null, "controles") { Key = "midia", Duration = TimeSpan.FromSeconds(4), Compact = true, Image = NowPlaying.Thumbnail });
                    Refresh();
                }
                _lastTrack = text;
            }
        };
        NowPlaying.Start();
        // Sem DI: abas e serviços de fundo são criados aqui na mão.
        Register(new EnergiaViewModel());
        Register(new AjustesViewModel(settings, Pomodoro, () => _allTabs, ApplyTabVisibility, () => MonitorChanged?.Invoke(),
            SelfUpdate.CheckAsync, UpdateNowAsync, () => OnPropertyChanged(nameof(NotchShape))));
        var agenda = _agenda = new AgendaViewModel(_api, settings);
        Register(new HojeViewModel(_api, Pomodoro, Timer, TodoStore.FilePath, agenda));
        // 5 min antes: alerta com "Entrar" se o evento tem link de reunião.
        agenda.Reminder += e => Notify(new Peek($"Em 5 min · {e.Title}", e.When, "hoje")
        {
            Duration = TimeSpan.FromSeconds(15),
            Actions = e.JoinUrl is { } join
                ? new[] { new PeekAction("Entrar", () => { try { Process.Start(new ProcessStartInfo(join) { UseShellExecute = true }); } catch { } }) }
                : Array.Empty<PeekAction>(),
        }, sound: true);
        agenda.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(AgendaViewModel.ActivityText)) SetActivity(ActivityKind.Agenda, agenda.ActivityText);
        };
        agenda.Start();
        Register(new ControlesViewModel(NowPlaying, ToggleMicMute));
        Register(new StatsViewModel());
        Register(new MercadoViewModel(_api, settings));
        Register(new NoticiasViewModel(_api, settings));
        var claude = _claude = new ClaudeViewModel(ClaudeUsageClient.FetchAsync);
        Register(claude);
        claude.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ClaudeViewModel.ActivityText))
                SetActivity(ActivityKind.Claude, claude.ActivityText);
        };
        claude.Attention += info =>
            Notify(new Peek($"Claude · {info.ProjectName}", info.StatusText, "claude"), _settings.ClaudeSoundEnabled);
        claude.LimitReached += (title, detail) => Notify(new Peek(title, detail, "claude"), _settings.ClaudeSoundEnabled);
        // O servidor roda em background: volta pra UI thread antes de mexer na aba.
        // ponytail: sem Dispose — o pipe morre com o processo.
        _claudeServer.HookReceived += evt => Application.Current?.Dispatcher.BeginInvoke(() => claude.HandleHookEvent(evt));
        _claudeServer.PermissionRequested = evt => Application.Current is { } app
            ? app.Dispatcher.InvokeAsync(() => claude.RequestPermission(evt)).Task.Unwrap()
            : Task.FromResult<PermissionAnswer?>(null);
        // Botões no alerta; se ele sumir antes, a aba Claude tem os mesmos no cartão da sessão.
        claude.PermissionAsked += (info, answer, canAlways) =>
        {
            var actions = new List<PeekAction> { new("Permitir", () => answer(PermissionAnswer.Allow)) };
            if (canAlways) actions.Add(new("Sempre", () => answer(PermissionAnswer.AlwaysAllow)));
            actions.Add(new("Negar", () => answer(PermissionAnswer.Deny)));
            Notify(new Peek($"Claude · {info.ProjectName}", info.StatusText, "claude")
                { Duration = TimeSpan.FromSeconds(30), Actions = actions }, _settings.ClaudeSoundEnabled);
        };
        _claudeServer.Start();
        // Clipboard: captura sempre por evento; aba escondida em Ajustes = não grava nada.
        Clips = new ClipsViewModel(ClipboardStore.FilePath, ClipboardStore.ImagesDir,
            () => !settings.HiddenTabs.Contains("clipboard"), () => DevicesOn);
        Register(Clips);
        Clips.SendToDeviceRequested += item =>
        {
            if (item.Kind == ClipKind.Image && item.ImagePath is { } img)
                ChooseDevice("imagem", d => SendFiles(new[] { img }, d, $"imagem-{item.CopiedAt:yyyyMMdd-HHmmss}.png"));
            else if (item.Text is { } text) ChooseDevice("texto", d => SendText(text, d));
        };
        // Explorer → Enviar para → "Dispositivo (Orbit)" chega por pipe (thread do pool).
        SendToDevice.Listen(files => Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            if (files.Length == 0) return;
            ChooseDevice(files.Length == 1 ? Path.GetFileName(files[0]) : $"{files.Length} arquivos", d => SendFiles(files, d));
        }), _sendToCts.Token);
        Register(new AppsViewModel(settings, AppIcons.CacheDir, Collapse));
        // Ponte com o aparelho: desligada até o usuário ligar na aba (BridgeEnabled).
        Dispositivos = new DispositivosViewModel(settings, _tokens, new AuditLog(AuditLog.FilePath),
            BuildBridgeHandlers, Path.Combine(AppContext.BaseDirectory, "wwwroot"), _outbox.RemoveDevice);
        Register(Dispositivos);
        StopBridgeIfDevicesOff(); // aba escondida em Ajustes = recurso desligado: nem sobe a ponte
        _ = Dispositivos.StartIfEnabledAsync();
        Register(Notas);
        _ = AnnounceUpdateAsync();
        // Quem está usando câmera/microfone: o watcher avisa (thread própria) e a pílula mostra.
        _cameraMicClock.Tick += (_, _) => ShowCameraMicActivity();
        _cameraMic.Changed += uses => Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            _cameraMicUses = uses;
            MicMuted = MicMute.IsMuted() == true; // mudado fora (Configurações do Windows) também aparece
            if (uses.Count > 0) _cameraMicClock.Start(); else _cameraMicClock.Stop();
            ShowCameraMicActivity();
        });
        _cameraMic.Start();
        MicMuted = MicMute.IsMuted() == true;
        _gameTimer.Tick += async (_, _) => await SampleGameAsync();
        // Eventos do sistema: alerta curto na pílula (não vai pro aparelho nem vira toast em tela cheia).
        _signals.Raised += sig => Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            if (SignalAllowed(_settings, sig.Kind)) RaisePeek(SignalPeek(sig));
        });
        _signals.Start();
        _screenshots.Captured += path => Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            if (_settings.ScreenshotOfferEnabled) RaisePeek(ScreenshotPeek(path, DevicesOn && _settings.BridgeEnabled ? Devices() : Array.Empty<TokenInfo>()));
        });
        _screenshots.Start();
        _downloads.Finished += path => Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            if (_settings.DownloadOfferEnabled) RaisePeek(DownloadPeek(path, DevicesOn && _settings.BridgeEnabled ? Devices() : Array.Empty<TokenInfo>()));
        });
        _downloads.Start();
        // "Vai chover": a cada 20 min (1ª checagem 1 min depois de abrir, pra não disputar o começo).
        _rainTimer.Tick += async (_, _) => { _rainTimer.Interval = TimeSpan.FromMinutes(20); await CheckRainAsync(); };
        _rainTimer.Interval = TimeSpan.FromMinutes(1);
        _rainTimer.Start();
        // Faxina: itens vencidos da caixa de saída e PNGs temporários de print/desenho pra aparelho.
        Sweep();
        _sweepTimer.Tick += (_, _) => Sweep();
        _sweepTimer.Start();
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
                !ClipboardReader.IsPrivateNow() && System.Windows.Clipboard.ContainsText() ? System.Windows.Clipboard.GetText() : "").Task,
            // Decodifica fora do Dispatcher (foto grande travaria a ilha); só o clipboard volta pra UI.
            SetClipboardImage = async bytes =>
            {
                System.Windows.Media.Imaging.BitmapSource img;
                try { img = await Task.Run(() => PhotoDecoder.Decode(bytes)); }
                catch (Exception ex) { Log.Warning(ex, "Foto do aparelho: não decodificou"); return false; }
                return await ui.InvokeAsync(() => ClipboardReader.SetImage(img)).Task;
            },
            Outbox = _outbox,
            SetClipboard = text => ui.InvokeAsync(() =>
            {
                if (!ClipboardReader.SetText(text)) throw new InvalidOperationException("Clipboard ocupado");
            }).Task,
            GetNotes = () => ui.InvokeAsync(() => Notas.Text).Task,
            // Gravou só na memória: a ponte responde erro em vez de "salvas no PC".
            SetNotes = text => ui.InvokeAsync(() =>
            {
                Notas.Write(text);
                if (Notas.SaveError is { } err) throw new IOException(err);
            }).Task,
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
            PcRoots = () => PcFiles.DefaultRoots(BridgeRules.UploadFolder(_settings.BridgeUploadFolder)),
            Push = _push,
            Screens = ScreenCapture.Screens,
            CaptureScreen = ScreenCapture.Capture,
            RemoteInput = Orbit.Services.RemoteInput.Execute,
            CursorPos = Orbit.Services.RemoteInput.CursorIn,
            Windows = OpenWindows.List,
            FocusWindow = OpenWindows.Focus,
            MicDevices = MicCapture.Devices,
            OpenMic = MicCapture.Open,
            RemoteChanged = state => ui.BeginInvoke(() =>
            {
                SetActivity(ActivityKind.TelaRemota, RemoteActivity(state));
                Dispositivos.SetRemote(state);
            }),
            ValidTokens = ValidTokenHashes,
            Notify = e => ui.BeginInvoke(() => Notify(new Peek(PeekTitle(e), e.Detail, "dispositivos", e.OpenPath), false)),
        };
    }

    // "iPad · Link aberto no PC"; sem aparelho (ex.: pareamento) fica só o título.
    public static string PeekTitle(BridgeEvent e) => e.Device is { Length: > 0 } d ? d + " · " + e.Title : e.Title;

    private void OnTimerFinished(int minutes) =>
        Notify(new Peek("Timer acabou", $"{minutes} min", "hoje"), true);

    private void OnPomodoroPhase(PomodoroPhase phase, int minutes) =>
        Notify(new Peek(PomodoroViewModel.PhaseLabel(phase), $"{minutes} min", "hoje"), _settings.PomodoroSoundEnabled);

    private void Register(TabViewModel tab) => _allTabs.Add(tab);

    // Ids visíveis na ordem de TabOrder. "ajustes" nunca some (senão não dá pra reverter).
    public static List<string> VisibleIds(ICollection<string> registered, ICollection<string> hidden) =>
        TabOrder.Where(id => registered.Contains(id) && (id == "ajustes" || !hidden.Contains(id))).ToList();

    // Aba Dispositivos escondida em Ajustes = recurso desligado: some de todo lugar (Clipboard, atalho,
    // bandeja, desenho, arrastar pra ilha, avisos de print/download) e a ponte desliga.
    public bool DevicesOn => !_settings.HiddenTabs.Contains("dispositivos");

    private void StopBridgeIfDevicesOff()
    {
        if (!DevicesOn && Dispositivos.Enabled) Dispositivos.Enabled = false;
    }

    // Sincroniza Tabs com as preferências sem recriar a coleção (a aba aberta continua aberta).
    public void ApplyTabVisibility()
    {
        if (Dispositivos is not null) StopBridgeIfDevicesOff(); // null: chamado antes de criar as abas
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
    // game: o app em tela cheia é um jogo (GameDetector) — vira faixa se a preferência estiver ligada.
    public void SetFullscreen(bool on, bool game = false) { _machine.SetFullscreen(on, game && _settings.GameStripEnabled); Refresh(); }

    // Mudo global: alerta curto (substitui o anterior) e o ícone do microfone na pílula muda.
    public bool? ToggleMicMute()
    {
        var muted = MicMute.Toggle();
        MicMuted = muted == true;
        RaisePeek(new Peek(muted switch { true => "Microfone mudo", false => "Microfone ligado", null => "Nenhum microfone" }, null, "controles")
            { Key = "mic-mudo", Duration = TimeSpan.FromSeconds(2), Compact = true });
        ShowCameraMicActivity();
        return muted;
    }

    private async Task SampleGameAsync()
    {
        if (_gameBusy) return;
        _gameBusy = true;
        try
        {
            bool reset = _gameReset;
            _gameReset = false;
            var (sys, gpu) = await Task.Run(() =>
            {
                if (reset) _gameGpu.Reset(); // aqui, em sequência com a leitura (nunca em paralelo)
                return (_gameSys.Read(disk: false), _gameGpu.Read());
            });
            if (Mode == IslandMode.Jogo) GameStatsText = GameStats(sys.CpuPercent, gpu, sys.Ram.Percent, DateTime.Now);
        }
        catch (Exception ex) { Log.Warning(ex, "Faixa do jogo: falha ao amostrar"); }
        finally { _gameBusy = false; }
    }

    public static string GameStats(double cpu, double gpu, double ram, DateTime now) =>
        $"CPU {cpu:0}%   GPU {gpu:0}%   RAM {ram:0}%   {now:HH:mm}";

    private void UpdateGameTimer()
    {
        if (Mode == IslandMode.Jogo)
        {
            if (_gameTimer.IsEnabled) return;
            GameStatsText = GameStats(0, 0, 0, DateTime.Now); // 1ª leitura de CPU/GPU sempre volta 0 (sem delta)
            _gameTimer.Start();
            _ = SampleGameAsync();
        }
        else if (_gameTimer.IsEnabled)
        {
            _gameTimer.Stop();
            _gameReset = true; // a próxima partida não faz média com o tempo parado
        }
    }
    public void SetActivity(ActivityKind kind, string? text, bool quiet = false) { _machine.SetActivity(kind, text, quiet); Refresh(); }
    public void RaisePeek(Peek peek) { _machine.RaisePeek(peek); Refresh(); }
    public void PeekExpired() { _machine.PeekDone(); Refresh(); }

    // Ponto único de alerta dos serviços/abas. Em tela cheia com a preferência ligada vira
    // toast do Windows; senão é peek na pílula (com bip curto, já que o peek é silencioso).
    // Avisos de verdade (Claude, Pomodoro, ponte, chuva), pro menu "Alertas recentes" da bandeja:
    // um alerta que passou enquanto você não olhava não some de vez.
    private readonly List<Peek> _recent = new();
    public const int MaxRecent = 15;

    public IReadOnlyList<(string Label, Action Open)> RecentAlerts() =>
        _recent.AsEnumerable().Reverse().Select(p => (RecentLabel(p), (Action)(() => OpenRecent(p)))).ToList();

    public static string RecentLabel(Peek p) =>
        $"{p.CreatedAt:HH:mm}  {p.Title}" + (string.IsNullOrEmpty(p.Detail) ? "" : " · " + p.Detail);

    private void OpenRecent(Peek p)
    {
        if (p.OpenPath is not null)
        {
            try { Process.Start(new ProcessStartInfo(p.OpenPath) { UseShellExecute = true }); }
            catch (Exception ex) { Log.Warning(ex, "Não abri {Caminho}", p.OpenPath); }
        }
        else if (p.TabId is not null) OpenTab(p.TabId);
        else Expand();
    }

    public void Notify(Peek peek, bool sound)
    {
        _recent.Add(peek);
        if (_recent.Count > MaxRecent) _recent.RemoveAt(0);
        // Também no aparelho (Web Push), com a ponte ligada. Eventos causados pelo próprio aparelho
        // (link aberto, arquivo recebido) não voltam pra ele.
        if (DevicesOn && _settings.BridgeEnabled && peek.TabId != "dispositivos")
            _ = _push.SendAsync(peek.Title, peek.Detail ?? "", ValidTokenHashes());
        if (ShouldToast(_machine.Fullscreen, _settings.AlertsAsToastInFullscreen))
        {
            ToastNotifier.Notify(peek.Title, peek.Detail ?? "", sound);
            return;
        }
        if (sound && !_machine.Fullscreen) SystemSounds.Asterisk.Play();
        RaisePeek(peek);
    }

    public static bool ShouldToast(bool fullscreen, bool preferToast) => fullscreen && preferToast;

    // Indicador de privacidade na pílula: tudo que o aparelho está usando agora.
    // 2+ aparelhos em tela cheia: o seletor não pode ir pra fila (apareceria muito depois, sem contexto).
    // blocked: tela cheia ou ilha escondida pela bandeja (o seletor ficaria invisível).
    public static DeviceChoice DecideChoice(int devices, bool blocked) =>
        devices == 0 ? DeviceChoice.None
        : devices == 1 ? DeviceChoice.Direct
        : blocked ? DeviceChoice.Blocked
        : DeviceChoice.Ask;

    // Um botão por aparelho (na ordem dada) e "Todos" com 2 ou mais. Quem chama ordena por CreatedAt.
    public static List<PeekAction> DeviceActions(IReadOnlyList<TokenInfo> devices, Action<IReadOnlyList<TokenInfo>> send)
    {
        var acts = devices.Select(d => new PeekAction(d.Name.Length > 12 ? d.Name[..12] + "…" : d.Name, () => send(new[] { d }))).ToList();
        if (devices.Count >= 2) acts.Add(new PeekAction("Todos", () => send(devices)));
        return acts;
    }

    // Hashes escolhidos que ainda existem (o aparelho pode ter sido revogado entre a escolha e o envio).
    public static List<string> LiveRecipients(IEnumerable<TokenInfo> chosen, IReadOnlyList<TokenInfo> current) =>
        chosen.Select(c => c.Hash).Where(h => current.Any(t => t.Hash == h)).ToList();

    // A janela marca quando a ilha está escondida pela bandeja.
    public bool UserHidden { get; set; }

    private List<TokenInfo> Devices() => _tokens.List().OrderBy(t => t.CreatedAt).ToList();

    // Todo envio PC → dispositivo passa por aqui. what: o que vai (aparece no seletor).
    public void ChooseDevice(string what, Action<IReadOnlyList<TokenInfo>> send)
    {
        if (!DevicesOn) return; // recurso desligado: nem o "Ligue a ponte"
        if (!_settings.BridgeEnabled) { RaisePeek(new Peek("Ligue a ponte", "Aba Dispositivos → Ponte ligada", "dispositivos")); return; }
        var devices = Devices();
        switch (DecideChoice(devices.Count, _machine.Fullscreen || UserHidden))
        {
            case DeviceChoice.None:
                RaisePeek(new Peek("Pareie um dispositivo", "Aba Dispositivos → Parear", "dispositivos"));
                break;
            case DeviceChoice.Direct:
                send(devices);
                break;
            case DeviceChoice.Blocked:
                ToastNotifier.Notify("Não mandei", _machine.Fullscreen
                    ? "Saia da tela cheia para escolher o dispositivo"
                    : "Mostre a ilha para escolher o dispositivo", false);
                break;
            default:
                Collapse(); // o alerta fica atrás do painel aberto
                RaisePeek(new Peek("Mandar para…", what, "dispositivos")
                {
                    Key = IslandMachine.DeviceChoiceKey,
                    Duration = TimeSpan.FromSeconds(10),
                    Actions = DeviceActions(devices, send),
                });
                break;
        }
    }

    // Caixa de saída: copia (fora da UI thread — pode ser arquivo grande), confirma na pílula e avisa
    // os dispositivos escolhidos, cujo toque abre "Recebidos do PC". name: nome amigável (só com 1 arquivo).
    public void SendFiles(IReadOnlyList<string> files, IReadOnlyList<TokenInfo> to, string? name = null)
    {
        if (files.Count == 0 || !DevicesOn) return; // aviso antigo na tela depois de esconder a aba
        var live = LiveRecipients(to, _tokens.List());
        if (live.Count == 0) { RaisePeek(new Peek("Dispositivo revogado", "Nada foi copiado", "dispositivos")); return; }
        // Copiar até 2 GB demora: a pílula mostra "Preparando 2/5" e um clique nela cancela.
        // Um envio novo não cancela o anterior: só passa a ser o mostrado (e o cancelável) na pílula.
        var cts = _sendCts = new System.Threading.CancellationTokenSource();
        var ui = Application.Current?.Dispatcher;
        _ = Task.Run(() =>
        {
            var added = new List<OutboxItem>();
            for (int i = 0; i < files.Count && !cts.IsCancellationRequested; i++)
            {
                var f = files[i];
                var text = SendProgressText(i, files.Count, Path.GetFileName(f));
                ui?.BeginInvoke(() => { if (_sendCts == cts) SetActivity(ActivityKind.Envio, text); });
                try { if (_outbox.AddFile(f, DateTime.Now, live, files.Count == 1 ? name : null, cts.Token) is { } item) added.Add(item); }
                catch (OperationCanceledException) { }
                catch (Exception ex) { Log.Warning(ex, "Mandar pro dispositivo: não copiou {Arquivo}", f); }
            }
            ui?.BeginInvoke(() =>
            {
                if (_sendCts == cts) { _sendCts = null; SetActivity(ActivityKind.Envio, null); }
                if (cts.IsCancellationRequested)
                {
                    // cancelado: o que já tinha ido pra caixa de saída sai também (nada pela metade no aparelho)
                    foreach (var item in added) foreach (var d in live) _outbox.Remove(item.Id, d);
                    RaisePeek(new Peek("Envio cancelado", added.Count > 0 ? $"{added.Count} arquivo(s) retirados" : null, null));
                }
                else Announce(added, files.Count, live);
                cts.Dispose();
            });
        });
    }

    private System.Threading.CancellationTokenSource? _sendCts;

    public static string SendProgressText(int index, int total, string file) =>
        (total == 1 ? "Preparando " + file : $"Preparando {index + 1}/{total} · {file}") + " — clique pra cancelar";

    private readonly AgendaViewModel _agenda;

    // Clique na pílula com a reunião na vez: entra nela em vez de abrir a ilha.
    public bool JoinMeetingIfShowing() =>
        _machine.Mode == IslandMode.Atividade && _machine.CurrentActivity == ActivityKind.Agenda && _agenda.JoinCountdown();

    // Clique na pílula com o envio na vez: cancela em vez de abrir a ilha.
    public bool CancelSendIfShowing()
    {
        if (_sendCts is null || _machine.Mode != IslandMode.Atividade || _machine.CurrentActivity != ActivityKind.Envio) return false;
        _sendCts.Cancel();
        return true;
    }

    public enum ClipSend { Files, Image, Text, Private, Empty }

    // O que mandar do clipboard: arquivos copiados no Explorer vão como os originais; senha copiada
    // de gerenciador (privado) nunca sai do PC.
    public static ClipSend DecideClip(string[]? files, bool isPrivate, bool hasImage, string? text) =>
        isPrivate ? ClipSend.Private
        : files is { Length: > 0 } ? ClipSend.Files
        : hasImage ? ClipSend.Image
        : !string.IsNullOrWhiteSpace(text) ? ClipSend.Text
        : ClipSend.Empty;

    // Ctrl+Alt+I / bandeja: manda o que estiver no clipboard agora (só escolhe o destino com conteúdo válido).
    public void SendClipboard()
    {
        string[]? files = null;
        try { if (System.Windows.Clipboard.ContainsFileDropList()) files = SendToDevice.FilesFrom(System.Windows.Clipboard.GetFileDropList().Cast<string>()); }
        catch (Exception ex) { Log.Warning(ex, "Mandar clipboard: não leu os arquivos"); }
        // com arquivos não decodifica nada, mas o marcador de privado vale igual
        var snap = files is { Length: > 0 } ? (ClipboardReader.IsPrivateNow() ? new ClipSnapshot(null, null, true) : null) : ClipboardReader.Read();
        switch (DecideClip(files, snap?.IsPrivate == true, snap?.Image is not null, snap?.Text))
        {
            case ClipSend.Files:
                ChooseDevice(files!.Length == 1 ? Path.GetFileName(files[0]) : $"{files.Length} arquivos", d => SendFiles(files, d));
                break;
            case ClipSend.Image:
                var png = Path.Combine(Path.GetTempPath(), $"orbit-clip-{Guid.NewGuid():N}.png");
                try
                {
                    var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
                    enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(snap!.Image!));
                    using (var fs = File.Create(png)) enc.Save(fs);
                    ChooseDevice("imagem", d => SendFiles(new[] { png }, d, $"imagem-{DateTime.Now:yyyyMMdd-HHmmss}.png"));
                }
                catch (Exception ex) { Log.Warning(ex, "Mandar clipboard: não salvou a imagem"); }
                break;
            case ClipSend.Text:
                var text = snap!.Text!;
                ChooseDevice("texto", d => SendText(text, d));
                break;
            case ClipSend.Private:
                RaisePeek(new Peek("Não mandei", "Conteúdo privado (senha?) fica só no PC", "clipboard"));
                break;
            default:
                RaisePeek(new Peek("Nada pra mandar", "Selecione algo e aperte Ctrl+Alt+I", "clipboard"));
                break;
        }
    }

    public void SendText(string text, IReadOnlyList<TokenInfo> to)
    {
        var live = LiveRecipients(to, _tokens.List());
        if (live.Count == 0) { RaisePeek(new Peek("Dispositivo revogado", "Nada foi copiado", "dispositivos")); return; }
        var item = _outbox.AddText(text, DateTime.Now, live);
        Announce(item is null ? new() : new() { item }, 1, live);
    }

    private void Sweep()
    {
        _outbox.Sweep(DateTime.Now);
        DeleteOldTemp(Path.GetTempPath(), DateTime.Now);
    }

    // PNGs que a ilha cria pra mandar ao aparelho (já copiados pra caixa de saída, ou envio cancelado).
    // Uma hora de folga: um envio em andamento ainda lê o arquivo.
    public static void DeleteOldTemp(string dir, DateTime now)
    {
        foreach (var pattern in new[] { "orbit-clip-*.png", "orbit-desenho-*.png" })
            foreach (var f in Directory.EnumerateFiles(dir, pattern))
                try { if (now - File.GetLastWriteTime(f) > TimeSpan.FromHours(1)) File.Delete(f); } catch { }
    }

    private void Announce(List<OutboxItem> added, int asked, IReadOnlyList<string> live)
    {
        if (added.Count == 0) { RaisePeek(new Peek("Não deu pra mandar", "Arquivo sumiu ou passa de 2 GB", "dispositivos")); return; }
        var (title, body) = OutboxAnnouncement(added);
        var current = _tokens.List();
        var dest = live.Count == 1 ? $"Mandado para {current.FirstOrDefault(t => t.Hash == live[0])?.Name ?? "dispositivo"}" : $"Mandado para {live.Count} dispositivos";
        RaisePeek(new Peek(dest, body + (added.Count < asked ? $" ({asked - added.Count} falharam)" : ""), "dispositivos"));
        _ = _push.SendAsync(title, body, ValidTokenHashes(), onlyTokens: added.SelectMany(i => i.Recipients ?? Array.Empty<string>()).ToHashSet(), url: OutboxPushUrl(added));
    }

    public sealed record DropTarget(string Label, IReadOnlyList<TokenInfo> Devices);
    public ObservableCollection<DropTarget> DropTargets { get; } = new();

    // Zonas de soltar: uma por aparelho + "Todos"; com um aparelho, zona única com o nome dele.
    public void BuildDropTargets()
    {
        // Ponte desligada: sem zonas, o drop cai em ChooseDevice ("Ligue a ponte").
        var devices = _settings.BridgeEnabled ? Devices() : new List<TokenInfo>();
        var want = new List<DropTarget>();
        if (devices.Count == 1) want.Add(new DropTarget(devices[0].Name, devices));
        else
        {
            foreach (var d in devices) want.Add(new DropTarget(d.Name, new[] { d }));
            if (devices.Count >= 2) want.Add(new DropTarget("Todos", devices));
        }
        // Já igual: não mexe (Clear destrói o Border sob o cursor e o WPF dispara DragEnter de novo).
        if (want.Count == DropTargets.Count && want.Zip(DropTargets).All(z => z.First.Label == z.Second.Label
            && z.First.Devices.Select(t => t.Hash).SequenceEqual(z.Second.Devices.Select(t => t.Hash)))) return;
        DropTargets.Clear();
        foreach (var t in want) DropTargets.Add(t);
    }

    public static (string Title, string Body) OutboxAnnouncement(IReadOnlyList<OutboxItem> added)
    {
        if (added.Count > 1) return ($"{added.Count} arquivos do PC", string.Join(", ", added.Select(i => i.Name)));
        var i = added[0];
        if (i.Text is { } t)
            return IsLink(t) ? ("Link do PC", t.Trim()) : ("Texto do PC", t.Length > 160 ? t[..160].TrimEnd() + "…" : t);
        return ("Arquivo do PC", $"{i.Name} · {Orbit.Tabs.Stats.StatsViewModel.FormatBytes(i.Size)}");
    }

    // Link sozinho: tocar no aviso abre o próprio link no aparelho. O resto abre "Recebidos do PC"
    // (texto precisa de um toque na página pra ir pro clipboard — regra do iOS).
    public static string OutboxPushUrl(IReadOnlyList<OutboxItem> added) =>
        added is [{ Text: { } t }] && IsLink(t) ? t.Trim() : "./#recebidos";

    private static bool IsLink(string t) => ClipHistory.ClassifyText(t) == ClipKind.Link;

    private void ShowCameraMicActivity()
    {
        CameraMicView = CameraMicWatcher.Display(_cameraMicUses, DateTime.Now, MicMuted);
        SetActivity(ActivityKind.CameraMic, CameraMicWatcher.Describe(_cameraMicUses, DateTime.Now));
    }

    // Ao abrir: resultado da última troca (se foi agora) ou "tem versão nova no código".
    private async Task AnnounceUpdateAsync()
    {
        try
        {
            if (SelfUpdate.RecentOutcome(SelfUpdate.LastLogLine(), DateTime.Now) is { } r)
            {
                RaisePeek(r.Ok
                    ? new Peek("Orbit atualizado", SelfUpdate.Current.Commit is { } c ? $"Versão {c}" : null, "ajustes")
                    : new Peek("Atualização desfeita", r.Message, "ajustes"));
                return;
            }
            await Task.Delay(TimeSpan.FromSeconds(15)); // não disputa o começo com o resto
            var s = await SelfUpdate.CheckAsync();
            if (s.CanUpdate && s.Behind > 0)
                RaisePeek(new Peek("Atualização do Orbit disponível", SelfUpdate.Describe(s), "ajustes"));
        }
        catch (Exception ex) { Log.Warning(ex, "Autoatualização: falha ao verificar"); }
    }

    // Republica a partir do código e troca os arquivos (script com backup e volta atrás).
    public async Task UpdateNowAsync()
    {
        if (_updating) return;
        _updating = true; // antes do 1º await: bandeja e Ajustes ao mesmo tempo não publicam duas vezes
        try
        {
            var s = await SelfUpdate.CheckAsync();
            if (!s.CanUpdate) { RaisePeek(new Peek("Não dá pra atualizar", s.Reason, "ajustes")); return; }
            Collapse(); // a pílula mostra o progresso; a ilha aberta só atrapalharia
            SetActivity(ActivityKind.Atualizacao, s.DownloadUrl is null ? "Atualizando Orbit…" : "Baixando Orbit…");
            var staging = await Task.Run(() => SelfUpdate.PrepareAsync(s));
            SetActivity(ActivityKind.Atualizacao, "Reiniciando o Orbit…");
            SelfUpdate.LaunchSwap(staging);
            ExitForUpdate?.Invoke();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Autoatualização: falhou");
            RaisePeek(new Peek("Atualização falhou", ex.Message, "ajustes"));
        }
        finally
        {
            _updating = false;
            SetActivity(ActivityKind.Atualizacao, null);
        }
    }

    private async Task CheckRainAsync()
    {
        if (!_settings.RainAlertEnabled) return;
        _rainLocation ??= await _api.GetLocationAsync();
        if (_rainLocation is null || await _api.GetRainAsync(_rainLocation.Lat, _rainLocation.Lon) is not { } f) return;
        if (_rain.Next(f) is { } minutes)
            Notify(new Peek(RainAlert.Text(minutes), _rainLocation.City, "hoje"), false); // Notify: vai pro aparelho também
    }

    // Print novo: 6 s com um botão por aparelho (+ "Todos") e "Abrir"; sem aparelhos, só "Abrir".
    public Peek ScreenshotPeek(string path, IReadOnlyList<TokenInfo> devices)
    {
        var actions = DeviceActions(devices, d => SendFiles(new[] { path }, d));
        actions.Add(new PeekAction("Texto", () => _ = CopyTextFromImageAsync(path)));
        actions.Add(new PeekAction("Abrir", () =>
        {
            try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
            catch (Exception ex) { Log.Warning(ex, "Não abri o print"); }
        }));
        return new Peek("Print salvo", Path.GetFileName(path), null) { Key = "print", Duration = TimeSpan.FromSeconds(6), Actions = actions };
    }

    // "Texto" no aviso de print: OCR do Windows, texto vai pro clipboard e a pílula diz quantas linhas.
    private async Task CopyTextFromImageAsync(string path)
    {
        var text = await TextFromImage.FromFileAsync(path);
        if (!string.IsNullOrEmpty(text))
        {
            if (ClipboardReader.SetText(text)) await Clips.IngestAsync(new ClipSnapshot(text, null, false)); // entra no histórico
            else text = null;
        }
        RaisePeek(new Peek(TextFromImage.Summary(text), string.IsNullOrEmpty(text) ? null : text.Split('\n')[0], "clipboard")
            { Key = "ocr", Duration = TimeSpan.FromSeconds(3) });
    }

    // Download terminado: 8 s com "Abrir", "Pasta" (Explorer com o arquivo selecionado) e um botão por aparelho.
    public Peek DownloadPeek(string path, IReadOnlyList<TokenInfo> devices)
    {
        var actions = new List<PeekAction>
        {
            new("Abrir", () =>
            {
                try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
                catch (Exception ex) { Log.Warning(ex, "Não abri o download"); }
            }),
            new("Pasta", () =>
            {
                try { Process.Start("explorer.exe", $"/select,\"{path}\""); }
                catch (Exception ex) { Log.Warning(ex, "Não abri a pasta do download"); }
            }),
        };
        actions.AddRange(DeviceActions(devices, d => SendFiles(new[] { path }, d)));
        return new Peek("Download concluído", Path.GetFileName(path), null) { Key = "download", Duration = TimeSpan.FromSeconds(8), Actions = actions };
    }

    public static bool SignalAllowed(OrbitSettings s, SignalKind kind) =>
        s.SystemSignalsEnabled && !s.MutedSignals.Contains(kind.ToString());

    // Volume some rápido (1,5 s); os outros ficam 2,5 s. A mesma chave substitui o alerta anterior.
    public static Peek SignalPeek(SystemSignal s) => new(s.Title, s.Detail, null)
    {
        Key = "sinal:" + s.Kind,
        Duration = TimeSpan.FromSeconds(s.Kind == SignalKind.Volume ? 1.5 : 2.5),
        Level = s.Level,
    };

    // ponytail: aparelhos comparados pelo nome (RemoteState só tem nomes); dois com o mesmo nome parecem um só.
    public static string? RemoteActivity(RemoteState s)
    {
        var viewer = s.Viewer ?? "Aparelho";
        var controller = s.Controller ?? "Aparelho";
        var screen = s.Controlling
            ? (s.Viewing && viewer != controller ? $"{viewer} vendo a tela · {controller} controlando o PC" : controller + " controlando o PC")
            : s.Viewing ? viewer + " vendo a tela" : null;
        var screenDevices = new[] { s.Viewing ? s.Viewer : null, s.Controlling ? s.Controller : null };
        if (s.Listening)
        {
            var mic = s.Listener ?? "Aparelho";
            screen = screen is null ? "Microfone no " + mic
                : screen + (screenDevices.All(d => d is null || d == s.Listener) ? " · microfone" : " · Microfone no " + mic);
        }
        if (s.Camera is not { } cam) return screen;
        if (screen is null) return cam + " vendo a câmera";
        var others = screenDevices.Append(s.Listening ? s.Listener : null);
        return screen + (others.All(d => d is null || d == cam) ? " · câmera" : " · Câmera no " + cam);
    }

    private IReadOnlyCollection<string> ValidTokenHashes() => _tokens.List().Select(t => t.Hash).ToHashSet();

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
        ShowAudioBars = _machine.Mode == IslandMode.Discreta && _machine.CurrentActivity == ActivityKind.Midia;
        // A barrinha de progresso serve ao Pomodoro e ao timer: mostra o da atividade da vez.
        ShowPomodoroBar = _machine.Mode == IslandMode.Discreta && _machine.CurrentActivity is ActivityKind.Pomodoro or ActivityKind.Timer;
        if (_machine.CurrentActivity == ActivityKind.Timer) PomodoroProgress = TimerProgress;
        ShowCameraMic = _machine.Mode == IslandMode.Atividade && _machine.CurrentActivity == ActivityKind.CameraMic;
        ShowUpdating = _machine.Mode == IslandMode.Atividade && _machine.CurrentActivity == ActivityKind.Atualizacao;
        Mode = _machine.Mode; // por último: a janela reage a Mode e já lê Peek/ActivityText novos
        UpdateGameTimer();
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
