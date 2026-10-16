using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Orbit.Services;
using Serilog;
using Windows.Devices.Radios;

namespace Orbit.Tabs.Controles;

public sealed partial class ControlesViewModel : TabViewModel
{
    private Volume? _volume;
    private bool _syncing; // evita eco: evento do Windows → slider → SetPercent
    private bool _active;
    private AudioMixer? _mixer;
    private readonly DispatcherTimer _meter = new() { Interval = TimeSpan.FromMilliseconds(125) }; // só com o mixer aberto
    private int _meterTicks;

    public override string Id => "controles";
    public override string Title => "Controles";

    public NowPlayingViewModel NowPlaying { get; }

    [ObservableProperty] private bool? _wifiOn;      // null = indisponível
    [ObservableProperty] private bool? _bluetoothOn;
    [ObservableProperty] private bool _darkOn;
    [ObservableProperty] private double _volumePercent;
    [ObservableProperty] private bool _muted;
    [ObservableProperty] private bool _hasAudio;
    [ObservableProperty] private bool _showMixer;
    [ObservableProperty] private string? _mixerStatus;

    public ObservableCollection<AudioMixer.Output> Outputs { get; } = new();
    public ObservableCollection<MixerAppViewModel> Apps { get; } = new();

    public string WifiStatus => Status(WifiOn);
    public string BluetoothStatus => Status(BluetoothOn);
    public string DarkStatus => DarkOn ? "Ligado" : "Desligado";
    public string MuteGlyph => Muted ? "\uE74F" : "\uE767";
    public string MixerGlyph => ShowMixer ? "\uE711" : "\uE9E9"; // fechar / equalizador
    public bool HasApps => Apps.Count > 0;

    public ControlesViewModel(NowPlayingViewModel nowPlaying)
    {
        NowPlaying = nowPlaying;
        _meter.Tick += (_, _) => MeterTick();
        Apps.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasApps));
    }

    partial void OnWifiOnChanged(bool? value) => OnPropertyChanged(nameof(WifiStatus));
    partial void OnBluetoothOnChanged(bool? value) => OnPropertyChanged(nameof(BluetoothStatus));
    partial void OnDarkOnChanged(bool value) => OnPropertyChanged(nameof(DarkStatus));
    partial void OnMutedChanged(bool value) => OnPropertyChanged(nameof(MuteGlyph));

    partial void OnVolumePercentChanged(double value)
    {
        if (!_syncing) _volume?.SetPercent(value);
    }

    // Só com a aba visível: lê estados e assina o volume; ao sair, solta tudo.
    public override void SetActive(bool active)
    {
        _active = active;
        if (!active)
        {
            _volume?.Dispose();
            _volume = null;
            ShowMixer = false; // reabrir a aba volta pros controles; para o medidor
            return;
        }
        _ = RefreshRadiosAsync();
        DarkOn = DarkMode.IsDark();
        OpenVolume();
    }

    private void OpenVolume()
    {
        _volume?.Dispose();
        _volume = new Volume();
        HasAudio = _volume.Available;
        Sync(_volume.Percent, _volume.Muted);
        var v = _volume;
        _volume.Changed += (p, m) => Application.Current?.Dispatcher.BeginInvoke(() => { if (_volume == v) Sync(p, m); });
    }

    // --- Mixer: vive só aberto e com a aba visível ---

    [RelayCommand]
    private void ToggleMixer() => ShowMixer = !ShowMixer;

    partial void OnShowMixerChanged(bool value)
    {
        OnPropertyChanged(nameof(MixerGlyph));
        if (value && _active)
        {
            _mixer = new AudioMixer();
            MixerStatus = null;
            RefreshOutputs();
            RefreshApps();
            _meter.Start();
            return;
        }
        _meter.Stop();
        Apps.Clear();
        Outputs.Clear();
        _mixer?.Dispose();
        _mixer = null;
    }

    private void RefreshOutputs()
    {
        Outputs.Clear();
        foreach (var o in _mixer?.Outputs() ?? []) Outputs.Add(o);
    }

    // Mescla por chave: linhas existentes ficam (o slider não pula), novas entram, sumidas saem.
    private void RefreshApps()
    {
        if (_mixer is null) return;
        var fresh = _mixer.Refresh();
        for (int i = Apps.Count - 1; i >= 0; i--)
            if (fresh.All(a => a.Key != Apps[i].Key)) Apps.RemoveAt(i);
        for (int i = 0; i < fresh.Count; i++)
        {
            var row = Apps.FirstOrDefault(r => r.Key == fresh[i].Key);
            if (row is null) Apps.Insert(Math.Min(i, Apps.Count), new MixerAppViewModel(_mixer, fresh[i]));
            else row.Sync(fresh[i]);
        }
    }

    private void MeterTick()
    {
        if (_mixer is null) return;
        if (++_meterTicks % 16 == 0) RefreshApps(); // ~2 s: apps que abriram/fecharam, volume mexido fora
        foreach (var row in Apps)
        {
            var peak = row.Muted ? 0 : _mixer.Peak(row.Key);
            row.Level = Math.Max(peak, row.Level * 0.6); // cai suave em vez de piscar
        }
    }

    [RelayCommand]
    private void SetOutput(string id)
    {
        if (Outputs.FirstOrDefault(o => o.Id == id) is { IsDefault: true }) { RefreshOutputs(); return; }
        if (AudioMixer.SetDefaultOutput(id))
        {
            Log.Information("Mixer: saída trocada");
            MixerStatus = null;
            OpenVolume(); // o volume geral passa a ser o da saída nova
        }
        else
        {
            MixerStatus = "Não deu pra trocar por aqui — abri as Configurações de som.";
            OpenSettings("ms-settings:sound");
        }
        RefreshOutputs();
        RefreshApps();
    }

    private void Sync(double percent, bool muted)
    {
        _syncing = true;
        VolumePercent = percent;
        Muted = muted;
        _syncing = false;
    }

    private async Task RefreshRadiosAsync()
    {
        WifiOn = await Radios.IsOnAsync(RadioKind.WiFi);
        BluetoothOn = await Radios.IsOnAsync(RadioKind.Bluetooth);
    }

    [RelayCommand]
    private async Task ToggleWifi()
    {
        var r = await Radios.ToggleAsync(RadioKind.WiFi);
        if (r is null) OpenSettings("ms-settings:network-wifi");
        else WifiOn = r;
    }

    [RelayCommand]
    private async Task ToggleBluetooth()
    {
        var r = await Radios.ToggleAsync(RadioKind.Bluetooth);
        if (r is null) OpenSettings("ms-settings:bluetooth");
        else BluetoothOn = r;
    }

    [RelayCommand]
    private async Task ToggleDark()
    {
        bool target = !DarkOn;
        DarkOn = target;
        await Task.Run(() => DarkMode.Set(target));
    }

    [RelayCommand]
    private void ToggleMute() => _volume?.ToggleMute();

    public static string Status(bool? on) => on switch
    {
        true => "Ligado",
        false => "Desligado",
        null => "Indisponível",
    };

    private static void OpenSettings(string uri)
    {
        try { Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true }); }
        catch (Exception ex) { Log.Warning(ex, "Não abri {Uri}", uri); }
    }
}
