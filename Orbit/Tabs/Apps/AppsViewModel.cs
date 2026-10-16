using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Orbit.Models;
using Orbit.Services;
using Serilog;

namespace Orbit.Tabs.Apps;

public sealed partial class AppTile : ObservableObject
{
    public AppTile(AppEntry entry, bool favorite)
    {
        Entry = entry;
        _isFavorite = favorite;
    }

    public AppEntry Entry { get; }
    public string Name => Entry.Name;

    [ObservableProperty] private string? _iconPath;
    [ObservableProperty] private bool _isFavorite;
}

// Aba Apps: favoritos fixados + busca em todos os apps. A lista e os ícones vêm do shell numa
// thread STA (Sta.Run); ícones ficam em cache em disco. Só trabalha com a aba aberta.
public sealed partial class AppsViewModel : TabViewModel
{
    private static readonly TimeSpan CatalogMaxAge = TimeSpan.FromMinutes(10);
    private const int MaxResults = 6; // é o que cabe na altura do painel sem rolagem

    private readonly OrbitSettings _settings;
    private readonly string _iconDir;
    private readonly Action _collapse;
    private List<AppEntry> _all = new();
    private DateTime? _loadedAt;

    public override string Id => "apps";
    public override string Title => "Apps";

    public ObservableCollection<AppTile> Favorites { get; } = new();
    public ObservableCollection<AppTile> Results { get; } = new();

    [ObservableProperty] private string _query = "";
    [ObservableProperty] private bool _loading;

    public bool HasQuery => Query.Trim().Length > 0;
    public bool NoFavorites => Favorites.Count == 0 && !Loading;

    public AppsViewModel(OrbitSettings settings, string iconDir, Action collapse)
    {
        _settings = settings;
        _iconDir = iconDir;
        _collapse = collapse;
    }

    public override void SetActive(bool active)
    {
        if (!active)
        {
            Query = "";
            return;
        }
        if (Freshness.IsStale(_loadedAt, CatalogMaxAge, DateTime.Now)) _ = LoadCatalogAsync();
        else BuildFavorites();
    }

    private async Task LoadCatalogAsync()
    {
        Loading = _all.Count == 0; // recarga periódica não cobre a grade de favoritos
        OnPropertyChanged(nameof(NoFavorites));
        try
        {
            _all = await Sta.Run(AppCatalog.Load);
            _loadedAt = DateTime.Now;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Apps: não consegui listar os apps");
        }
        finally
        {
            Loading = false;
        }
        BuildFavorites();
        BuildResults();
    }

    partial void OnQueryChanged(string value)
    {
        OnPropertyChanged(nameof(HasQuery));
        BuildResults();
    }

    private void BuildFavorites()
    {
        Favorites.Clear();
        foreach (var id in _settings.FavoriteApps)
            if (_all.FirstOrDefault(a => a.Id == id) is { } app)
                Favorites.Add(new AppTile(app, favorite: true));
        OnPropertyChanged(nameof(NoFavorites));
        _ = LoadIconsAsync(Favorites.ToList());
    }

    private void BuildResults()
    {
        Results.Clear();
        foreach (var app in AppSearch.Search(_all, Query, MaxResults))
            Results.Add(new AppTile(app, _settings.FavoriteApps.Contains(app.Id)));
        _ = LoadIconsAsync(Results.ToList());
    }

    // ponytail: extrai só os visíveis (≤ 12 favoritos ou 8 resultados); o cache em disco deixa
    // a 2ª vez instantânea. Uma busca nova antes de terminar só desperdiça a extração antiga.
    private async Task LoadIconsAsync(List<AppTile> tiles)
    {
        if (tiles.Count == 0) return;
        try
        {
            var dir = _iconDir;
            var paths = await Sta.Run(() => tiles.Select(t => AppIcons.GetOrCreate(t.Entry.Id, dir)).ToList());
            for (int i = 0; i < tiles.Count; i++) tiles[i].IconPath = paths[i];
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Apps: falha ao carregar ícones");
        }
    }

    [RelayCommand]
    private void Launch(AppTile? tile)
    {
        tile ??= Results.FirstOrDefault();
        if (tile is null) return;
        try
        {
            // Shell execute do próprio caminho: ids com espaço ("Epson Scan 2") não quebram o argumento.
            Process.Start(new ProcessStartInfo("shell:AppsFolder\\" + tile.Entry.Id) { UseShellExecute = true });
            _collapse();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Apps: não abri {App}", tile.Name);
        }
    }

    [RelayCommand]
    private void ToggleFavorite(AppTile tile)
    {
        _settings.FavoriteApps = AppSearch.ToggleFavorite(_settings.FavoriteApps, tile.Entry.Id);
        SettingsStore.Save(_settings);
        tile.IsFavorite = _settings.FavoriteApps.Contains(tile.Entry.Id);
        foreach (var r in Results.Where(r => r.Entry.Id == tile.Entry.Id)) r.IsFavorite = tile.IsFavorite;
        BuildFavorites();
    }
}
