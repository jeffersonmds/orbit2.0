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
    private readonly Outbox _outbox = new(Outbox.DefaultDir);
    private readonly CameraMicWatcher _cameraMic = new();
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
        Register(new AjustesViewModel(settings, Pomodoro, () => _allTabs, ApplyTabVisibility, () => MonitorChanged?.Invoke(),
            SelfUpdate.CheckAsync, UpdateNowAsync));
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
        Clips.SendToIPhoneRequested += item =>
        {
            if (item.Kind == ClipKind.Image && item.ImagePath is { } img) SendFilesToIPhone(new[] { img }, $"imagem-{item.CopiedAt:yyyyMMdd-HHmmss}.png");
            else if (item.Text is { } text) SendTextToIPhone(text);
        };
        // Explorer → Enviar para → "iPhone (Orbit)" chega por pipe (thread do pool).
        SendToIPhone.Listen(files => Application.Current?.Dispatcher.BeginInvoke(() => SendFilesToIPhone(files)), _sendToCts.Token);
        Register(new AppsViewModel(settings, AppIcons.CacheDir, Collapse));
        // Ponte com o iPhone: desligada até o usuário ligar na aba (BridgeEnabled).
        IPhone = new IPhoneViewModel(settings, _tokens, new AuditLog(AuditLog.FilePath),
            BuildBridgeHandlers, Path.Combine(AppContext.BaseDirectory, "wwwroot"));
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
            CameraEnabled = () => _settings.BridgeCameraEnabled,
            PcFilesEnabled = () => _settings.BridgePcFilesEnabled,
            PcRoots = () => PcFiles.DefaultRoots(BridgeRules.UploadFolder(_settings.BridgeUploadFolder)),
            Push = _push,
            ScreenEnabled = () => _settings.BridgeScreenEnabled,
            ControlEnabled = () => _settings.BridgeControlEnabled,
            KeyboardEnabled = () => _settings.BridgeKeyboardEnabled,
            Screens = ScreenCapture.Screens,
            CaptureScreen = ScreenCapture.Capture,
            RemoteInput = Orbit.Services.RemoteInput.Execute,
            MicEnabled = () => _settings.BridgeMicEnabled,
            MicDevices = MicCapture.Devices,
            OpenMic = MicCapture.Open,
            RemoteChanged = state => ui.BeginInvoke(() =>
            {
                SetActivity(ActivityKind.TelaRemota, RemoteActivity(state));
                IPhone.SetRemote(state);
            }),
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

    // Indicador de privacidade na pílula: tudo que o iPhone está usando agora.
    // Caixa de saída pro iPhone: copia (fora da UI thread — pode ser arquivo grande), confirma na pílula
    // e avisa o iPhone, cujo toque abre "Recebidos do PC". name: nome amigável (só com 1 arquivo).
    public void SendFilesToIPhone(IReadOnlyList<string> files, string? name = null)
    {
        if (files.Count == 0) return;
        if (!_settings.BridgeEnabled) { RaisePeek(new Peek("Ligue a ponte do iPhone", "Aba iPhone → Ponte ligada", "iphone")); return; }
        _ = Task.Run(() =>
        {
            var added = files.Select(f =>
            {
                try { return _outbox.AddFile(f, DateTime.Now, files.Count == 1 ? name : null); }
                catch (Exception ex) { Log.Warning(ex, "Mandar pro iPhone: não copiou {Arquivo}", f); return null; }
            }).OfType<OutboxItem>().ToList();
            Application.Current?.Dispatcher.BeginInvoke(() => AnnounceToIPhone(added, files.Count));
        });
    }

    public enum ClipToIPhone { Files, Image, Text, Private, Empty }

    // O que mandar do clipboard: arquivos copiados no Explorer vão como os originais; senha copiada
    // de gerenciador (privado) nunca sai do PC.
    public static ClipToIPhone DecideClip(string[]? files, bool isPrivate, bool hasImage, string? text) =>
        isPrivate ? ClipToIPhone.Private
        : files is { Length: > 0 } ? ClipToIPhone.Files
        : hasImage ? ClipToIPhone.Image
        : !string.IsNullOrWhiteSpace(text) ? ClipToIPhone.Text
        : ClipToIPhone.Empty;

    // Ctrl+Alt+I / bandeja: manda o que estiver no clipboard agora.
    public void SendClipboardToIPhone()
    {
        string[]? files = null;
        try { if (System.Windows.Clipboard.ContainsFileDropList()) files = SendToIPhone.FilesFrom(System.Windows.Clipboard.GetFileDropList().Cast<string>()); }
        catch (Exception ex) { Log.Warning(ex, "Mandar clipboard: não leu os arquivos"); }
        var snap = files is { Length: > 0 } ? null : ClipboardReader.Read();
        switch (DecideClip(files, snap?.IsPrivate == true, snap?.Image is not null, snap?.Text))
        {
            case ClipToIPhone.Files:
                SendFilesToIPhone(files!);
                break;
            case ClipToIPhone.Image:
                var png = Path.Combine(Path.GetTempPath(), $"orbit-clip-{Guid.NewGuid():N}.png");
                try
                {
                    var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
                    enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(snap!.Image!));
                    using (var fs = File.Create(png)) enc.Save(fs);
                    SendFilesToIPhone(new[] { png }, $"imagem-{DateTime.Now:yyyyMMdd-HHmmss}.png");
                }
                catch (Exception ex) { Log.Warning(ex, "Mandar clipboard: não salvou a imagem"); }
                break;
            case ClipToIPhone.Text:
                SendTextToIPhone(snap!.Text!);
                break;
            case ClipToIPhone.Private:
                RaisePeek(new Peek("Não mandei pro iPhone", "Conteúdo privado (senha?) fica só no PC", "clipboard"));
                break;
            default:
                RaisePeek(new Peek("Nada pra mandar", "Selecione algo e aperte Ctrl+Alt+I", "clipboard"));
                break;
        }
    }

    public void SendTextToIPhone(string text)
    {
        if (!_settings.BridgeEnabled) { RaisePeek(new Peek("Ligue a ponte do iPhone", "Aba iPhone → Ponte ligada", "iphone")); return; }
        var item = _outbox.AddText(text, DateTime.Now);
        AnnounceToIPhone(item is null ? new() : new() { item }, 1);
    }

    private void AnnounceToIPhone(List<OutboxItem> added, int asked)
    {
        if (added.Count == 0) { RaisePeek(new Peek("Não deu pra mandar", "Arquivo sumiu ou passa de 2 GB", "iphone")); return; }
        var (title, body) = OutboxAnnouncement(added);
        RaisePeek(new Peek("Mandado pro iPhone", body + (added.Count < asked ? $" ({asked - added.Count} falharam)" : ""), "iphone"));
        _ = _push.SendAsync(title, body, ValidTokenNames(), url: OutboxPushUrl(added));
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
