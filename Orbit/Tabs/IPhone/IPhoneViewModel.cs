using System;
using System.Collections.ObjectModel;
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
    public ObservableCollection<TokenInfo> Tokens { get; } = new();
    public ObservableCollection<AuditRow> Activity { get; } = new();

    [ObservableProperty] private bool _enabled;
    [ObservableProperty] private string _status = "Desligada";
    [ObservableProperty] private string _publicUrl = "";
    [ObservableProperty] private BitmapSource? _qrImage;
    [ObservableProperty] private string? _pairLink;
    [ObservableProperty] private bool _hasPairing;

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

    // Chamado uma vez pela ilha no startup.
    public Task StartIfEnabledAsync() => Enabled ? StartAsync() : Task.CompletedTask;

    partial void OnEnabledChanged(bool value)
    {
        if (_syncing) return;
        _settings.BridgeEnabled = value;
        SettingsStore.Save(_settings);
        _ = value ? StartAsync() : StopAsync();
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
            await Server.StartAsync(_settings.BridgePort, _webRoot, _tokens, _audit, _handlers());
            Status = $"Ligada · 127.0.0.1:{Server.Port}";
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Ponte: não subiu na porta {Porta}", _settings.BridgePort);
            Status = $"Erro: a porta {_settings.BridgePort} está em uso ou bloqueada";
            _syncing = true;
            Enabled = false;
            _syncing = false;
        }
    }

    private async Task StopAsync()
    {
        try { await Server.StopAsync(); }
        catch (Exception ex) { Log.Warning(ex, "Ponte: falha ao desligar"); }
        Status = "Desligada";
    }

    public override void SetActive(bool active)
    {
        if (active)
        {
            RefreshTokens();
            RefreshActivity();
            return;
        }
        // O token do pareamento aparece uma vez só: some ao sair da aba.
        QrImage = null;
        PairLink = null;
        HasPairing = false;
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
