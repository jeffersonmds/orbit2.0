using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Orbit.Services;

namespace Orbit.Tabs.Controles;

// Uma linha do mixer (um app). Slider/mudo vão direto pro AudioMixer.
public sealed partial class MixerAppViewModel : ObservableObject
{
    private static readonly Dictionary<string, ImageSource?> Icons = new(); // ponytail: cresce com cada exe visto; poucos na prática
    private readonly AudioMixer _mixer;
    private bool _syncing;

    public string Key { get; }
    public string Name { get; }
    public ImageSource? Icon { get; }
    public bool IsSystem => Key == AudioMixer.SystemKey;
    public string FallbackGlyph => IsSystem ? "\uE7F5" : "\uE8D6"; // alto-falante / nota

    [ObservableProperty] private double _percent;
    [ObservableProperty] private bool _muted;
    [ObservableProperty] private double _level; // 0..1, medidor

    public string MuteGlyph => Muted ? "\uE74F" : "\uE767";

    public MixerAppViewModel(AudioMixer mixer, AudioMixer.App app)
    {
        _mixer = mixer;
        Key = app.Key;
        Name = app.Name;
        Icon = IconFor(app.ExePath);
        Sync(app);
    }

    public void Sync(AudioMixer.App app)
    {
        _syncing = true;
        Percent = app.Percent;
        Muted = app.Muted;
        _syncing = false;
    }

    partial void OnPercentChanged(double value)
    {
        if (!_syncing) _mixer.SetVolume(Key, value);
    }

    partial void OnMutedChanged(bool value) => OnPropertyChanged(nameof(MuteGlyph));

    [RelayCommand]
    private void ToggleMute()
    {
        Muted = !Muted;
        _mixer.SetMute(Key, Muted);
    }

    private static ImageSource? IconFor(string? exePath)
    {
        if (exePath is null) return null;
        if (Icons.TryGetValue(exePath, out var cached)) return cached;
        ImageSource? img = null;
        try
        {
            using var ico = System.Drawing.Icon.ExtractAssociatedIcon(exePath);
            if (ico is not null)
            {
                var bmp = Imaging.CreateBitmapSourceFromHIcon(ico.Handle, Int32Rect.Empty, BitmapSizeOptions.FromWidthAndHeight(32, 32));
                bmp.Freeze();
                img = bmp;
            }
        }
        catch { } // exe protegido/sumiu: a view mostra o glifo
        return Icons[exePath] = img;
    }
}
