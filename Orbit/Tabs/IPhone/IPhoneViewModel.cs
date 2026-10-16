using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Orbit.Bridge;
using Orbit.Models;
using Orbit.Services;
using Serilog;

namespace Orbit.Tabs.IPhone;

public sealed record AuditRow(string When, string Action, string Result);

// Aba iPhone: liga/desliga a ponte, mostra status, endereço do Tailscale, pareamento (QR com
// token novo, exibido uma vez) e tokens ativos (revogar). O servidor só existe ligado.
public sealed partial class IPhoneViewModel : TabViewModel
{
    private readonly OrbitSettings _settings;
    private readonly TokenStore _tokens;
    private readonly AuditLog _audit;
    private readonly Func<BridgeHandlers> _handlers;
    private readonly string _webRoot;
    private bool _syncing; // muda Enabled por código sem disparar start/stop de novo

    public override string Id => "iphone";
    public override string Title => "iPhone";

    public BridgeServer Server { get; } = new();
    private readonly PairingCodes _codes = new();
    public ObservableCollection<TokenInfo> Tokens { get; } = new();
    public ObservableCollection<AuditRow> Activity { get; } = new();

    [ObservableProperty] private bool _enabled;
    [ObservableProperty] private string _status = "Desligada";
    [ObservableProperty] private string _publicUrl = "";
    [ObservableProperty] private BitmapSource? _qrImage;
    [ObservableProperty] private string? _pairLink;
    [ObservableProperty] private bool _hasPairing;
    [ObservableProperty] private string? _pairCode;      // "482 913"
    [ObservableProperty] private string? _pairCodeHint;  // "vale até 10:32"

    // Integração com o Tailscale instalado (via CLI): status, endereço automático, publicar.
    [ObservableProperty] private string _tailscaleStatus = "Verificando o Tailscale…";
    [ObservableProperty] private bool _isServing;
    [ObservableProperty] private bool _canPublish;
    [ObservableProperty] private string? _enableUrl;   // link do painel quando falta liberar HTTPS/Serve
    [ObservableProperty] private bool _tailscaleBusy;

    public bool ShowPublish => CanPublish && !IsServing && !TailscaleBusy;
    public bool ShowUnpublish => IsServing && !TailscaleBusy;
    partial void OnIsServingChanged(bool value) => NotifyPublishButtons();
    partial void OnCanPublishChanged(bool value) => NotifyPublishButtons();
    partial void OnTailscaleBusyChanged(bool value) => NotifyPublishButtons();
    private void NotifyPublishButtons()
    {
        OnPropertyChanged(nameof(ShowPublish));
        OnPropertyChanged(nameof(ShowUnpublish));
    }

    public bool HasQr => QrImage is not null;
    partial void OnQrImageChanged(BitmapSource? value) => OnPropertyChanged(nameof(HasQr));

    public IPhoneViewModel(OrbitSettings settings, TokenStore tokens, AuditLog audit, Func<BridgeHandlers> handlers, string webRoot)
    {
        _settings = settings;
        _tokens = tokens;
        _audit = audit;
        _handlers = handlers;
        _webRoot = webRoot;
        _syncing = true;
        Enabled = settings.BridgeEnabled;
        PublicUrl = settings.BridgePublicUrl;
        _syncing = false;
        _audit.Written += _ => Application.Current?.Dispatcher.BeginInvoke(RefreshActivity);
    }

    private Task _toggle = Task.CompletedTask; // liga/desliga em fila: cliques rápidos não sobrepõem Start/Stop

    // Chamado uma vez pela ilha no startup.
    public Task StartIfEnabledAsync() => _toggle = ApplyAsync(_toggle);

    private async Task ApplyAsync(Task previous)
    {
        try { await previous; } catch { /* a anterior já logou */ }
        if (Enabled == Server.Running) return;
        await (Enabled ? StartAsync() : StopAsync());
    }

    partial void OnEnabledChanged(bool value)
    {
        if (_syncing) return;
        _settings.BridgeEnabled = value;
        SettingsStore.Save(_settings);
        _toggle = ApplyAsync(_toggle);
    }

