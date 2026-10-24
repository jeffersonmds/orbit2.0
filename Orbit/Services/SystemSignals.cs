using System;
using System.Diagnostics;
using System.Linq;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using Serilog;

namespace Orbit.Services;

public enum SignalKind { Volume, AudioDevice, Power, CapsLock, Network }

// Um evento do sistema pra pílula. Level (0..1) desenha barra; Key = Kind (o novo substitui o anterior).
public sealed record SystemSignal(SignalKind Kind, string Title, string? Detail, double? Level = null);

// Eventos do sistema que a Dynamic Island mostra: volume, saída de som (fone ↔ caixa), carregador,
// Caps Lock e internet. Por evento do Windows sempre que existe; só o Caps Lock é conferido a cada
// 300 ms (avisar a troca exigiria um gancho global de teclado — coisa de keylogger, que antivírus odeiam).
// Nada dispara na abertura: o estado inicial é lido em silêncio.
public sealed class SystemSignals : IDisposable, IMMNotificationClient
{
    public event Action<SystemSignal>? Raised;

    private readonly MMDeviceEnumerator _enumerator = new();
    private MMDevice? _output;
    private bool? _plugged;
    private bool _caps;
    private string? _network; // "Wi-Fi Casa", "Cabo", null = sem internet
    private Timer? _capsTimer, _netDebounce;
    private bool _started;

    public void Start()
    {
        try { _enumerator.RegisterEndpointNotificationCallback(this); } catch (Exception ex) { Log.Warning(ex, "Sinais: sem aviso de troca de saída de som"); }
        HookOutput();
        _plugged = Battery.ReadFull().Charging;
        SystemEvents.PowerModeChanged += OnPower;
        _caps = CapsOn();
        _capsTimer = new Timer(_ => CheckCaps(), null, 300, 300);
        _network = NetworkName();
        NetworkChange.NetworkAvailabilityChanged += (_, _) => DebounceNetwork();
        NetworkChange.NetworkAddressChanged += (_, _) => DebounceNetwork();
        _started = true;
    }

    // --- Volume (da saída padrão; troca de saída reengancha) ---

    private void HookOutput()
    {
        try
        {
            if (_output is not null) { _output.AudioEndpointVolume.OnVolumeNotification -= OnVolume; _output.Dispose(); }
            _output = _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            _output.AudioEndpointVolume.OnVolumeNotification += OnVolume;
        }
        catch (Exception ex) { _output = null; Log.Warning(ex, "Sinais: sem saída de som"); }
    }

    private void OnVolume(AudioVolumeNotificationData d) =>
        Raised?.Invoke(VolumeSignal(d.MasterVolume, d.Muted));

    public static SystemSignal VolumeSignal(float level, bool muted) =>
        muted ? new(SignalKind.Volume, "Sem som", null, 0)
              : new(SignalKind.Volume, $"Volume · {Math.Round(level * 100)}%", null, Math.Clamp(level, 0, 1));

