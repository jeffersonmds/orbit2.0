using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading;
using System.Threading.Tasks;
using Serilog;
using Windows.Devices.Enumeration;
using Windows.Foundation;
using Orbit.Bridge;
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
    private string? _streamId;
    private string? _sessionOwner; // hash do aparelho dono do stream atual
    private CancellationTokenSource? _session; // sessão de stream atual; cancelar encerra o stream

    private readonly object _frameLock = new();
    private byte[]? _latest;
    private int _gen; // sobe ao fechar: quadro que terminou de codificar depois disso é da sessão velha
    private TaskCompletionSource<byte[]> _next = NewSignal();
    private FramePacer _pacer = new();
    private Task _encoding = Task.CompletedTask;
    private CameraPreset _preset = CameraPreset.Default, _requested = CameraPreset.Default;
    private Exception? _failure;
    private long _sentFrames, _started;
    private int _width, _height, _slow, _fast;

    public (int Width, int Height, double Fps, bool Reduced, string? Id) Status
    {
        get { lock (_frameLock) return (_width, _height, _sentFrames * 1000.0 / Math.Max(1, Environment.TickCount64 - _started), _preset.Width < _requested.Width, _streamId); }
    }

    public void ReportSend(double ms)
    {
        lock (_frameLock)
        {
            _sentFrames++;
            _preset = _preset.Adapt(ms, ref _slow, ref _fast, _requested);
        }
    }

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
    public async Task<(CancellationTokenSource Session, CancellationToken Token)> OpenStreamAsync(string deviceId, Orbit.Bridge.CameraPreset preset, string? owner = null, CancellationToken ct = default, string? id = null)
    {
        await _gate.WaitAsync(ct);
        try
        {
            _session?.Cancel();
            _session?.Dispose();
            _session = null;
            await CloseLocked();
            ct.ThrowIfCancellationRequested();
            await OpenLocked(deviceId, preset, ct);
            if (ct.IsCancellationRequested) { await CloseLocked(); ct.ThrowIfCancellationRequested(); }
            _session = new CancellationTokenSource();
            _sessionOwner = owner;
            lock (_frameLock) _streamId = id;
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

    private async Task OpenLocked(string deviceId, Orbit.Bridge.CameraPreset preset, CancellationToken ct)
    {
        lock (_frameLock)
        {
            _preset = _requested = preset;
            _pacer = new(); _failure = null; _next = NewSignal(); _sentFrames = 0; _width = _height = _slow = _fast = 0;
            _started = Environment.TickCount64;
        }
        var capture = new MediaCapture();
        MediaFrameReader? reader = null;
        // Só vira campo depois de tudo dar certo: falha no meio (câmera ocupada, formato) solta o que abriu,
        // senão a webcam fica presa até o GC.
        try
        {
            ct.ThrowIfCancellationRequested();
            await capture.InitializeAsync(new MediaCaptureInitializationSettings
            {
                VideoDeviceId = deviceId,
                StreamingCaptureMode = StreamingCaptureMode.Video,
                MemoryPreference = MediaCaptureMemoryPreference.Cpu,
                SharingMode = MediaCaptureSharingMode.ExclusiveControl,
            }).AsTask(ct);
            ct.ThrowIfCancellationRequested();
            var source = capture.FrameSources.Values.FirstOrDefault(s => s.Info.MediaStreamType == MediaStreamType.VideoRecord)
                         ?? capture.FrameSources.Values.First(s => s.Info.SourceKind == MediaFrameSourceKind.Color);
            // Evita pedir ao sensor resolução/taxa que serão descartadas no encoder.
            var format = source.SupportedFormats
                .Where(f => f.VideoFormat.Width > 0)
                .OrderBy(f => CameraPreset.FormatCost((int)f.VideoFormat.Width, f.FrameRate.Numerator / Math.Max(1.0, f.FrameRate.Denominator), preset))
                .FirstOrDefault();
            if (format is not null) await source.SetFormatAsync(format).AsTask(ct);
            reader = await capture.CreateFrameReaderAsync(source).AsTask(ct);
            reader.FrameArrived += OnFrame;
            capture.Failed += OnFailed;
            var status = await reader.StartAsync().AsTask(ct);
            ct.ThrowIfCancellationRequested();
            if (status != MediaFrameReaderStartStatus.Success) throw new InvalidOperationException("Câmera não iniciou: " + status);
        }
        catch
        {
            if (reader is not null) { reader.FrameArrived -= OnFrame; reader.Dispose(); }
            capture.Failed -= OnFailed;
            capture.Dispose();
            throw;
        }
        _capture = capture;
        _reader = reader;
        KeepAwake.On();
        Log.Information("Câmera: aberta {Dev}", deviceId);
    }

    private void OnFailed(MediaCapture sender, MediaCaptureFailedEventArgs args)
    {
        lock (_frameLock)
        {
            if (sender != _capture) return;
            _failure = new IOException("A câmera perdeu o sinal.");
            _next.TrySetResult([]);
        }
    }

    private void OnFrame(MediaFrameReader sender, MediaFrameArrivedEventArgs args)
    {
        try
        {
            using var frame = sender.TryAcquireLatestFrame();
            using var bmp = frame?.VideoMediaFrame?.SoftwareBitmap;
            if (bmp is null) return;
            lock (_frameLock)
            {
                if (sender != _reader || !_encoding.IsCompleted || !_pacer.Accept(Environment.TickCount64, _preset.Fps)) return;
                // A cópia nasce antes do await; ambos os bitmaps têm dono explícito.
                _encoding = EncodeAsync(SoftwareBitmap.Copy(bmp), _gen, _preset);
            }
        }
        catch (Exception ex) { Log.Debug(ex, "Câmera: quadro indisponível"); }
    }

    private async Task EncodeAsync(SoftwareBitmap copy, int gen, CameraPreset preset)
    {
        using (copy)
        try
        {
            using var ms = new InMemoryRandomAccessStream();
            var options = new BitmapPropertySet { ["ImageQuality"] = new BitmapTypedValue(preset.JpegQuality, PropertyType.Single) };
            var enc = await BitmapEncoder.CreateAsync(BitmapEncoder.JpegEncoderId, ms, options);
            using var ready = SoftwareBitmap.Convert(copy, BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore);
            enc.SetSoftwareBitmap(ready);
            var width = (uint)Math.Min(preset.Width, ready.PixelWidth);
            var height = (uint)Math.Max(1, Math.Round(ready.PixelHeight * width / (double)ready.PixelWidth));
            enc.BitmapTransform.ScaledWidth = width;
            enc.BitmapTransform.ScaledHeight = height;
            enc.BitmapTransform.InterpolationMode = BitmapInterpolationMode.Linear;
            await enc.FlushAsync();
            var bytes = await ReadJpegAsync(ms);
            lock (_frameLock)
            {
                if (gen != _gen) return;
                _width = (int)width; _height = (int)height;
                Publish(bytes, gen);
            }
        }
        catch (Exception ex) { Log.Debug(ex, "Câmera: falha ao codificar quadro"); }
    }

    private static async Task<byte[]> ReadJpegAsync(InMemoryRandomAccessStream ms)
    {
        if (ms.Size == 0 || ms.Size > int.MaxValue) throw new IOException("JPEG incompleto.");
        var bytes = new byte[(int)ms.Size];
        ms.Seek(0);
        var read = await ms.ReadAsync(bytes.AsBuffer(), (uint)bytes.Length, InputStreamOptions.None);
        if (read.Length != bytes.Length) throw new IOException("JPEG incompleto.");
        return bytes;
    }

    private void Publish(byte[] jpeg, int gen)
    {
        lock (_frameLock)
        {
            if (gen != _gen) return;
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
        lock (_frameLock)
        {
            if (_failure is not null) throw _failure;
            wait = _next.Task;
        }
        var jpeg = await wait.WaitAsync(TimeSpan.FromSeconds(8), ct);
        lock (_frameLock) if (_failure is not null) throw _failure;
        return jpeg;
    }

    // Foto em resolução cheia: abre uma MediaCapture própria, captura e fecha (não mexe no stream).
    public async Task<byte[]> PhotoAsync(string deviceId, CancellationToken ct = default)
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
            }).AsTask(ct);
            ct.ThrowIfCancellationRequested();
            using var ms = new InMemoryRandomAccessStream();
            await capture.CapturePhotoToStreamAsync(ImageEncodingProperties.CreateJpeg(), ms).AsTask(ct);
            ct.ThrowIfCancellationRequested();
            return await ReadJpegAsync(ms);
        }
        finally { capture.Dispose(); }
    }

    private async Task CloseLocked()
    {
        MediaFrameReader? reader;
        lock (_frameLock) { _latest = null; _gen++; _streamId = null; reader = _reader; _reader = null; }
        if (_capture is null && reader is null) return;
        if (reader is not null)
        {
            reader.FrameArrived -= OnFrame;
            try { await reader.StopAsync(); } catch { }
            reader.Dispose();
        }
        if (_capture is not null) _capture.Failed -= OnFailed;
        _capture?.Dispose();
        _capture = null;
        await _encoding;
        KeepAwake.Off();
        // O LED da webcam só apaga quando o objeto COM nativo da MediaCapture é liberado; o Dispose
        // gerenciado não faz isso na hora. Forçar a finalização solta o sensor (e apaga a luz) já.
        // ponytail: pausa global curta ao fechar; tirar só depois de confirmar o LED apagando sem isto.
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        Log.Information("Câmera: fechada");
    }

    private static TaskCompletionSource<byte[]> NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    public void Dispose()
    {
        // O serviço é reusado após desligar/ligar a ponte.
        try { CloseAsync().GetAwaiter().GetResult(); } catch { }
    }
}