    partial void OnPublicUrlChanged(string value)
    {
        if (_syncing) return;
        var normalized = BridgeRules.NormalizeBaseUrl(value);
        if (_settings.BridgePublicUrl == normalized) return;
        _settings.BridgePublicUrl = normalized;
        SettingsStore.Save(_settings);
    }

    private async Task StartAsync()
    {
        try
        {
            Status = "Ligando…";
            var handlers = _handlers(); // na UI thread: captura o Dispatcher
            await Task.Run(() => Server.StartAsync(_settings.BridgePort, _webRoot, _tokens, _audit, handlers, _codes));
            Status = $"Ligada · 127.0.0.1:{Server.Port}";
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Ponte: não subiu na porta {Porta}", _settings.BridgePort);
            Status = $"Erro: a porta {_settings.BridgePort} está em uso ou bloqueada";
            _syncing = true;
            Enabled = false;
            _syncing = false;
            _settings.BridgeEnabled = false;
            SettingsStore.Save(_settings);
        }
    }

    private async Task StopAsync()
    {
        try { await Task.Run(() => Server.StopAsync()); }
        catch (Exception ex) { Log.Warning(ex, "Ponte: falha ao desligar"); }
        Status = "Desligada";
    }

    public override void SetActive(bool active)
    {
        if (active)
        {
            RefreshTokens();
            RefreshActivity();
            _ = RefreshTailscaleAsync();
            return;
        }
        // O token do pareamento aparece uma vez só: some ao sair da aba.
        QrImage = null;
        PairLink = null;
        PairCode = null;
        HasPairing = false;
        // O código continua valendo até expirar: dá pra fechar a ilha e digitar no iPhone com calma.
    }

    private async Task RefreshTailscaleAsync()
    {
        var exe = TailscaleCli.FindExe();
        if (exe is null)
        {
            ApplyTailscale(new TailscaleInfo(false, false, null, false), serving: false);
            return;
        }
        try
        {
            var status = await TailscaleCli.RunAsync(exe, new[] { "status", "--json" }, TimeSpan.FromSeconds(5));
            var info = TailscaleCli.ParseStatus(status.Stdout);
            bool serving = false;
            if (info.Running)
            {
                var serve = await TailscaleCli.RunAsync(exe, new[] { "serve", "status", "--json" }, TimeSpan.FromSeconds(5));
                serving = TailscaleCli.IsServing(serve.Stdout, _settings.BridgePort);
            }
            ApplyTailscale(info, serving);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Tailscale: falha ao consultar o status");
            TailscaleStatus = "Não consegui falar com o Tailscale.";
        }
    }

    private void ApplyTailscale(TailscaleInfo info, bool serving)
    {
        TailscaleStatus = TailscaleCli.Describe(info, serving);
        IsServing = serving;
        CanPublish = info.Running && info.HttpsReady;
        EnableUrl = info.Running && !info.HttpsReady ? TailscaleCli.AdminDnsUrl : null;
        // Endereço automático: só preenche se o usuário ainda não escreveu nada.
        if (info.DnsName is { } dns && string.IsNullOrWhiteSpace(PublicUrl)) PublicUrl = "https://" + dns;
    }

