using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Orbit.Island;
using Orbit.Models;
using Orbit.Services;

namespace Orbit.Tabs.Ajustes;

// Hex null = "usar a cor do Windows".
public sealed record AccentOption(string Nome, string? Hex, Brush Amostra);

// Liga/desliga uma aba do painel.
public sealed partial class TabToggle : ObservableObject
{
    private readonly Action<TabToggle> _changed;

    public TabToggle(string id, string title, bool visible, Action<TabToggle> changed)
    {
        Id = id;
        Title = title;
        _changed = changed;
        _visible = visible; // campo direto: não dispara _changed na construção
    }

    public string Id { get; }
    public string Title { get; }

    [ObservableProperty] private bool _visible;

    partial void OnVisibleChanged(bool value) => _changed(this);
}

public sealed partial class AjustesViewModel : TabViewModel
{
    private readonly OrbitSettings _settings;
    private readonly Func<IEnumerable<TabViewModel>> _allTabs;
    private readonly Action _tabsChanged;

    public override string Id => "ajustes";
    public override string Title => "Ajustes";

    public ObservableCollection<AccentOption> Opcoes { get; }
    public ObservableCollection<TabToggle> Abas { get; } = new();

    [ObservableProperty] private bool _alertasComoToast;

    public AjustesViewModel(OrbitSettings settings, Func<IEnumerable<TabViewModel>> allTabs, Action tabsChanged)
    {
        _settings = settings;
        _allTabs = allTabs;
        _tabsChanged = tabsChanged;
        Opcoes = new ObservableCollection<AccentOption>(new[] { SystemOption() }
            .Concat(Accent.Paleta.Select(p => new AccentOption(p.Nome, p.Hex, new SolidColorBrush(Accent.ParseHex(p.Hex)!.Value)))));
        // A amostra "Windows" acompanha a cor do sistema (antes ficava congelada na abertura).
        Accent.Changed += () => Opcoes[0] = SystemOption();
        AlertasComoToast = settings.AlertsAsToastInFullscreen;
    }

    private static AccentOption SystemOption() => new("Windows", null, new SolidColorBrush(Accent.ReadSystem()));

    // Reconstrói a lista a cada abertura (cada fase registra abas novas).
    public override void SetActive(bool active)
    {
        if (!active) return;
        Abas.Clear();
        foreach (var t in _allTabs()
                     .Where(t => t.Id != "ajustes")
                     .OrderBy(t => Array.IndexOf(IslandViewModel.TabOrder, t.Id)))
            Abas.Add(new TabToggle(t.Id, t.Title, !_settings.HiddenTabs.Contains(t.Id), OnTabToggled));
    }

    private void OnTabToggled(TabToggle toggle)
    {
        _settings.HiddenTabs.Remove(toggle.Id);
        if (!toggle.Visible) _settings.HiddenTabs.Add(toggle.Id);
        SettingsStore.Save(_settings);
        _tabsChanged();
    }

    partial void OnAlertasComoToastChanged(bool value)
    {
        if (_settings.AlertsAsToastInFullscreen == value) return;
        _settings.AlertsAsToastInFullscreen = value;
        SettingsStore.Save(_settings);
    }

    [RelayCommand]
    private void EscolherAcento(AccentOption opcao)
    {
        _settings.AccentOverride = opcao.Hex;
        SettingsStore.Save(_settings);
        Accent.Refresh();
    }
}
