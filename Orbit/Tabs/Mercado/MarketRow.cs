using System.Windows; // Point
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Orbit.Tabs.Mercado;

// Uma linha do ticker de mercado (cripto ou câmbio).
public partial class MarketRow : ObservableObject
{
    public string Id { get; }
    public string Glyph { get; }
    public Brush GlyphColor { get; }
    public string Name { get; }
    public MarketKind Kind { get; }

    [ObservableProperty] private string _price = "…";
    [ObservableProperty] private string _change = "";
    [ObservableProperty] private Brush _changeColor = Brushes.Gray;
    [ObservableProperty] private PointCollection? _sparkline;

    public const double W = 90, H = 24; // tamanho da sparkline na view

    public MarketRow(string id, string glyph, string glyphHex, string name, MarketKind kind)
    {
        Id = id;
        Glyph = glyph;
        GlyphColor = new SolidColorBrush((Color)ColorConverter.ConvertFromString(glyphHex));
        Name = name;
        Kind = kind;
    }

    // Normaliza a série pro box do sparkline (y invertido: preço alto = topo).
    public void SetSparkline(double[]? values)
    {
        if (values is null || values.Length < 2) { Sparkline = null; return; }
        double min = values[0], max = values[0];
        foreach (var v in values) { if (v < min) min = v; if (v > max) max = v; }
        double range = max - min;
        var pts = new PointCollection(values.Length);
        for (int i = 0; i < values.Length; i++)
        {
            double x = i / (double)(values.Length - 1) * W;
            double y = range < 1e-9 ? H / 2 : H - (values[i] - min) / range * H;
            pts.Add(new Point(x, y));
        }
        Sparkline = pts;
    }
}
