using System;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;
using Serilog;
using Windows.UI.ViewManagement;

namespace Orbit.Services;

// Cor de destaque da ilha: a do Windows (que segue o wallpaper quando o Windows está em
// "automático"), ou um override "#RRGGBB" escolhido em Ajustes.
public static class Accent
{
    public static readonly (string Nome, string Hex)[] Paleta =
    {
        ("Âmbar", "#E2B04A"),
        ("Azul", "#4C9BF0"),
        ("Ameixa", "#C86BD6"),
        ("Menta", "#5CCF8A"),
        ("Coral", "#E8604C"),
    };

    private static readonly Color Fallback = Color.FromRgb(0x4C, 0x9B, 0xF0);
    private static readonly Regex HexRx = new("^#[0-9A-Fa-f]{6}$");

    public static event Action? Changed; // depois de cada Refresh (cor nova aplicada)

    // Guardado em campo: se o UISettings for coletado, o evento ColorValuesChanged para.
    private static UISettings? _ui;
    private static Func<string?> _override = () => null;

    public static Color? ParseHex(string? hex)
    {
        if (hex is null || !HexRx.IsMatch(hex)) return null;
        byte B(int i) => byte.Parse(hex.AsSpan(i, 2), NumberStyles.HexNumber);
        return Color.FromRgb(B(1), B(3), B(5));
    }

    public static Color Resolve(string? overrideHex, Color system) => ParseHex(overrideHex) ?? system;

    public static Color Soft(Color c) => Color.FromArgb(0x33, c.R, c.G, c.B);

    public static Color ReadSystem()
    {
        try
        {
            var c = (_ui ?? new UISettings()).GetColorValue(UIColorType.Accent);
            return Color.FromRgb(c.R, c.G, c.B);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Não consegui ler a cor de destaque do Windows");
            return Fallback;
        }
    }

    // Chamado uma vez no startup. overrideHex é lido a cada Refresh (Ajustes muda o valor).
    public static void Start(Func<string?> overrideHex)
    {
        _override = overrideHex;
        try
        {
            _ui = new UISettings();
            // O evento vem de outra thread — volta pro dispatcher antes de mexer nos recursos.
            _ui.ColorValuesChanged += (_, _) => Application.Current?.Dispatcher.BeginInvoke(Refresh);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "UISettings indisponível; acento não acompanhará o Windows");
        }
        Refresh();
    }

    public static void Refresh()
    {
        var c = Resolve(_override(), ReadSystem());
        var res = Application.Current.Resources;
        res["AccentBrush"] = Frozen(c);
        res["AccentSoftBrush"] = Frozen(Soft(c));
        Changed?.Invoke();
    }

    private static SolidColorBrush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }
}
