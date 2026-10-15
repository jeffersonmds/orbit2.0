using System;
using System.Globalization;
using System.Net.NetworkInformation;

namespace Orbit.Services;

// Taxa de rede ↓/↑ somando as interfaces ativas (menos loopback), por delta entre leituras.
public sealed class NetStats
{
    private static readonly CultureInfo Pt = new("pt-BR");
    private long _rx, _tx;
    private DateTime _at;

    public (double DownBps, double UpBps) Read()
    {
        long rx = 0, tx = 0;
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up || ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
            var s = ni.GetIPStatistics();
            rx += s.BytesReceived;
            tx += s.BytesSent;
        }
        var now = DateTime.UtcNow;
        double secs = (now - _at).TotalSeconds;
        var result = _at == default ? (0d, 0d) : (Rate(_rx, rx, secs), Rate(_tx, tx, secs));
        _rx = rx;
        _tx = tx;
        _at = now;
        return result;
    }

    // Aba fechada: a próxima leitura recomeça (sem média do tempo parado).
    public void Reset() => _at = default;

    public static double Rate(long before, long after, double seconds) =>
        seconds <= 0 || after < before ? 0 : (after - before) / seconds;

    public static string Format(double bytesPerSec) =>
        bytesPerSec >= 1_048_576 ? string.Format(Pt, "{0:0.0} MB/s", bytesPerSec / 1_048_576)
        : bytesPerSec >= 1024 ? string.Format(Pt, "{0:0} KB/s", bytesPerSec / 1024)
        : string.Format(Pt, "{0:0} B/s", bytesPerSec);
}