    // IMMNotificationClient: só a troca da saída padrão interessa (fone conectado vira o padrão, etc.)
    public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId)
    {
        if (flow != DataFlow.Render || role != Role.Multimedia || !_started) return;
        // aviso vem de thread do COM: o reengancho/leitura vai pro pool
        Task.Run(() =>
        {
            HookOutput();
            try { Raised?.Invoke(new SystemSignal(SignalKind.AudioDevice, "Som agora em", _output?.FriendlyName)); } catch { }
        });
    }
    public void OnDeviceStateChanged(string deviceId, DeviceState newState) { }
    public void OnDeviceAdded(string pwstrDeviceId) { }
    public void OnDeviceRemoved(string deviceId) { }
    public void OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key) { }

    // --- Carregador ---

    private void OnPower(object? sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode != PowerModes.StatusChange) return;
        var (percent, charging, secondsLeft) = Battery.ReadFull();
        if (percent is null || charging == _plugged) return;
        _plugged = charging;
        Raised?.Invoke(PowerSignal(percent.Value, charging, secondsLeft));
    }

    public static SystemSignal PowerSignal(int percent, bool charging, int? secondsLeft) =>
        charging
            ? new(SignalKind.Power, $"Carregando · {percent}%", null, percent / 100.0)
            : new(SignalKind.Power, $"Na bateria · {percent}%", secondsLeft is > 0 ? $"{Battery.FormatDuration(TimeSpan.FromSeconds(secondsLeft.Value))} restantes" : null, percent / 100.0);

    // --- Caps Lock ---

    private void CheckCaps()
    {
        var on = CapsOn();
        if (on == _caps) return;
        _caps = on;
        Raised?.Invoke(new SystemSignal(SignalKind.CapsLock, on ? "Caps Lock ligado" : "Caps Lock desligado", null));
    }

    private static bool CapsOn() => (GetKeyState(0x14) & 1) != 0;

    // --- Internet ---

    // Troca de rede dispara vários avisos seguidos (endereços indo e vindo): espera assentar 2 s.
    private void DebounceNetwork()
    {
        _netDebounce?.Dispose();
        _netDebounce = new Timer(_ =>
        {
            var now = NetworkName();
            if (now == _network) return;
            var before = _network;
            _network = now;
            Raised?.Invoke(now is null
                ? new SystemSignal(SignalKind.Network, "Sem internet", before is null ? null : $"Caiu: {before}")
                : new SystemSignal(SignalKind.Network, "Conectado", now));
        }, null, 2000, Timeout.Infinite);
    }

    // "Wi-Fi Casa" / "Cabo" / null sem rede. Interfaces virtuais (Tailscale, Hyper-V, VPN) não contam.
    private static string? NetworkName()
    {
        try
        {
            var up = NetworkInterface.GetAllNetworkInterfaces().Where(n => n.OperationalStatus == OperationalStatus.Up
                && n.NetworkInterfaceType is NetworkInterfaceType.Wireless80211 or NetworkInterfaceType.Ethernet or NetworkInterfaceType.GigabitEthernet
                && !Regex.IsMatch(n.Description, "virtual|hyper-v|tailscale|vpn|vmware|loopback|bluetooth", RegexOptions.IgnoreCase)
                && n.GetIPProperties().GatewayAddresses.Any(g => !g.Address.Equals(System.Net.IPAddress.Any))).ToList();
            if (up.Count == 0) return null;
            if (up.Any(n => n.NetworkInterfaceType == NetworkInterfaceType.Wireless80211))
                return WifiSsid() is { } ssid ? $"Wi-Fi {ssid}" : "Wi-Fi";
            return "Cabo";
        }
        catch { return null; }
    }

    // SSID da rede Wi-Fi atual (netsh; a linha "SSID" sem "BSSID").
    public static string? WifiSsid() => ParseSsid(Run("netsh", "wlan show interfaces"));

    public static string? ParseSsid(string netshInterfaces)
    {
        foreach (var raw in netshInterfaces.Split('\n'))
        {
            var line = raw.Trim();
            var m = Regex.Match(line, @"^SSID\s*:\s*(.+)$");
            if (m.Success) return m.Groups[1].Value.Trim();
        }
        return null;
    }

    internal static string Run(string file, string args)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(file, args)
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, StandardOutputEncoding = System.Text.Encoding.UTF8 })!;
            var output = p.StandardOutput.ReadToEnd();
            p.WaitForExit(3000);
            return output;
        }
        catch { return ""; }
    }

    public void Dispose()
    {
        _capsTimer?.Dispose();
        _netDebounce?.Dispose();
        SystemEvents.PowerModeChanged -= OnPower;
        try { _enumerator.UnregisterEndpointNotificationCallback(this); } catch { }
        _output?.Dispose();
        _enumerator.Dispose();
    }

    [DllImport("user32.dll")] private static extern short GetKeyState(int vKey);
}
