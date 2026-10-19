using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Ink;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Orbit.Island;

// Desenhar na tela (Ctrl+Alt+D): rabiscos sobre a tela congelada, com barra de ferramentas.
// Copiar põe a tela rabiscada no clipboard; iPhone manda pela caixa de saída. Esc fecha.
// ponytail: só traço livre (caneta/marca-texto); setas e formas se fizerem falta.
public sealed class DrawWindow : FrozenScreen
{
    private static readonly (string Nome, Color Cor)[] Palette =
    {
        ("Vermelho", Color.FromRgb(0xFF, 0x3B, 0x30)), ("Amarelo", Color.FromRgb(0xFF, 0xD6, 0x0A)),
        ("Verde", Color.FromRgb(0x34, 0xC7, 0x59)), ("Azul", Color.FromRgb(0x0A, 0x84, 0xFF)), ("Branco", Colors.White),
    };

    private readonly InkCanvas _ink = new() { Background = Brushes.Transparent, Cursor = Cursors.Pen };
    private readonly Grid _stage;
    private bool _highlighter;
    private Color _color = Palette[0].Cor;
    private Border? _penChip, _markChip, _eraseChip;

    // A tela rabiscada salva em PNG pra mandar pro iPhone (a ilha cuida do envio).
    public event Action<string>? SendToIPhone;

    public DrawWindow()
    {
        _ink.DefaultDrawingAttributes = new DrawingAttributes { FitToCurve = true };
        ApplyBrush();
        _stage = new Grid { Children = { new Image { Source = Shot, Stretch = Stretch.Fill }, _ink } };

        var bar = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var (nome, cor) in Palette)
        {
            var dot = new Border
            {
                Width = 22, Height = 22, CornerRadius = new CornerRadius(11), Background = new SolidColorBrush(cor),
                Margin = new Thickness(4, 0, 4, 0), Cursor = Cursors.Hand, ToolTip = nome, Tag = ButtonTag,
                BorderBrush = Brushes.White, BorderThickness = new Thickness(cor == _color ? 2 : 0),
            };
            dot.MouseLeftButtonUp += (_, e) =>
            {
                _color = cor;
                foreach (var d in bar.Children) if (d is Border b && b.Width == 22) b.BorderThickness = new Thickness(b == dot ? 2 : 0);
                _ink.EditingMode = InkCanvasEditingMode.Ink;
                ApplyBrush();
                e.Handled = true;
            };
            bar.Children.Add(dot);
        }
        bar.Children.Add(Separator());
        bar.Children.Add(_penChip = Chip("Caneta", () => { _highlighter = false; _ink.EditingMode = InkCanvasEditingMode.Ink; ApplyBrush(); }));
        bar.Children.Add(_markChip = Chip("Marca-texto", () => { _highlighter = true; _ink.EditingMode = InkCanvasEditingMode.Ink; ApplyBrush(); }));
        bar.Children.Add(_eraseChip = Chip("Borracha", () => { _ink.EditingMode = InkCanvasEditingMode.EraseByStroke; ApplyBrush(); }));
        bar.Children.Add(Chip("Desfazer", Undo));
        bar.Children.Add(Chip("Limpar", () => _ink.Strokes.Clear()));
        bar.Children.Add(Separator());
        bar.Children.Add(Chip("Copiar", () => Finish(img => Orbit.Services.ClipboardReader.SetImage(img))));
        bar.Children.Add(Chip("iPhone", () => Finish(img =>
        {
            var png = Path.Combine(Path.GetTempPath(), $"orbit-desenho-{Guid.NewGuid():N}.png");
            Task.Run(() => // PNG fora da UI thread (a imagem está congelada)
            {
                var enc = new PngBitmapEncoder();
                enc.Frames.Add(BitmapFrame.Create(img));
                using (var fs = File.Create(png)) enc.Save(fs);
            }).ContinueWith(_ => SendToIPhone?.Invoke(png), TaskScheduler.FromCurrentSynchronizationContext());
        })));
        bar.Children.Add(Chip("Fechar (Esc)", Close));

