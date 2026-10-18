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
using Orbit.Tabs.Hoje;

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

// Um monitor onde a ilha pode morar. Device null = "sempre o principal".
public sealed partial class MonitorOption : ObservableObject
{
    private readonly Action<MonitorOption> _picked;

    public MonitorOption(string? device, string name, bool selected, Action<MonitorOption> picked)
    {
        Device = device;
        Name = name;
        _picked = picked;
        _selected = selected; // campo direto: não dispara _picked na construção
    }

    public string? Device { get; }
    public string Name { get; }

    [ObservableProperty] private bool _selected;

    partial void OnSelectedChanged(bool value) => _picked(this);
}

public sealed partial class AjustesViewModel : TabViewModel
{
    private readonly OrbitSettings _settings;
    private readonly PomodoroViewModel _pomodoro;
    private readonly Func<IEnumerable<TabViewModel>> _allTabs;
    private readonly Action _tabsChanged;
    private readonly Action _monitorChanged;
    private readonly Func<System.Threading.Tasks.Task<UpdateStatus>> _checkUpdate;
    private readonly Func<System.Threading.Tasks.Task> _updateNow;

    [ObservableProperty] private string _versionText = "Verificando versão…";
    [ObservableProperty] private bool _canUpdate;

    public override string Id => "ajustes";
    public override string Title => "Ajustes";

    public ObservableCollection<AccentOption> Opcoes { get; }
    public ObservableCollection<TabToggle> Abas { get; } = new();
    public ObservableCollection<MonitorOption> Monitores { get; } = new();

    [ObservableProperty] private bool _alertasComoToast;
    [ObservableProperty] private bool _somClaude;

    public AjustesViewModel(OrbitSettings settings, PomodoroViewModel pomodoro, Func<IEnumerable<TabViewModel>> allTabs, Action tabsChanged, Action monitorChanged,
        Func<System.Threading.Tasks.Task<UpdateStatus>> checkUpdate, Func<System.Threading.Tasks.Task> updateNow)
    {
        _settings = settings;
        _pomodoro = pomodoro;
        _allTabs = allTabs;
        _tabsChanged = tabsChanged;
        _monitorChanged = monitorChanged;
        _checkUpdate = checkUpdate;
        _updateNow = updateNow;
        Opcoes = new ObservableCollection<AccentOption>(new[] { SystemOption() }
            .Concat(Accent.Paleta.Select(p => new AccentOption(p.Nome, p.Hex, new SolidColorBrush(Accent.ParseHex(p.Hex)!.Value)))));
        // A amostra "Windows" acompanha a cor do sistema (antes ficava congelada na abertura).
        Accent.Changed += () => Opcoes[0] = SystemOption();
        AlertasComoToast = settings.AlertsAsToastInFullscreen;
        FocoTexto = settings.PomodoroFocusMinutes.ToString();
        PausaTexto = settings.PomodoroShortBreakMinutes.ToString();
        PausaLongaTexto = settings.PomodoroLongBreakMinutes.ToString();
        CiclosTexto = settings.PomodoroCyclesUntilLongBreak.ToString();
        SomPomodoro = settings.PomodoroSoundEnabled;
        SomClaude = settings.ClaudeSoundEnabled;
    }

    // Campos de texto (gravam ao perder o foco). Valor inválido volta ao anterior.
    [ObservableProperty] private string _focoTexto = "";
    [ObservableProperty] private string _pausaTexto = "";
    [ObservableProperty] private string _pausaLongaTexto = "";
    [ObservableProperty] private string _ciclosTexto = "";
    [ObservableProperty] private bool _somPomodoro;

    partial void OnFocoTextoChanged(string value) =>
        FocoTexto = SavePomodoro(value, _settings.PomodoroFocusMinutes, 180, v => _settings.PomodoroFocusMinutes = v);
    partial void OnPausaTextoChanged(string value) =>
        PausaTexto = SavePomodoro(value, _settings.PomodoroShortBreakMinutes, 180, v => _settings.PomodoroShortBreakMinutes = v);
    partial void OnPausaLongaTextoChanged(string value) =>
        PausaLongaTexto = SavePomodoro(value, _settings.PomodoroLongBreakMinutes, 180, v => _settings.PomodoroLongBreakMinutes = v);
    partial void OnCiclosTextoChanged(string value) =>
        CiclosTexto = SavePomodoro(value, _settings.PomodoroCyclesUntilLongBreak, 12, v => _settings.PomodoroCyclesUntilLongBreak = v);

