using System;
using NAudio.CoreAudioApi;

namespace Orbit.Services;

// Nível do som que está saindo agora (o pico que o Windows já calcula pro mixer) — alimenta as
// barrinhas da pílula. Barato: não captura áudio, só lê o medidor do dispositivo padrão.
public sealed class AudioMeter : IDisposable
{
    private MMDeviceEnumerator? _enumerator;
    private MMDevice? _device;
    private DateTime _acquiredAt;

    // 0..1. Troca de dispositivo (fone ↔ caixa) é pega relendo o padrão a cada 5 s.
    public float Peak()
    {
        try
        {
            if (_device is null || DateTime.UtcNow - _acquiredAt > TimeSpan.FromSeconds(5))
            {
                _device?.Dispose();
                _enumerator ??= new MMDeviceEnumerator();
                _device = _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                _acquiredAt = DateTime.UtcNow;
            }
            return _device.AudioMeterInformation.MasterPeakValue;
        }
        catch
        {
            _device?.Dispose();
            _device = null;
            return 0;
        }
    }

    // Altura (0..1) de cada barra: o pico (curva que realça som baixo) modulado por uma onda com
    // fase própria por barra, pra não subirem todas iguais. Puro, testável.
    public static double[] Bars(double peak, double seconds, int count = 4)
    {
        var level = Math.Pow(Math.Clamp(peak, 0, 1), 0.6);
        var bars = new double[count];
        for (int i = 0; i < count; i++)
        {
            var wave = 0.6 + 0.4 * Math.Sin(seconds * (7 + 3.1 * i) + i * 1.7);
            bars[i] = Math.Clamp(0.15 + level * wave, 0.15, 1); // nunca some de vez: fica um tracinho
        }
        return bars;
    }

    public void Dispose()
    {
        _device?.Dispose();
        _enumerator?.Dispose();
    }
}

// "Tocando" segundo o app não quer dizer som no PC (Spotify Connect tocando no celular, app no mudo).
// Uma leitura por segundo: qualquer som liga na hora; 3 s seguidos de silêncio desligam.
public sealed class SilenceGate
{
    public const float Threshold = 0.01f;
    public const int SilentTicks = 3;
    private int _silent;

    public bool Audible { get; private set; } = true; // começa ligado: não atrasa a pílula de quem acabou de dar play

    public bool Feed(float peak)
    {
        if (peak > Threshold) { _silent = 0; Audible = true; }
        else if (++_silent >= SilentTicks) Audible = false;
        return Audible;
    }

    public void Reset() { _silent = 0; Audible = true; }
}
