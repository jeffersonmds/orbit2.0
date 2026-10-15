using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.Input;
using Orbit.Models;
using Orbit.Services;
using Serilog;

namespace Orbit.Tabs.Noticias;

public sealed record NewsRow(NewsItem Item, string Domain);

// Portado do NewsTileViewModel do 1.0: busca a cada 5 min só com a aba aberta (portão de frescor).
public sealed partial class NoticiasViewModel : TabViewModel
{
    private static readonly TimeSpan Every = TimeSpan.FromMinutes(5);
    private const int Pool = 8;         // quantos do topo buscar
    private const int Show = 6;         // quantos exibir
    private const int DismissCap = 200; // teto da lista de ignorados

    private readonly ApiService _api;
    private readonly OrbitSettings _settings;
    private readonly DispatcherTimer _timer = new() { Interval = Every };
    private DateTime? _loadedAt;

    public override string Id => "noticias";
    public override string Title => "Notícias";

    public ObservableCollection<NewsRow> Items { get; } = new();

    public NoticiasViewModel(ApiService api, OrbitSettings settings)
    {
        _api = api;
        _settings = settings;
        _timer.Tick += async (_, _) => await RefreshAsync();
    }

    public override void SetActive(bool active)
    {
        if (!active)
        {
            _timer.Stop();
            return;
        }
        if (Freshness.IsStale(_loadedAt, Every, DateTime.Now)) _ = RefreshAsync();
        _timer.Start();
    }

    private async Task RefreshAsync()
    {
        var items = await _api.GetTopNewsAsync(Pool);
        if (items is null) return; // falha de rede: mantém o que está na tela
        var dismissed = new HashSet<int>(_settings.DismissedNewsIds);
        Items.Clear();
        foreach (var it in SelectVisible(items, dismissed, Show)) Items.Add(new NewsRow(it, Domain(it.Url)));
        _loadedAt = DateTime.Now;
    }

    [RelayCommand]
    private void Dismiss(NewsRow row)
    {
        Items.Remove(row); // some sem refil até a próxima busca
        _settings.DismissedNewsIds = Prune(_settings.DismissedNewsIds, row.Item.Id, DismissCap);
        SettingsStore.Save(_settings);
    }

    [RelayCommand]
    private void Open(NewsRow row)
    {
        try { Process.Start(new ProcessStartInfo(row.Item.Url) { UseShellExecute = true }); }
        catch (Exception ex) { Log.Warning(ex, "Não abri {Url}", row.Item.Url); }
    }

    // --- Lógica pura testável (portada do 1.0) ---

    public static List<NewsItem> SelectVisible(IReadOnlyList<NewsItem> pool, ISet<int> dismissed, int max) =>
        pool.Where(i => !dismissed.Contains(i.Id)).Take(max).ToList();

    // Adiciona `add` e mantém só os `cap` maiores (ids do HN só crescem).
    public static List<int> Prune(List<int> ids, int add, int cap)
    {
        var set = new HashSet<int>(ids) { add };
        return set.OrderByDescending(x => x).Take(cap).ToList();
    }

    public static string Domain(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u)
            ? (u.Host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? u.Host[4..] : u.Host)
            : "";
}
