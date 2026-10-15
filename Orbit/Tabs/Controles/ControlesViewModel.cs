using System;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Windows;
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

    public override string Id => "controles";
    public override string Title => "Controles";

    public NowPlayingViewModel NowPlaying { get; }

    [ObservableProperty] private bool? _wifiOn;      // null = indisponível
    [ObservableProperty] private bool? _bluetoothOn;
    [ObservableProperty] private bool _darkOn;
    [ObservableProperty] private double _volumePercent;
    [ObservableProperty] private bool _muted;
    [ObservableProperty] private bool _hasAudio;

    public string WifiStatus => Status(WifiOn);
    public string BluetoothStatus => Status(BluetoothOn);
    public string DarkStatus => DarkOn ? "Ligado" : "Desligado";
    public string MuteGlyph => Muted ? "\uE74F" : "\uE767";

    public ControlesViewModel(NowPlayingViewModel nowPlaying) => NowPlaying = nowPlaying;

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
        if (!active)
        {
            _volume?.Dispose();
            _volume = null;
            return;
        }
        _ = RefreshRadiosAsync();
        DarkOn = DarkMode.IsDark();
        _volume = new Volume();
        HasAudio = _volume.Available;
        Sync(_volume.Percent, _volume.Muted);
        _volume.Changed += (p, m) => Application.Current?.Dispatcher.BeginInvoke(() => Sync(p, m));
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
