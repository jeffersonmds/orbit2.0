using System;
using System.Collections.Generic;
using System.Linq;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Dsp;

namespace Orbit.Services;

public sealed record MicDevice(string Id, string Name);

// Microfone do PC pro aparelho: WASAPI (NAudio) → mono, reduzido pra ~24 kHz, PCM 16 bits.
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

    // O WDL já vem no NAudio: mantém fase/filtro entre pacotes, inclusive em 44,1 kHz.
    public sealed class PcmConverter
    {
        private readonly WaveFormat _format;
        private readonly bool _float;
        private readonly WdlResampler _resampler = new();
        private byte[] _tail = [];

        public static bool Supported(WaveFormat fmt)
        {
            var encoding = fmt.Encoding;
            if (fmt is WaveFormatExtensible ext)
                encoding = ext.SubFormat == new Guid("00000003-0000-0010-8000-00aa00389b71") ? WaveFormatEncoding.IeeeFloat
                    : ext.SubFormat == new Guid("00000001-0000-0010-8000-00aa00389b71") ? WaveFormatEncoding.Pcm : WaveFormatEncoding.Unknown;
            return fmt.Channels > 0 && fmt.SampleRate > 0 && fmt.BlockAlign == fmt.Channels * (fmt.BitsPerSample / 8)
                && (encoding == WaveFormatEncoding.IeeeFloat && fmt.BitsPerSample == 32
                    || encoding == WaveFormatEncoding.Pcm && fmt.BitsPerSample is 8 or 16 or 24 or 32);
        }

        public PcmConverter(WaveFormat format)
        {
            if (!Supported(format)) throw new NotSupportedException("Formato do microfone não aceito.");
            _format = format;
            _float = format.Encoding == WaveFormatEncoding.IeeeFloat || format is WaveFormatExtensible e && e.SubFormat == new Guid("00000003-0000-0010-8000-00aa00389b71");
            _resampler.SetMode(true, 2, false);
            _resampler.SetFilterParms();
            _resampler.SetFeedMode(true);
            _resampler.SetRates(format.SampleRate, TargetRate);
        }

        public byte[] Convert(ReadOnlySpan<byte> bytes)
        {
            var data = new byte[_tail.Length + bytes.Length];
            _tail.CopyTo(data, 0); bytes.CopyTo(data.AsSpan(_tail.Length));
            int frames = data.Length / _format.BlockAlign;
            _tail = data.AsSpan(frames * _format.BlockAlign).ToArray();
            if (frames == 0) return [];
            _resampler.ResamplePrepare(frames, 1, out var input, out var offset);
            int size = _format.BitsPerSample / 8;
            for (int f = 0; f < frames; f++)
            {
                double sum = 0;
                for (int c = 0; c < _format.Channels; c++)
                {
                    int at = f * _format.BlockAlign + c * size;
                    double v = _float ? BitConverter.ToSingle(data, at) : size switch
                    {
                        1 => (data[at] - 128) / 128.0,
                        2 => BitConverter.ToInt16(data, at) / 32768.0,
                        3 => ((data[at] | data[at + 1] << 8 | data[at + 2] << 16) << 8 >> 8) / 8388608.0,
                        _ => BitConverter.ToInt32(data, at) / 2147483648.0,
                    };
                    sum += double.IsFinite(v) ? Math.Clamp(v, -1, 1) : 0;
                }
                input[offset + f] = (float)(sum / _format.Channels);
            }
            var samples = new float[(int)Math.Ceiling((frames + 64) * TargetRate / (double)_format.SampleRate) + 64];
            int count = _resampler.ResampleOut(samples, 0, frames, samples.Length, 1);
            var output = new byte[count * 2];
            for (int i = 0; i < count; i++)
            {
                var v = (short)Math.Clamp(samples[i] * 32767.0, short.MinValue, short.MaxValue);
                output[i * 2] = (byte)v; output[i * 2 + 1] = (byte)(v >> 8);
            }
            return output;
        }
    }

    // Abre o microfone (null = padrão) e entrega pedaços de PCM. Dispose para e solta o dispositivo.
    public static (int Rate, IDisposable Stop) Open(string? deviceId, Action<byte[]> onPcm, Action<Exception?> onStopped)
    {
        var en = new MMDeviceEnumerator();
        MMDevice? dev = null;
        WasapiCapture? cap = null;
        try
        {
            dev = deviceId is null ? en.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications) : en.GetDevice(deviceId);
            cap = new WasapiCapture(dev, true, 50);
            var fmt = cap.WaveFormat;
            var converter = new PcmConverter(fmt);
            cap.DataAvailable += (_, e) =>
            {
                try { if (e.BytesRecorded > 0) onPcm(converter.Convert(e.Buffer.AsSpan(0, e.BytesRecorded))); }
                catch (Exception ex) { onStopped(ex); }
            };
            cap.RecordingStopped += (_, e) => onStopped(e.Exception);
            cap.StartRecording();
            return (TargetRate, new Stopper(cap, dev, en));
        }
        catch
        {
            // falhou no meio (aparelho sumiu, formato): solta os objetos COM já abertos
            cap?.Dispose();
            dev?.Dispose();
            en.Dispose();
            throw;
        }
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
