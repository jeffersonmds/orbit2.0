using System;
using CommunityToolkit.Mvvm.ComponentModel;
using Orbit.Bridge;

namespace Orbit.Tabs.Dispositivos;

// Uma linha da lista de dispositivos: nome, último acesso e as seis permissões.
// Mudar uma chave chama onChanged; o ViewModel grava no TokenStore.
public sealed partial class DeviceRow : ObservableObject
{
    private readonly Action<DeviceRow> _onChanged;
    private readonly Action<DeviceRow> _onRename;
    private bool _loading;

    public string Hash { get; }

    [ObservableProperty] private string _name;
    [ObservableProperty] private string _lastSeenText;
    [ObservableProperty] private bool _camera;
    [ObservableProperty] private bool _pcFiles;
    [ObservableProperty] private bool _screen;
    [ObservableProperty] private bool _control;
    [ObservableProperty] private bool _mic;
    [ObservableProperty] private bool _keyboard;

    public DeviceRow(TokenInfo info, DateTime? seen, DateTime now, Action<DeviceRow> onChanged, Action<DeviceRow> onRename)
    {
        _onChanged = onChanged;
        _onRename = onRename;
        Hash = info.Hash;
        _name = info.Name;
        _lastSeenText = SeenText(seen, now);
        _loading = true;
        var p = info.Permissions ?? DevicePermissions.None;
        Camera = p.Camera; PcFiles = p.PcFiles; Screen = p.Screen;
        Control = p.Control; Mic = p.Mic; Keyboard = p.Keyboard;
        _loading = false;
    }

    public DevicePermissions ToPermissions() => new(Camera, PcFiles, Screen, Control, Mic, Keyboard);

    // Restaura o nome sem disparar nada (Rename recusado).
    public void ResetName(string name)
    {
        _loading = true;
        Name = name;
        _loading = false;
    }

    // O TextBox só empurra o texto ao perder o foco: aí vira Rename.
    partial void OnNameChanged(string value) { if (!_loading) _onRename(this); }

    private void Changed() { if (!_loading) _onChanged(this); }
    partial void OnCameraChanged(bool value) => Changed();
    partial void OnPcFilesChanged(bool value) => Changed();
    partial void OnScreenChanged(bool value) => Changed();
    partial void OnControlChanged(bool value) => Changed();
    partial void OnMicChanged(bool value) => Changed();
    partial void OnKeyboardChanged(bool value) => Changed();

    public static string SeenText(DateTime? seen, DateTime now)
    {
        if (seen is not { } s) return "sem acesso desde que abriu";
        var d = now - s;
        if (d < TimeSpan.FromMinutes(1)) return "online";
        if (d < TimeSpan.FromHours(1)) return $"há {(int)d.TotalMinutes} min";
        if (d < TimeSpan.FromDays(1)) return $"há {(int)d.TotalHours} h";
        return $"há {(int)d.TotalDays} d";
    }
}
