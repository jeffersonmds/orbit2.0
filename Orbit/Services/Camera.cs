using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading;
using System.Threading.Tasks;
using Serilog;
using Windows.Devices.Enumeration;
using Windows.Graphics.Imaging;
using Windows.Media.Capture;
using Windows.Media.Capture.Frames;
using Windows.Media.MediaProperties;
using Windows.Storage.Streams;

namespace Orbit.Services;

public sealed record CameraDevice(string Id, string Name);

// Câmera do notebook como fonte de JPEGs. Uma câmera/stream por vez. Abre no 1º viewer, fecha no
// último; parado não existe. ponytail: InitializeAsync pode exigir apartamento específico — se
// quebrar em thread do pool num futuro Windows, criar um DispatcherQueueController dedicado aqui.
public sealed class Camera : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private MediaCapture? _capture;
    private MediaFrameReader? _reader;
    private string? _openDevice;
    private string? _sessionOwner; // hash do aparelho dono do stream atual
    private CancellationTokenSource? _session; // sessão de stream atual; cancelar encerra o stream

    private readonly object _frameLock = new();
    private byte[]? _latest;
    private TaskCompletionSource<byte[]> _next = NewSignal();
    private long _lastEncodeTicks;
    private int _minIntervalMs;

    public static async Task<IReadOnlyList<CameraDevice>> DevicesAsync()
    {
        try
        {
            var found = await DeviceInformation.FindAllAsync(DeviceClass.VideoCapture);
            return OrderDevices(found.Select(d => new CameraDevice(d.Id, d.Name)).ToList());
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Câmera: não enumerei dispositivos");
            return [];
        }
    }

    public static IReadOnlyList<CameraDevice> OrderDevices(IEnumerable<CameraDevice> devices) =>
        devices.OrderBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase).ToList();

    // Abre a câmera e começa uma sessão. O iPhone (Safari) NÃO aborta a conexão MJPEG ao parar nem
    // ao trocar, então não dá pra contar viewers pela conexão: cada abertura cancela a sessão anterior
    // (derruba o stream antigo) e o fim real é explícito (EndStream/CloseAsync). O token devolvido é
    // observado pelo loop do stream; cancelá-lo encerra o stream na hora.
    public async Task<(CancellationTokenSource Session, CancellationToken Token)> OpenStreamAsync(string deviceId, Orbit.Bridge.CameraPreset preset, string? owner = null)
    {
        await _gate.WaitAsync();
        try
        {
            _session?.Cancel();
            _session?.Dispose();
            _session = null;
            await CloseLocked();
            await OpenLocked(deviceId, preset);
            _session = new CancellationTokenSource();
            _sessionOwner = owner;
            return (_session, _session.Token);
        }
        finally { _gate.Release(); }
    }

    // Fim do stream visto pelo servidor (conexão caiu de verdade). Só fecha se ainda é a sessão
    // atual — num switch a sessão já virou outra e o device novo deve continuar aberto.
    public async Task EndStream(CancellationTokenSource session)
    {
        await _gate.WaitAsync();
        try
        {
            if (_session == session)
            {
                _session.Dispose();
                _session = null;
                await CloseLocked();
            }
        }
        finally { _gate.Release(); }
    }

    // Fechar só vale pro dono do stream (mesma regra de StopVideo/StopAudio); owner nulo = incondicional.
    public static bool MayClose(string? sessionOwner, string? requester) => requester is null || sessionOwner == requester;

    // Parar explícito (o PWA chama ao tocar em "Parar"): derruba a sessão e fecha a câmera já.
    public async Task CloseAsync(string? owner = null)
    {
        await _gate.WaitAsync();
        try
        {
            if (_session is not null && !MayClose(_sessionOwner, owner)) return;
            _session?.Cancel();
            _session?.Dispose();
            _session = null;
            await CloseLocked();
        }
        finally { _gate.Release(); }
    }

    private async Task OpenLocked(string deviceId, Orbit.Bridge.CameraPreset preset)
    {
        _minIntervalMs = 1000 / Math.Max(preset.Fps, 1);
        var capture = new MediaCapture();
        await capture.InitializeAsync(new MediaCaptureInitializationSettings
        {
            VideoDeviceId = deviceId,
            StreamingCaptureMode = StreamingCaptureMode.Video,
            MemoryPreference = MediaCaptureMemoryPreference.Cpu,
            SharingMode = MediaCaptureSharingMode.ExclusiveControl,
        });
        var source = capture.FrameSources.Values.FirstOrDefault(s => s.Info.MediaStreamType == MediaStreamType.VideoRecord)
                     ?? capture.FrameSources.Values.First(s => s.Info.SourceKind == MediaFrameSourceKind.Color);
        // Formato mais perto da largura do preset (desempata pela maior taxa).
        var format = source.SupportedFormats
            .Where(f => f.VideoFormat.Width > 0)
            .OrderBy(f => Math.Abs((int)f.VideoFormat.Width - preset.Width))
            .ThenByDescending(f => f.FrameRate.Numerator / Math.Max(1.0, f.FrameRate.Denominator))
            .FirstOrDefault();
        if (format is not null) await source.SetFormatAsync(format);
        var reader = await capture.CreateFrameReaderAsync(source);
        reader.FrameArrived += OnFrame;
        await reader.StartAsync();
        _capture = capture;
        _reader = reader;
        _openDevice = deviceId;
        KeepAwake.On();
        Log.Information("Câmera: aberta {Dev}", deviceId);
    }

    private void OnFrame(MediaFrameReader sender, MediaFrameArrivedEventArgs args)
    {
        var now = Environment.TickCount64;
        if (now - Interlocked.Read(ref _lastEncodeTicks) < _minIntervalMs) { using (sender.TryAcquireLatestFrame()) { } return; }
        using var frame = sender.TryAcquireLatestFrame();
        var bmp = frame?.VideoMediaFrame?.SoftwareBitmap;
        if (bmp is null) return;
        Interlocked.Exchange(ref _lastEncodeTicks, now);
        _ = EncodeAsync(bmp); // SoftwareBitmap é copiado dentro; o frame é liberado ao sair
    }

    private async Task EncodeAsync(SoftwareBitmap bmp)
    {
        try
        {
            using var copy = SoftwareBitmap.Copy(bmp);
            using var ms = new InMemoryRandomAccessStream();
            var enc = await BitmapEncoder.CreateAsync(BitmapEncoder.JpegEncoderId, ms);
            using var ready = SoftwareBitmap.Convert(copy, BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore);
            enc.SetSoftwareBitmap(ready);
            await enc.FlushAsync();
            var bytes = new byte[ms.Size];
            await ms.ReadAsync(bytes.AsBuffer(), (uint)ms.Size, InputStreamOptions.None);
            Publish(bytes);
        }
        catch (Exception ex) { Log.Debug(ex, "Câmera: falha ao codificar quadro"); }
    }

    private void Publish(byte[] jpeg)
    {
        lock (_frameLock)
        {
            _latest = jpeg;
            var signal = _next;
            _next = NewSignal();
            signal.TrySetResult(jpeg);
        }
    }

    // Próximo JPEG. Bloqueia até chegar um ou ct cancelar.
    public async Task<byte[]> NextFrameAsync(CancellationToken ct)
    {
        Task<byte[]> wait;
        lock (_frameLock) wait = _next.Task;
        return await wait.WaitAsync(ct);
    }

    // Foto em resolução cheia: abre uma MediaCapture própria, captura e fecha (não mexe no stream).
    public async Task<byte[]> PhotoAsync(string deviceId)
    {
        var capture = new MediaCapture();
        try
        {
            await capture.InitializeAsync(new MediaCaptureInitializationSettings
            {
                VideoDeviceId = deviceId,
                StreamingCaptureMode = StreamingCaptureMode.Video,
                MemoryPreference = MediaCaptureMemoryPreference.Cpu,
                SharingMode = MediaCaptureSharingMode.SharedReadOnly, // convive com o stream
            });
            using var ms = new InMemoryRandomAccessStream();
            await capture.CapturePhotoToStreamAsync(ImageEncodingProperties.CreateJpeg(), ms);
            var bytes = new byte[ms.Size];
            ms.Seek(0);
            await ms.ReadAsync(bytes.AsBuffer(), (uint)ms.Size, InputStreamOptions.None);
            return bytes;
        }
        finally { capture.Dispose(); }
    }

    private async Task CloseLocked()
    {
        if (_capture is null && _reader is null) return;
        if (_reader is not null)
        {
            _reader.FrameArrived -= OnFrame;
            try { await _reader.StopAsync(); } catch { }
            _reader.Dispose();
            _reader = null;
        }
        _capture?.Dispose();
        _capture = null;
        _openDevice = null;
        lock (_frameLock) _latest = null;
        KeepAwake.Off();
        // O LED da webcam só apaga quando o objeto COM nativo da MediaCapture é liberado; o Dispose
        // gerenciado não faz isso na hora. Forçar a finalização solta o sensor (e apaga a luz) já.
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        Log.Information("Câmera: fechada");
    }

    private static TaskCompletionSource<byte[]> NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    public void Dispose()
    {
        // ponytail: não dispõe _gate — o serviço é reusado após desligar/ligar a ponte.
        try { CloseAsync().GetAwaiter().GetResult(); } catch { }
    }
}
