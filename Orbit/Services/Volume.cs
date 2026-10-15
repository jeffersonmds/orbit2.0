using System;
using NAudio.CoreAudioApi;
using Serilog;

namespace Orbit.Services;

// Volume mestre da saída padrão (Core Audio via NAudio.Wasapi). Criado só com a aba
// Controles visível; Dispose solta o callback do Windows.
// ponytail: troca de dispositivo padrão com a aba aberta não é seguida; reabrir a aba resolve.
public sealed class Volume : IDisposable
{
    private readonly MMDeviceEnumerator _enumerator = new();
    private readonly MMDevice? _device;

    // (0–100, mudo). Vem de thread do Windows — quem assina volta pro dispatcher.
    public event Action<double, bool>? Changed;

    public Volume()
    {
        try
        {
            _device = _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            _device.AudioEndpointVolume.OnVolumeNotification += OnNotify;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Sem dispositivo de áudio padrão");
        }
    }

    public bool Available => _device is not null;
    public double Percent => _device is null ? 0 : ToPercent(_device.AudioEndpointVolume.MasterVolumeLevelScalar);
    public bool Muted => _device?.AudioEndpointVolume.Mute ?? false;

    public void SetPercent(double percent)
    {
        if (_device is not null) _device.AudioEndpointVolume.MasterVolumeLevelScalar = ToScalar(percent);
    }

    public void ToggleMute()
    {
        if (_device is not null) _device.AudioEndpointVolume.Mute = !_device.AudioEndpointVolume.Mute;
    }

    private void OnNotify(AudioVolumeNotificationData data) => Changed?.Invoke(ToPercent(data.MasterVolume), data.Muted);

    public static double ToPercent(float scalar) => Math.Round(Math.Clamp(scalar, 0f, 1f) * 100);
    public static float ToScalar(double percent) => (float)(Math.Clamp(percent, 0, 100) / 100);

    public void Dispose()
    {
        if (_device is not null)
        {
            _device.AudioEndpointVolume.OnVolumeNotification -= OnNotify;
            _device.Dispose();
        }
        _enumerator.Dispose();
    }
}
