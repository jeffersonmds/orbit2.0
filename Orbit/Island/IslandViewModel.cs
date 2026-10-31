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

public enum DeviceChoice { None, Direct, Ask, Blocked }

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
    private readonly Outbox _outbox = new(Outbox.DefaultDir);
    private readonly CameraMicWatcher _cameraMic = new();
    private readonly SystemSignals _signals = new();
    private readonly ScreenshotWatcher _screenshots = new();
    private readonly RainAlert _rain = new();
    private readonly System.Windows.Threading.DispatcherTimer _rainTimer = new() { Interval = TimeSpan.FromMinutes(20) };
    private LocationResult? _rainLocation;
    private IReadOnlyList<CameraMicUse> _cameraMicUses = Array.Empty<CameraMicUse>();
    // só pra atualizar o "· 12 min" enquanto algo está em uso
    private readonly System.Windows.Threading.DispatcherTimer _cameraMicClock = new() { Interval = TimeSpan.FromSeconds(30) };
    private readonly System.Threading.CancellationTokenSource _sendToCts = new();
    private readonly ClaudeHookServer _claudeServer = new();
    private readonly List<TabViewModel> _allTabs = new();

    public ObservableCollection<TabViewModel> Tabs { get; } = new();
    public PomodoroViewModel Pomodoro { get; }
    public NowPlayingViewModel NowPlaying { get; } = new();

    [ObservableProperty] private TabViewModel? _selectedTab;
    [ObservableProperty] private IslandMode _mode = IslandMode.Repouso;
    [ObservableProperty] private string _activityText = "";
    [ObservableProperty] private Peek? _peek;
    // Barrinhas no ritmo do som: só com a pílula em Atividade e a música como atividade da vez.
    [ObservableProperty] private bool _showAudioBars;
    // Câmera/microfone em uso: a pílula troca o texto pelo desenho próprio (bolinha, nome, ícones, tempo).
    [ObservableProperty] private bool _showCameraMic;
    [ObservableProperty] private CameraMicDisplay? _cameraMicView;

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
        _tokens.SeedPermissions(new DevicePermissions(settings.BridgeCameraEnabled, settings.BridgePcFilesEnabled,
            settings.BridgeScreenEnabled, settings.BridgeControlEnabled, settings.BridgeMicEnabled, settings.BridgeKeyboardEnabled));
        _push.MigrateTokenRefs(_tokens.List());
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
        Register(new AjustesViewModel(settings, Pomodoro, () => _allTabs, ApplyTabVisibility, () => MonitorChanged?.Invoke(),
            SelfUpdate.CheckAsync, UpdateNowAsync));
        Register(new HojeViewModel(_api, Pomodoro, TodoStore.FilePath));
        Register(new ControlesViewModel(NowPlaying));
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
        _claudeServer.Start();
        // Clipboard: captura sempre por evento; aba escondida em Ajustes = não grava nada.
        Clips = new ClipsViewModel(ClipboardStore.FilePath, ClipboardStore.ImagesDir,
            () => !settings.HiddenTabs.Contains("clipboard"));
        Register(Clips);
        Clips.SendToDeviceRequested += item =>
        {
            if (item.Kind == ClipKind.Image && item.ImagePath is { } img)
                ChooseDevice("imagem", d => SendFiles(new[] { img }, d, $"imagem-{item.CopiedAt:yyyyMMdd-HHmmss}.png"));
            else if (item.Text is { } text) ChooseDevice("texto", d => SendText(text, d));
        };
        // Explorer → Enviar para → "Dispositivo (Orbit)" chega por pipe (thread do pool).
        SendToDevice.Listen(files => Application.Current?.Dispatcher.BeginInvoke(() =>
            ChooseDevice(files.Length == 1 ? Path.GetFileName(files[0]) : $"{files.Length} arquivos", d => SendFiles(files, d))), _sendToCts.Token);
        Register(new AppsViewModel(settings, AppIcons.CacheDir, Collapse));
        // Ponte com o iPhone: desligada até o usuário ligar na aba (BridgeEnabled).
        IPhone = new IPhoneViewModel(settings, _tokens, new AuditLog(AuditLog.FilePath),
            BuildBridgeHandlers, Path.Combine(AppContext.BaseDirectory, "wwwroot"), _outbox.RemoveDevice);
        Register(IPhone);
        _ = IPhone.StartIfEnabledAsync();
        Register(Notas);
        _ = AnnounceUpdateAsync();
        // Quem está usando câmera/microfone: o watcher avisa (thread própria) e a pílula mostra.
        _cameraMicClock.Tick += (_, _) => ShowCameraMicActivity();
        _cameraMic.Changed += uses => Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            _cameraMicUses = uses;
            if (uses.Count > 0) _cameraMicClock.Start(); else _cameraMicClock.Stop();
            ShowCameraMicActivity();
        });
        _cameraMic.Start();
        // Eventos do sistema: alerta curto na pílula (não vai pro iPhone nem vira toast em tela cheia).
        _signals.Raised += sig => Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            if (_settings.SystemSignalsEnabled) RaisePeek(SignalPeek(sig));
        });
        _signals.Start();
        _screenshots.Captured += path => Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            if (_settings.ScreenshotOfferEnabled) RaisePeek(ScreenshotPeek(path, _settings.BridgeEnabled ? Devices() : Array.Empty<TokenInfo>()));
        });
        _screenshots.Start();
        // "Vai chover": a cada 20 min (1ª checagem 1 min depois de abrir, pra não disputar o começo).
        _rainTimer.Tick += async (_, _) => { _rainTimer.Interval = TimeSpan.FromMinutes(20); await CheckRainAsync(); };
        _rainTimer.Interval = TimeSpan.FromMinutes(1);
        _rainTimer.Start();
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
            SetClipboardImage = bytes => ui.InvokeAsync(() =>
            {
                try { return ClipboardReader.SetImage(PhotoDecoder.Decode(bytes)); }
                catch (Exception ex) { Log.Warning(ex, "Foto do iPhone: não decodificou"); return false; }
            }).Task,
            Outbox = _outbox,
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
            PcRoots = () => PcFiles.DefaultRoots(BridgeRules.UploadFolder(_settings.BridgeUploadFolder)),
            Push = _push,
            Screens = ScreenCapture.Screens,
            CaptureScreen = ScreenCapture.Capture,
            RemoteInput = Orbit.Services.RemoteInput.Execute,
            MicDevices = MicCapture.Devices,
            OpenMic = MicCapture.Open,
            RemoteChanged = state => ui.BeginInvoke(() =>
            {
                SetActivity(ActivityKind.TelaRemota, RemoteActivity(state));
                IPhone.SetRemote(state);
            }),
            ValidTokens = ValidTokenHashes,
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

    // Indicador de privacidade na pílula: tudo que o iPhone está usando agora.
    // 2+ aparelhos em tela cheia: o seletor não pode ir pra fila (apareceria muito depois, sem contexto).
    public static DeviceChoice DecideChoice(int devices, bool fullscreen) =>
        devices == 0 ? DeviceChoice.None
        : devices == 1 ? DeviceChoice.Direct
        : fullscreen ? DeviceChoice.Blocked
        : DeviceChoice.Ask;

    // Um botão por aparelho (na ordem dada) e "Todos" com 2 ou mais. Quem chama ordena por CreatedAt.
    public static List<PeekAction> DeviceActions(IReadOnlyList<TokenInfo> devices, Action<IReadOnlyList<TokenInfo>> send)
    {
        var acts = devices.Select(d => new PeekAction(d.Name, () => send(new[] { d }))).ToList();
        if (devices.Count >= 2) acts.Add(new PeekAction("Todos", () => send(devices)));
        return acts;
    }

    // Hashes escolhidos que ainda existem (o aparelho pode ter sido revogado entre a escolha e o envio).
    public static List<string> LiveRecipients(IEnumerable<TokenInfo> chosen, IReadOnlyList<TokenInfo> current) =>
        chosen.Select(c => c.Hash).Where(h => current.Any(t => t.Hash == h)).ToList();

    private List<TokenInfo> Devices() => _tokens.List().OrderBy(t => t.CreatedAt).ToList();

    // Todo envio PC → dispositivo passa por aqui. what: o que vai (aparece no seletor).
    public void ChooseDevice(string what, Action<IReadOnlyList<TokenInfo>> send)
    {
        if (!_settings.BridgeEnabled) { RaisePeek(new Peek("Ligue a ponte", "Aba iPhone → Ponte ligada", "iphone")); return; }
        var devices = Devices();
        switch (DecideChoice(devices.Count, _machine.Fullscreen))
        {
            case DeviceChoice.None:
                RaisePeek(new Peek("Pareie um dispositivo", "Aba iPhone → Parear", "iphone"));
                break;
            case DeviceChoice.Direct:
                send(devices);
                break;
            case DeviceChoice.Blocked:
                ToastNotifier.Notify("Não mandei", "Saia da tela cheia para escolher o dispositivo", false);
                break;
            default:
                Collapse(); // o alerta fica atrás do painel aberto
                RaisePeek(new Peek("Mandar para…", what, "iphone")
                {
                    Key = "escolher-dispositivo",
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
        if (files.Count == 0) return;
        var live = LiveRecipients(to, _tokens.List());
        if (live.Count == 0) { RaisePeek(new Peek("Dispositivo revogado", "Nada foi copiado", "iphone")); return; }
        _ = Task.Run(() =>
        {
            var added = files.Select(f =>
            {
                try { return _outbox.AddFile(f, DateTime.Now, live, files.Count == 1 ? name : null); }
                catch (Exception ex) { Log.Warning(ex, "Mandar pro dispositivo: não copiou {Arquivo}", f); return null; }
            }).OfType<OutboxItem>().ToList();
            Application.Current?.Dispatcher.BeginInvoke(() => Announce(added, files.Count, to));
        });
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
        var snap = files is { Length: > 0 } ? null : ClipboardReader.Read();
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
        if (live.Count == 0) { RaisePeek(new Peek("Dispositivo revogado", "Nada foi copiado", "iphone")); return; }
        var item = _outbox.AddText(text, DateTime.Now, live);
        Announce(item is null ? new() : new() { item }, 1, to);
    }

    private void Announce(List<OutboxItem> added, int asked, IReadOnlyList<TokenInfo> to)
    {
        if (added.Count == 0) { RaisePeek(new Peek("Não deu pra mandar", "Arquivo sumiu ou passa de 2 GB", "iphone")); return; }
        var (title, body) = OutboxAnnouncement(added);
        var dest = to.Count == 1 ? $"Mandado para {to[0].Name}" : $"Mandado para {to.Count} dispositivos";
        RaisePeek(new Peek(dest, body + (added.Count < asked ? $" ({asked - added.Count} falharam)" : ""), "iphone"));
        _ = _push.SendAsync(title, body, ValidTokenHashes(), onlyTokens: added.SelectMany(i => i.Recipients ?? Array.Empty<string>()).ToHashSet(), url: OutboxPushUrl(added));
    }

    public sealed record DropTarget(string Label, IReadOnlyList<TokenInfo> Devices);
    public ObservableCollection<DropTarget> DropTargets { get; } = new();

    // Zonas de soltar: uma por aparelho + "Todos"; com um aparelho, zona única com o nome dele.
    public void BuildDropTargets()
    {
        DropTargets.Clear();
        var devices = Devices();
        if (devices.Count == 1) { DropTargets.Add(new DropTarget(devices[0].Name, devices)); return; }
        foreach (var d in devices) DropTargets.Add(new DropTarget(d.Name, new[] { d }));
        if (devices.Count >= 2) DropTargets.Add(new DropTarget("Todos", devices));
    }

    public static (string Title, string Body) OutboxAnnouncement(IReadOnlyList<OutboxItem> added)
    {
        if (added.Count > 1) return ($"{added.Count} arquivos do PC", string.Join(", ", added.Select(i => i.Name)));
        var i = added[0];
        if (i.Text is { } t)
            return IsLink(t) ? ("Link do PC", t.Trim()) : ("Texto do PC", t.Length > 160 ? t[..160].TrimEnd() + "…" : t);
        return ("Arquivo do PC", $"{i.Name} · {Orbit.Tabs.Stats.StatsViewModel.FormatBytes(i.Size)}");
    }

    // Link sozinho: tocar no aviso abre o próprio link no iPhone. O resto abre "Recebidos do PC"
    // (texto precisa de um toque na página pra ir pro clipboard — regra do iOS).
    public static string OutboxPushUrl(IReadOnlyList<OutboxItem> added) =>
        added is [{ Text: { } t }] && IsLink(t) ? t.Trim() : "./#recebidos";

    private static bool IsLink(string t) => ClipHistory.ClassifyText(t) == ClipKind.Link;

    private void ShowCameraMicActivity()
    {
        CameraMicView = CameraMicWatcher.Display(_cameraMicUses, DateTime.Now);
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
        var s = await SelfUpdate.CheckAsync();
        if (!s.CanUpdate) { RaisePeek(new Peek("Não dá pra atualizar", s.Reason, "ajustes")); return; }
        _updating = true;
        SetActivity(ActivityKind.Atualizacao, "Atualizando Orbit…");
        try
        {
            var staging = await Task.Run(SelfUpdate.PublishAsync);
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
            Notify(new Peek(RainAlert.Text(minutes), _rainLocation.City, "hoje"), false); // Notify: vai pro iPhone também
    }

    // Print novo: 6 s com um botão por aparelho (+ "Todos") e "Abrir"; sem aparelhos, só "Abrir".
    public Peek ScreenshotPeek(string path, IReadOnlyList<TokenInfo> devices)
    {
        var actions = DeviceActions(devices, d => SendFiles(new[] { path }, d));
        actions.Add(new PeekAction("Abrir", () =>
        {
            try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
            catch (Exception ex) { Log.Warning(ex, "Não abri o print"); }
        }));
        return new Peek("Print salvo", Path.GetFileName(path), null) { Key = "print", Duration = TimeSpan.FromSeconds(6), Actions = actions };
    }

    // Volume some rápido (1,5 s); os outros ficam 2,5 s. A mesma chave substitui o alerta anterior.
    public static Peek SignalPeek(SystemSignal s) => new(s.Title, s.Detail, null)
    {
        Key = "sinal:" + s.Kind,
        Duration = TimeSpan.FromSeconds(s.Kind == SignalKind.Volume ? 1.5 : 2.5),
        Level = s.Level,
    };

    public static string? RemoteActivity(RemoteState s)
    {
        var screen = s.Controlling ? "iPhone controlando o PC" : s.Viewing ? "Tela no iPhone" : null;
        return (screen, s.Listening) switch
        {
            (null, false) => null,
            (null, true) => "Microfone no iPhone",
            (_, false) => screen,
            _ => screen + " · microfone",
        };
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
        ShowAudioBars = _machine.Mode == IslandMode.Atividade && _machine.CurrentActivity == ActivityKind.Midia;
        ShowCameraMic = _machine.Mode == IslandMode.Atividade && _machine.CurrentActivity == ActivityKind.CameraMic;
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