    // tailscale serve --bg --https=443 http://127.0.0.1:<porta> (tailnet só; nunca funnel).
    [RelayCommand]
    private async Task Publish()
    {
        if (TailscaleCli.FindExe() is not { } exe) return;
        if (!Enabled) Enabled = true; // publicar sem a ponte ligada não serve pra nada
        TailscaleBusy = true;
        TailscaleStatus = "Publicando…";
        try
        {
            var r = await TailscaleCli.RunAsync(exe, TailscaleCli.ServeArgs(_settings.BridgePort), TimeSpan.FromSeconds(20));
            if (r.TimedOut || r.ExitCode != 0)
            {
                Log.Warning("Tailscale serve falhou ({Codigo}): {Saida}", r.ExitCode, r.All);
                if (TailscaleCli.ExtractEnableUrl(r.All) is { } url)
                {
                    EnableUrl = url;
                    TailscaleStatus = "Libere o Serve no painel do Tailscale (botão ao lado) e publique de novo.";
                    return;
                }
                TailscaleStatus = "Falhou: " + FirstLine(r.All);
                return;
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Tailscale: falha ao publicar");
            TailscaleStatus = "Falhou ao chamar o Tailscale.";
            return;
        }
        finally
        {
            TailscaleBusy = false;
        }
        await RefreshTailscaleAsync();
    }

    [RelayCommand]
    private async Task Unpublish()
    {
        if (TailscaleCli.FindExe() is not { } exe) return;
        TailscaleBusy = true;
        try
        {
            var r = await TailscaleCli.RunAsync(exe, TailscaleCli.ServeOffArgs(), TimeSpan.FromSeconds(10));
            if (r.ExitCode != 0) Log.Warning("Tailscale serve off falhou ({Codigo}): {Saida}", r.ExitCode, r.All);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Tailscale: falha ao despublicar");
        }
        finally
        {
            TailscaleBusy = false;
        }
        await RefreshTailscaleAsync();
    }

    public string UploadFolder => BridgeRules.UploadFolder(_settings.BridgeUploadFolder);

    [RelayCommand]
    private void OpenUploadFolder()
    {
        try
        {
            System.IO.Directory.CreateDirectory(UploadFolder);
            Process.Start(new ProcessStartInfo(UploadFolder) { UseShellExecute = true });
        }
        catch (Exception ex) { Log.Warning(ex, "Não abri {Pasta}", UploadFolder); }
    }

    [RelayCommand]
    private void OpenEnableUrl()
    {
        if (EnableUrl is null) return;
        try { Process.Start(new ProcessStartInfo(EnableUrl) { UseShellExecute = true }); }
        catch (Exception ex) { Log.Warning(ex, "Não abri {Url}", EnableUrl); }
    }

    private static string FirstLine(string text)
    {
        var line = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? "sem detalhes";
        return line.Length > 90 ? line[..90] + "…" : line;
    }

    [RelayCommand]
    private void Pair()
    {
        var token = _tokens.Create($"iPhone {DateTime.Now:dd/MM HH:mm}");
        var baseUrl = BridgeRules.NormalizeBaseUrl(PublicUrl);
        PairLink = baseUrl.Length > 0
            ? BridgeRules.PairingLink(baseUrl, token)
            : token; // sem endereço: mostra só o token (pros Atalhos)
        QrImage = baseUrl.Length > 0 ? TryRenderQr(PairLink) : null;
        var now = DateTime.Now;
        PairCode = PairingCodes.Format(_codes.Issue(token, now));
        PairCodeHint = $"Digite no app Orbit do iPhone · vale até {now + PairingCodes.Lifetime:HH:mm}";
        HasPairing = true;
        RefreshTokens();
    }

    [RelayCommand]
    private void CopyPairLink()
    {
        if (PairLink is not null) ClipboardReader.SetText(PairLink);
    }

    [RelayCommand]
    private void Revoke(TokenInfo token)
    {
        try
        {
            _tokens.Revoke(token.Hash);
        }
        catch (Exception ex)
        {
            // Revoke não falha em silêncio (senão o token voltaria a valer ao reiniciar).
            Log.Warning(ex, "Ponte: não consegui revogar {Token}", token.Name);
            Status = "Não consegui revogar (tokens.json em uso). Tente de novo.";
        }
        RefreshTokens();
    }

    private void RefreshTokens()
    {
        Tokens.Clear();
        foreach (var t in _tokens.List().OrderByDescending(t => t.CreatedAt)) Tokens.Add(t);
    }

    private void RefreshActivity()
    {
        Activity.Clear();
        foreach (var e in _audit.RecentEntries().Take(8))
            Activity.Add(new AuditRow(e.When.ToString("HH:mm"), e.Action, e.Result));
    }

    private static BitmapSource? TryRenderQr(string text)
    {
        try
        {
            var png = Qr.RenderPng(text);
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.StreamSource = new MemoryStream(png);
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Ponte: QR indisponível; mostrando só o link");
            return null;
        }
    }
}
