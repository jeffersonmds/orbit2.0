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

    // + tempo restante na bateria (BatteryLifeRemaining: segundos, -1 = desconhecido/na tomada).
    public static (int? Percent, bool Charging, int? SecondsLeft) ReadFull()
    {
        var s = SystemInformation.PowerStatus;
        var (percent, charging) = Read();
        return (percent, charging, s.BatteryLifeRemaining > 0 ? s.BatteryLifeRemaining : null);
    }

    public static (string Big, string Detail) Describe(int? percent, bool charging, int? secondsLeft)
    {
        if (percent is not { } p) return ("—", "Sem bateria · na tomada");
        if (charging) return ($"{p}%", p >= 100 ? "Carregada · na tomada" : "Carregando");
        return ($"{p}%", secondsLeft is > 0 ? $"Na bateria · {FormatDuration(System.TimeSpan.FromSeconds(secondsLeft.Value))} restantes" : "Na bateria");
    }

    public static string FormatDuration(System.TimeSpan t)
    {
        int totalMin = (int)System.Math.Ceiling(t.TotalMinutes);
        return totalMin >= 60 ? $"{totalMin / 60} h {totalMin % 60:00} min" : $"{totalMin} min";
    }

    // BatteryLifePercent vem 0..1, ou 255 quando desconhecido.
    public static int? Percent(float lifePercent, bool hasBattery) =>
        hasBattery && lifePercent is >= 0 and <= 1 ? (int)System.Math.Round(lifePercent * 100) : null;
}