        var toolbar = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(235, 20, 20, 22)), CornerRadius = new CornerRadius(16),
            Padding = new Thickness(10, 8, 10, 8), Child = bar, Cursor = Cursors.SizeAll, ToolTip = "Arraste pra mover",
        };
        // Barra num Canvas: nasce centralizada no topo do monitor do mouse (centralizar na área de trabalho
        // inteira a deixava na divisa entre dois monitores) e pode ser arrastada pelo fundo.
        var layer = new Canvas { Children = { toolbar } };
        Content = new Grid { Children = { _stage, layer } };
        Loaded += (_, _) => PlaceOnCursorMonitor(toolbar);
        Point? grab = null;
        // Só o FUNDO da barra arrasta: capturar o mouse num clique de botão engolia o clique (nenhum botão funcionava).
        toolbar.MouseLeftButtonDown += (_, e) =>
        {
            if (IsButton(e.OriginalSource as DependencyObject, toolbar)) return;
            grab = e.GetPosition(toolbar);
            toolbar.CaptureMouse();
            e.Handled = true;
        };
        toolbar.MouseMove += (_, e) =>
        {
            if (grab is not { } g) return;
            var at = e.GetPosition(layer);
            Canvas.SetLeft(toolbar, at.X - g.X);
            Canvas.SetTop(toolbar, at.Y - g.Y);
        };
        toolbar.MouseLeftButtonUp += (_, _) => { grab = null; toolbar.ReleaseMouseCapture(); };
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Z && Keyboard.Modifiers == ModifierKeys.Control) { Undo(); e.Handled = true; }
        };
    }

    private void PlaceOnCursorMonitor(FrameworkElement toolbar)
    {
        toolbar.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var screen = System.Windows.Forms.Screen.FromPoint(System.Windows.Forms.Cursor.Position).Bounds; // pixels físicos
        var topLeft = ToWindow(screen.Left, screen.Top);
        var bottomRight = ToWindow(screen.Right, screen.Bottom);
        Canvas.SetLeft(toolbar, topLeft.X + (bottomRight.X - topLeft.X - toolbar.DesiredSize.Width) / 2);
        Canvas.SetTop(toolbar, topLeft.Y + 60);
    }

    private void Undo()
    {
        if (_ink.Strokes.Count > 0) _ink.Strokes.RemoveAt(_ink.Strokes.Count - 1);
    }

    private void ApplyBrush()
    {
        var a = _ink.DefaultDrawingAttributes;
        a.Color = _highlighter ? Color.FromArgb(110, _color.R, _color.G, _color.B) : _color;
        a.IsHighlighter = _highlighter;
        a.Width = a.Height = _highlighter ? 22 : 4;
        a.StylusTip = _highlighter ? StylusTip.Rectangle : StylusTip.Ellipse;
        bool erasing = _ink.EditingMode == InkCanvasEditingMode.EraseByStroke;
        Mark(_penChip, !erasing && !_highlighter);
        Mark(_markChip, !erasing && _highlighter);
        Mark(_eraseChip, erasing);
    }

    private static void Mark(Border? chip, bool on)
    {
        if (chip is not null) chip.Background = new SolidColorBrush(on ? Color.FromRgb(0x0A, 0x84, 0xFF) : Color.FromArgb(40, 255, 255, 255));
    }

    // Copiar/iPhone: a janela (que cobre todos os monitores por cima de tudo) some e FECHA antes de
    // entregar a imagem. Pôr imagem no clipboard pode demorar (o Windows converte formatos e o próprio
    // Orbit lê o clipboard): com a janela aberta isso prendia a tela inteira.
    private async void Finish(Action<BitmapSource> use)
    {
        var region = Region();
        Hide();
        await Dispatcher.Yield(DispatcherPriority.Background); // deixa o Windows redesenhar a tela de verdade
        BitmapSource? img = null;
        try { img = Render(region); }
        catch (Exception ex) { Serilog.Log.Warning(ex, "Desenho: não gerou a imagem"); }
        Close();
        if (img is null) return;
        try { use(img); }
        catch (Exception ex) { Serilog.Log.Warning(ex, "Desenho: não entregou a imagem"); }
    }

    // Monitor onde estão os rabiscos (sem rabisco, o do mouse), em pixels físicos relativos à foto.
    // A área de trabalho inteira (dois monitores, ~15 MP) era lenta e ninguém quer um print desse tamanho.
    private System.Drawing.Rectangle Region()
    {
        double sx = Bounds.Width / _stage.ActualWidth, sy = Bounds.Height / _stage.ActualHeight;
        System.Drawing.Point at;
        if (_ink.Strokes.Count > 0)
        {
            var b = _ink.Strokes.GetBounds();
            at = new System.Drawing.Point(Bounds.X + (int)((b.Left + b.Width / 2) * sx), Bounds.Y + (int)((b.Top + b.Height / 2) * sy));
        }
        else at = System.Windows.Forms.Cursor.Position;
        var screen = System.Windows.Forms.Screen.FromPoint(at).Bounds;
        return CropTo(screen, Bounds);
    }

    // Retângulo do monitor dentro da foto da área de trabalho (pixels relativos à foto). Puro, testável.
    public static System.Drawing.Rectangle CropTo(System.Drawing.Rectangle screen, System.Drawing.Rectangle virtualBounds)
    {
        var r = System.Drawing.Rectangle.Intersect(screen, virtualBounds);
        r.Offset(-virtualBounds.X, -virtualBounds.Y);
        return r;
    }

    // Tela + rabiscos de um recorte, na resolução física (a janela está em DIP; os traços são escalados).
    private BitmapSource Render(System.Drawing.Rectangle region)
    {
        double sx = Bounds.Width / _stage.ActualWidth, sy = Bounds.Height / _stage.ActualHeight;
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.PushTransform(new TranslateTransform(-region.X, -region.Y));
            dc.DrawImage(Shot, new Rect(0, 0, Bounds.Width, Bounds.Height));
            dc.PushTransform(new ScaleTransform(sx, sy));
            _ink.Strokes.Draw(dc);
            dc.Pop();
            dc.Pop();
        }
        var rtb = new RenderTargetBitmap(region.Width, region.Height, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(visual);
        rtb.Freeze();
        return rtb;
    }

    private const string ButtonTag = "botão";

    // Sobe da origem do clique até a barra procurando um botão (chip ou bolinha de cor).
    private static bool IsButton(DependencyObject? from, DependencyObject toolbar)
    {
        for (var d = from; d is not null && d != toolbar; d = VisualTreeHelper.GetParent(d))
            if (d is FrameworkElement { Tag: ButtonTag }) return true;
        return false;
    }

    private static Border Chip(string text, Action onClick)
    {
        var chip = new Border
        {
            Tag = ButtonTag,
            Background = new SolidColorBrush(Color.FromArgb(40, 255, 255, 255)), CornerRadius = new CornerRadius(10),
            Padding = new Thickness(12, 6, 12, 6), Margin = new Thickness(4, 0, 4, 0), Cursor = Cursors.Hand,
            Child = new TextBlock { Text = text, Foreground = Brushes.White, FontSize = 13, FontWeight = FontWeights.SemiBold },
        };
        chip.MouseLeftButtonUp += (_, e) => { onClick(); e.Handled = true; };
        return chip;
    }

    private static Border Separator() =>
        new() { Width = 1, Margin = new Thickness(8, 2, 8, 2), Background = new SolidColorBrush(Color.FromArgb(60, 255, 255, 255)) };
}