    partial void OnSomPomodoroChanged(bool value)
    {
        if (_settings.PomodoroSoundEnabled == value) return;
        _settings.PomodoroSoundEnabled = value;
        SettingsStore.Save(_settings);
    }

    // Devolve o texto normalizado (o valor gravado). Reatribuir o mesmo texto não re-dispara.
    private string SavePomodoro(string text, int current, int max, Action<int> set)
    {
        int v = ParseRange(text, current, 1, max);
        if (v != current)
        {
            set(v);
            SettingsStore.Save(_settings);
            _pomodoro.UpdateConfig(_settings.PomodoroFocusMinutes, _settings.PomodoroShortBreakMinutes,
                _settings.PomodoroLongBreakMinutes, _settings.PomodoroCyclesUntilLongBreak);
        }
        return v.ToString();
    }

    public static int ParseRange(string? text, int fallback, int min, int max) =>
        int.TryParse(text, out var v) && v >= min && v <= max ? v : fallback;

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
        BuildMonitores();
        _ = RefreshVersionAsync();
    }

    private async System.Threading.Tasks.Task RefreshVersionAsync()
    {
        try
        {
            var s = await _checkUpdate();
            VersionText = SelfUpdate.Describe(s);
            CanUpdate = s.CanUpdate;
        }
        catch (Exception ex) { VersionText = "Não consegui verificar: " + ex.Message; }
    }

    [RelayCommand]
    private System.Threading.Tasks.Task AtualizarAgora() => _updateNow();

    // Lida a cada abertura: monitores entram e saem. O salvo desconectado continua na lista
    // (marcado), pra mostrar que a ilha volta pra ele quando reconectar.
    private void BuildMonitores()
    {
        Monitores.Clear();
        var saved = _settings.IslandMonitor;
        Monitores.Add(new MonitorOption(null, "Sempre o principal", saved is null, OnMonitorPicked));
        // Numerados: 1 = principal, 2… = os outros.
        var screens = System.Windows.Forms.Screen.AllScreens.OrderByDescending(s => s.Primary).ToArray();
        for (int i = 0; i < screens.Length; i++)
            Monitores.Add(new MonitorOption(screens[i].DeviceName, MonitorName(i + 1, screens[i].Bounds.Width, screens[i].Bounds.Height),
                string.Equals(screens[i].DeviceName, saved, StringComparison.OrdinalIgnoreCase), OnMonitorPicked));
        if (saved is not null && !screens.Any(s => string.Equals(s.DeviceName, saved, StringComparison.OrdinalIgnoreCase)))
            Monitores.Add(new MonitorOption(saved, "Monitor salvo (desconectado)", true, OnMonitorPicked));
    }

    public static string MonitorName(int number, int width, int height) => $"{number} · {width}×{height}";

    // Chips funcionam como rádio: clicar no marcado não desmarca. A lista é refeita depois do
    // clique (não dentro dele: mexer na coleção durante o binding do CheckBox dá erro).
    private void OnMonitorPicked(MonitorOption option)
    {
        if (option.Selected && option.Device != _settings.IslandMonitor)
        {
            _settings.IslandMonitor = option.Device;
            SettingsStore.Save(_settings);
            _monitorChanged();
        }
        System.Windows.Application.Current.Dispatcher.BeginInvoke(BuildMonitores);
    }

    private void OnTabToggled(TabToggle toggle)
    {
        _settings.HiddenTabs.Remove(toggle.Id);
        if (!toggle.Visible) _settings.HiddenTabs.Add(toggle.Id);
        SettingsStore.Save(_settings);
        _tabsChanged();
    }

    partial void OnSomClaudeChanged(bool value)
    {
        if (_settings.ClaudeSoundEnabled == value) return;
        _settings.ClaudeSoundEnabled = value;
        SettingsStore.Save(_settings);
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
