using System;
using System.Windows.Media;

namespace Orbit.Tabs.Mercado;

// Setinha + cor pra variação % (verde sobe / vermelho cai).
internal static class ChangeIndicator
{
    private static readonly Brush Up = new SolidColorBrush(Color.FromRgb(0x4A, 0xDE, 0x80));
    private static readonly Brush Down = new SolidColorBrush(Color.FromRgb(0xF8, 0x71, 0x71));

    public static (string text, Brush color) Format(double change, int decimals = 1)
    {
        var arrow = change >= 0 ? "▲" : "▼";
        return ($"{arrow} {Math.Abs(change).ToString("F" + decimals)}%", change >= 0 ? Up : Down);
    }
}
