using System;
using System.Collections.Generic;
using System.Linq;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace Orbit.Services;

public sealed record MicDevice(string Id, string Name);

// Microfone do PC pro iPhone: WASAPI (NAudio) → mono, reduzido pra ~24 kHz, PCM 16 bits.
// O iPhone toca com Web Audio; sem codec (~48 KB/s cabe folgado no Tailscale).
public static class MicCapture
{
    public const int TargetRate = 24000;

    // Padrão de comunicação primeiro (o microfone que o Windows usa em chamadas), depois os outros.
    public static IReadOnlyList<MicDevice> Devices()
    {
        try
        {
            using var en = new MMDeviceEnumerator();
            string? def = null;
            try { using var d = en.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications); def = d.ID; } catch { }
            var list = new List<MicDevice>();
            foreach (var d in en.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
                using (d) list.Add(new MicDevice(d.ID, d.FriendlyName));
            return list.OrderByDescending(m => m.Id == def).ToList();
        }
        catch { return Array.Empty<MicDevice>(); }
    }

    // Fator inteiro de redução até perto de 24 kHz (48k → 2, 44,1k → 2, 16k → 1).
    public static int Factor(int sampleRate) => Math.Max(1, (int)Math.Round(sampleRate / (double)TargetRate));

    // Intercalado (float 32 ou PCM 16) → mono Int16 little-endian, média dos canais e de cada grupo
    // de `factor` amostras (filtro passa-baixa simples antes de descartar).
    // ponytail: sobra de quadros entre buffers é descartada (buffers do WASAPI são múltiplos de 10 ms).
    public static byte[] ToMonoPcm16(ReadOnlySpan<byte> data, int channels, bool isFloat, int factor)
    {
        int bytesPerSample = isFloat ? 4 : 2;
        int frames = data.Length / (bytesPerSample * channels);
        int outCount = frames / factor;
        var output = new byte[outCount * 2];
        for (int o = 0; o < outCount; o++)
        {
            double sum = 0;
            for (int f = o * factor; f < (o + 1) * factor; f++)
                for (int c = 0; c < channels; c++)
                {
                    int at = (f * channels + c) * bytesPerSample;
                    sum += isFloat ? BitConverter.ToSingle(data.Slice(at, 4)) : BitConverter.ToInt16(data.Slice(at, 2)) / 32768.0;
                }
            var v = (short)Math.Clamp(sum / (factor * channels) * 32767.0, short.MinValue, short.MaxValue);
            output[o * 2] = (byte)v;
            output[o * 2 + 1] = (byte)(v >> 8);
        }
        return output;
    }

    // Abre o microfone (null = padrão) e entrega pedaços de PCM. Dispose para e solta o dispositivo.
    public static (int Rate, IDisposable Stop) Open(string? deviceId, Action<byte[]> onPcm)
    {
        var en = new MMDeviceEnumerator();
        var dev = deviceId is null ? en.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications) : en.GetDevice(deviceId);
        var cap = new WasapiCapture(dev, true, 50);
        var fmt = cap.WaveFormat;
        bool isFloat = fmt.BitsPerSample == 32; // mix format do WASAPI compartilhado é float; PCM 32 bits não aparece na prática
        int factor = Factor(fmt.SampleRate);
        cap.DataAvailable += (_, e) =>
        {
            if (e.BytesRecorded > 0) onPcm(ToMonoPcm16(e.Buffer.AsSpan(0, e.BytesRecorded), fmt.Channels, isFloat, factor));
        };
        cap.StartRecording();
        return (fmt.SampleRate / factor, new Stopper(cap, dev, en));
    }

    private sealed class Stopper(WasapiCapture cap, MMDevice dev, MMDeviceEnumerator en) : IDisposable
    {
        public void Dispose()
        {
            try { cap.StopRecording(); } catch { }
            cap.Dispose();
            dev.Dispose();
            en.Dispose();
        }
    }
}
