using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using Orbit.Models;
using Orbit.Services;
using Orbit.Tabs;
using Orbit.Tabs.Ajustes;
using Orbit.Tabs.Energia;
using Serilog;

namespace Orbit.Island;

// Orquestra a máquina de estados, as abas e os serviços de fundo que alimentam a pílula.
// A janela observa Mode/Peek/ActivityText.
public sealed partial class IslandViewModel : ObservableObject
{
    // Ordem fixa das abas no painel. Ids ainda não registrados são pulados.
    public static readonly string[] TabOrder =
        { "controles", "hoje", "stats", "mercado", "noticias", "claude", "energia", "ajustes" };

    private readonly IslandMachine _machine = new();
    private readonly OrbitSettings _settings;
    private readonly List<TabViewModel> _allTabs = new();

    public ObservableCollection<TabViewModel> Tabs { get; } = new();

    [ObservableProperty] private TabViewModel? _selectedTab;
    [ObservableProperty] private IslandMode _mode = IslandMode.Repouso;
    [ObservableProperty] private string _activityText = "";
    [ObservableProperty] private Peek? _peek;

    public IslandViewModel(OrbitSettings settings)
    {
        _settings = settings;
        // Sem DI: abas e serviços de fundo são criados aqui na mão.
        Register(new EnergiaViewModel());
        Register(new AjustesViewModel(settings, () => _allTabs, ApplyTabVisibility));
        // novas abas/serviços entram acima desta linha
        ApplyTabVisibility();
        SelectedTab = Tabs.FirstOrDefault(t => t.Id == settings.LastTab) ?? Tabs.FirstOrDefault();
    }

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
        if (ShouldToast(_machine.Fullscreen, _settings.AlertsAsToastInFullscreen))
        {
            ToastNotifier.Notify(peek.Title, peek.Detail ?? "", sound);
            return;
        }
        if (sound && !_machine.Fullscreen) SystemSounds.Asterisk.Play();
        RaisePeek(peek);
    }

    public static bool ShouldToast(bool fullscreen, bool preferToast) => fullscreen && preferToast;

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
