using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Orbit.Island;

// Conta-gotas (Ctrl+Alt+C): lupa pixelada seguindo o cursor sobre a tela congelada. Clique copia
// "#RRGGBB"; botão direito ou Esc cancela. Picked = a cor escolhida (null = cancelou).
public sealed class ColorPickerWindow : FrozenScreen
{
    private const int Span = 13;          // pixels da tela mostrados na lupa (ímpar: o do meio é o escolhido)
    private const double Lens = 143;      // tamanho da lupa (DIP)
    private readonly ImageBrush _zoom;
    private readonly Border _lens, _label;
    private readonly TextBlock _hex;
    private readonly Border _swatch;
    private readonly Canvas _canvas = new();

    public Color? Picked { get; private set; }

    public ColorPickerWindow()
    {
        Cursor = Cursors.Cross;
        _zoom = new ImageBrush(Shot) { ViewboxUnits = BrushMappingMode.Absolute, Stretch = Stretch.Fill };
        RenderOptions.SetBitmapScalingMode(_zoom, BitmapScalingMode.NearestNeighbor);
        var cell = Lens / Span;
        _lens = new Border
        {
            Width = Lens, Height = Lens, CornerRadius = new CornerRadius(12), BorderThickness = new Thickness(2),
            BorderBrush = Brushes.White, Background = _zoom, IsHitTestVisible = false,
            Child = new Border // quadradinho do pixel escolhido
            {
                Width = cell + 2, Height = cell + 2, BorderThickness = new Thickness(1.5), BorderBrush = Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            },
        };
        _swatch = new Border { Width = 14, Height = 14, CornerRadius = new CornerRadius(7), Margin = new Thickness(0, 0, 8, 0) };
        _hex = new TextBlock { Foreground = Brushes.White, FontSize = 14, FontWeight = FontWeights.SemiBold, FontFamily = new FontFamily("Consolas") };
        _label = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(220, 20, 20, 22)), CornerRadius = new CornerRadius(10),
            Padding = new Thickness(10, 6, 10, 6), IsHitTestVisible = false,
            Child = new StackPanel { Orientation = Orientation.Horizontal, Children = { _swatch, _hex } },
        };
        _canvas.Children.Add(_lens);
        _canvas.Children.Add(_label);
        Content = new Grid { Children = { new Image { Source = Shot, Stretch = Stretch.Fill }, _canvas } };

        MouseMove += (_, _) => Track();
        Loaded += (_, _) => Track();
        MouseLeftButtonUp += (_, _) =>
        {
            if (CursorPixel() is { } p) Picked = ColorAt(p.X, p.Y);
            Close();
        };
        MouseRightButtonUp += (_, _) => Close();
    }

    private void Track()
    {
        if (CursorPixel() is not { } p) return;
        _zoom.Viewbox = new Rect(p.X - Span / 2, p.Y - Span / 2, Span, Span);
        var c = ColorAt(p.X, p.Y);
        _swatch.Background = new SolidColorBrush(c);
        _hex.Text = Hex(c);
        // lupa à direita/abaixo do cursor; perto da borda, vai pro outro lado
        var at = ToWindow(p.X + Bounds.X, p.Y + Bounds.Y);
        double x = at.X + 24, y = at.Y + 24;
        if (x + Lens > ActualWidth) x = at.X - 24 - Lens;
        if (y + Lens + 40 > ActualHeight) y = at.Y - 24 - Lens - 40;
        Canvas.SetLeft(_lens, x);
        Canvas.SetTop(_lens, y);
        Canvas.SetLeft(_label, x);
        Canvas.SetTop(_label, y + Lens + 6);
    }
}
