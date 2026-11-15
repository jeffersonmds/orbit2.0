using System;
using System.Collections.Generic;
using System.Linq;
using NAudio.CoreAudioApi;

namespace Orbit.Services;

public sealed record MicDevice(string Id, string Name);

// Lista os microfones do PC; a captura em si é WebRTC (WebRtcCapture).
public static class MicCapture
{
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
}
