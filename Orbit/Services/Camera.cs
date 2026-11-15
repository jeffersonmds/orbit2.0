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
using Windows.Media.MediaProperties;
using Windows.Storage.Streams;

namespace Orbit.Services;

public sealed record CameraDevice(string Id, string Name);

// Câmeras do notebook: só enumerar e tirar foto. A transmissão ao vivo é WebRTC (WebRtcCapture).
public static class Camera
{
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

    // Foto em resolução cheia: abre uma MediaCapture própria, captura e fecha (não mexe na transmissão WebRTC).
    public static async Task<byte[]> PhotoAsync(string deviceId, CancellationToken ct = default)
    {
        var capture = new MediaCapture();
        try
        {
            await capture.InitializeAsync(new MediaCaptureInitializationSettings
            {
                VideoDeviceId = deviceId,
                StreamingCaptureMode = StreamingCaptureMode.Video,
                MemoryPreference = MediaCaptureMemoryPreference.Cpu,
                SharingMode = MediaCaptureSharingMode.SharedReadOnly, // convive com a transmissão
            }).AsTask(ct);
            ct.ThrowIfCancellationRequested();
            using var ms = new InMemoryRandomAccessStream();
            await capture.CapturePhotoToStreamAsync(ImageEncodingProperties.CreateJpeg(), ms).AsTask(ct);
            ct.ThrowIfCancellationRequested();
            return await ReadJpegAsync(ms);
        }
        finally { capture.Dispose(); }
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
}
