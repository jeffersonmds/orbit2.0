using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Orbit.Models;
using Orbit.Services;

namespace Orbit.Tabs.Mercado;

// Opção do catálogo no modo edição.
public sealed partial class MarketOption : ObservableObject
{
    public MarketOption(MarketCatalogItem item, bool selected)
    {
        Id = item.Id;
        Name = item.Name;
        Glyph = item.Glyph;
        GlyphColor = new SolidColorBrush((Color)ColorConverter.ConvertFromString(item.ColorHex));
        Kind = item.Kind;
        _selected = selected;
    }

    public string Id { get; }
    public string Name { get; }
    public string Glyph { get; }
    public Brush GlyphColor { get; }
    public MarketKind Kind { get; }

    [ObservableProperty] private bool _selected;
}

// Portado dos tiles Crypto/Exchange do 1.0, juntos numa aba. Preços a cada 60 s (o 1.0
// fazia cripto a cada 30 s; 60 s é mais gentil com o limite do CoinGecko) e sparklines a
// cada 5 min — ambos só com a aba aberta e com portão de frescor.
public sealed partial class MercadoViewModel : TabViewModel
{
    private static readonly TimeSpan PricesEvery = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan SparksEvery = TimeSpan.FromMinutes(5);

    private readonly ApiService _api;
    private readonly OrbitSettings _settings;
    private readonly DispatcherTimer _prices = new() { Interval = PricesEvery };
    private readonly DispatcherTimer _sparks = new() { Interval = SparksEvery };
    private DateTime? _pricesAt, _sparksAt;

    public override string Id => "mercado";
    public override string Title => "Mercado";

    public ObservableCollection<MarketRow> Rows { get; } = new();
    public ObservableCollection<MarketOption> CryptoOptions { get; } = new();
    public ObservableCollection<MarketOption> CurrencyOptions { get; } = new();

    [ObservableProperty] private bool _isEditing;

    public MercadoViewModel(ApiService api, OrbitSettings settings)
    {
        _api = api;
        _settings = settings;
        _prices.Tick += async (_, _) => await RefreshPricesAsync();
        _sparks.Tick += async (_, _) => await RefreshSparksAsync();
        BuildRows();
    }

    public override void SetActive(bool active)
    {
        if (!active)
        {
            _prices.Stop();
            _sparks.Stop();
            IsEditing = false;
            return;
        }
        var now = DateTime.Now;
        if (Freshness.IsStale(_pricesAt, PricesEvery, now)) _ = RefreshPricesAsync();
        if (Freshness.IsStale(_sparksAt, SparksEvery, now)) _ = RefreshSparksAsync();
        _prices.Start();
        _sparks.Start();
    }

    private void BuildRows()
    {
        Rows.Clear();
        foreach (var item in _settings.MarketCryptos.Select(MarketCatalog.FindCrypto).OfType<MarketCatalogItem>())
            Rows.Add(new MarketRow(item.Id, item.Glyph, item.ColorHex, item.Name, MarketKind.Crypto));
        foreach (var item in _settings.MarketCurrencies.Select(MarketCatalog.FindCurrency).OfType<MarketCatalogItem>())
            Rows.Add(new MarketRow(item.Id, item.Glyph, item.ColorHex, item.Name, MarketKind.Currency));
    }

    private async Task RefreshPricesAsync()
    {
        var cryptoRows = Rows.Where(r => r.Kind == MarketKind.Crypto).ToList();
        var currencyRows = Rows.Where(r => r.Kind == MarketKind.Currency).ToList();
        var crypto = await _api.GetCryptoPricesAsync(cryptoRows.Select(r => r.Id));
        var fx = await _api.GetExchangePricesAsync(currencyRows.Select(r => r.Id));
        if (crypto is not null)
            foreach (var row in cryptoRows)
                if (crypto.TryGetValue(row.Id, out var p))
                {
                    row.Price = FormatUsd(p.Price);
                    (row.Change, row.ChangeColor) = ChangeIndicator.Format(p.Change);
                }
        if (fx is not null)
            foreach (var row in currencyRows)
                if (fx.TryGetValue(row.Id, out var p))
                {
                    row.Price = "R$ " + p.Price.ToString("0.00", CultureInfo.InvariantCulture);
                    (row.Change, row.ChangeColor) = ChangeIndicator.Format(p.Change, 2);
                }
        if (crypto is not null && fx is not null) _pricesAt = DateTime.Now;
    }

    // ponytail: 1 chamada por cripto; limitar ao top-N se bater no rate limit.
    private async Task RefreshSparksAsync()
    {
        bool any = false;
        foreach (var row in Rows.Where(r => r.Kind == MarketKind.Crypto).ToList())
        {
            var s = await _api.GetSparklineAsync(row.Id);
            if (s is null) continue; // falha (ex.: 429): mantém a linha anterior
            row.SetSparkline(s);
            any = true;
        }
        if (any) _sparksAt = DateTime.Now;
    }

    [RelayCommand]
    private void Edit()
    {
        Fill(CryptoOptions, MarketCatalog.Cryptos, _settings.MarketCryptos);
        Fill(CurrencyOptions, MarketCatalog.Currencies, _settings.MarketCurrencies);
        IsEditing = true;
    }

    [RelayCommand]
    private void CancelEdit() => IsEditing = false;

    [RelayCommand]
    private void SaveEdit()
    {
        _settings.MarketCryptos = CryptoOptions.Where(o => o.Selected).Select(o => o.Id).ToList();
        _settings.MarketCurrencies = CurrencyOptions.Where(o => o.Selected).Select(o => o.Id).ToList();
        SettingsStore.Save(_settings);
        BuildRows();
        _pricesAt = _sparksAt = null;
        _ = RefreshPricesAsync();
        _ = RefreshSparksAsync();
        IsEditing = false;
    }

    private static void Fill(ObservableCollection<MarketOption> target, IReadOnlyList<MarketCatalogItem> catalog, IReadOnlyList<string> saved)
    {
        target.Clear();
        var byId = catalog.ToDictionary(c => c.Id);
        foreach (var id in OrderCatalog(catalog.Select(c => c.Id).ToList(), saved))
            target.Add(new MarketOption(byId[id], saved.Contains(id)));
    }

    // --- Lógica pura testável (portada do 1.0) ---

    // Preço grande: sem decimais. Médio: 2 casas. Sub-dólar: 4 casas.
    public static string FormatUsd(double v) =>
        v >= 100 ? "$" + v.ToString("N0", CultureInfo.InvariantCulture)
        : v >= 1 ? "$" + v.ToString("0.00", CultureInfo.InvariantCulture)
                 : "$" + v.ToString("0.0000", CultureInfo.InvariantCulture);

    // Selecionados primeiro (na ordem salva), depois o resto na ordem do catálogo.
    public static List<string> OrderCatalog(IReadOnlyList<string> catalogIds, IReadOnlyList<string> savedIds)
    {
        var result = new List<string>();
        foreach (var id in savedIds) if (catalogIds.Contains(id)) result.Add(id);
        foreach (var id in catalogIds) if (!result.Contains(id)) result.Add(id);
        return result;
    }
}
