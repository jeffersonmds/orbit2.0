using System;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using Orbit.Models;
using Orbit.Services;
using Orbit.Tabs;
using Orbit.Tabs.Ajustes;
using Orbit.Tabs.Energia;
using Serilog;

namespace Orbit.Island;

// Orquestra a máquina de estados e as abas. A janela só observa Mode/Peek/ActivityText.
public sealed partial class IslandViewModel : ObservableObject
{
    private readonly IslandMachine _machine = new();
    private readonly OrbitSettings _settings;

    public ObservableCollection<TabViewModel> Tabs { get; }

    [ObservableProperty] private TabViewModel? _selectedTab;
    [ObservableProperty] private IslandMode _mode = IslandMode.Repouso;
    [ObservableProperty] private string _activityText = "";
    [ObservableProperty] private Peek? _peek;

    public IslandViewModel(OrbitSettings settings)
    {
        _settings = settings;
        // Sem DI: cada aba é criada aqui na mão (fases 2-4 acrescentam as outras).
        Tabs = new ObservableCollection<TabViewModel>
        {
            new EnergiaViewModel(),
            new AjustesViewModel(settings),
        };
        SelectedTab = Tabs.FirstOrDefault(t => t.Id == settings.LastTab) ?? Tabs[0];
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
        _machine.PeekDone();
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
