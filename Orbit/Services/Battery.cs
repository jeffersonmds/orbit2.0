using System.Windows.Forms;

namespace Orbit.Services;

public static class Battery
{
    public static (int? Percent, bool Charging) Read()
    {
        var s = SystemInformation.PowerStatus;
        bool has = !s.BatteryChargeStatus.HasFlag(BatteryChargeStatus.NoSystemBattery);
        return (Percent(s.BatteryLifePercent, has), s.PowerLineStatus == PowerLineStatus.Online);
    }

    // BatteryLifePercent vem 0..1, ou 255 quando desconhecido.
    public static int? Percent(float lifePercent, bool hasBattery) =>
        hasBattery && lifePercent is >= 0 and <= 1 ? (int)System.Math.Round(lifePercent * 100) : null;
}
