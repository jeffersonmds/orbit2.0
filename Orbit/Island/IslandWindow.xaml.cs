using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Microsoft.Win32;
using Orbit.Services;
using Serilog;

namespace Orbit.Island;

public partial class IslandWindow : Window
{
    private readonly IslandViewModel _vm;
    private readonly TrayIcon _tray;
    private readonly DispatcherTimer _hover = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private readonly DispatcherTimer _leave = new() { Interval = TimeSpan.FromMilliseconds(600) };
    private readonly DispatcherTimer _peek = new() { Interval = TimeSpan.FromSeconds(4) };
    private GlobalHotKey? _hotKey;
    private FullscreenWatcher? _fullscreen;
    private bool _userHidden;
    private bool _exiting;
    private bool _released;
    private int _layoutGen;
    private IntPtr _prevFg;
#if DEBUG
    private bool _debugActivity;
#endif

    public IslandWindow(IslandViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;
        Top = 0;
        Recenter();
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;

        _hover.Tick += (_, _) => { _hover.Stop(); _vm.Expand(); };
        _leave.Tick += (_, _) => { _leave.Stop(); _vm.Collapse(); };
        _peek.Tick += (_, _) => { _peek.Stop(); _vm.PeekExpired(); };
        _vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(IslandViewModel.Mode)) OnModeChanged();
            // Próximo alerta da fila: o modo continua Alerta, então reinicia o timer aqui.
            else if (e.PropertyName == nameof(IslandViewModel.Peek) && _vm.Mode == IslandMode.Alerta && !_userHidden)
            {
                _peek.Stop();
                _peek.Start();
            }
        };

        _tray = new TrayIcon();
        _tray.ToggleVisibility += () =>
        {
            _userHidden = !_userHidden;
            UpdateVisibility();
            // Alerta só conta tempo visível: escondida, ele espera; ao mostrar, começa a contar.
            if (_userHidden) _peek.Stop();
            else if (_vm.Mode == IslandMode.Alerta) _peek.Start();
        };
        _tray.SettingsRequested += () =>
        {
            if (_vm.Mode == IslandMode.Oculta) return; // não rouba o foco do jogo/vídeo em tela cheia
            _userHidden = false; UpdateVisibility(); _vm.OpenTab("ajustes");
            Activate();
        };
        _tray.ExitRequested += () => { _exiting = true; Application.Current.Shutdown(); };
#if DEBUG
        _tray.DebugPeek += () => _vm.RaisePeek(new Peek("Alerta de teste", "github.com/… · abrindo no navegador", "energia"));
        _tray.DebugActivity += () =>
        {
            _debugActivity = !_debugActivity;
            _vm.SetActivity(ActivityKind.Midia, _debugActivity ? "▶  lofi beats · lofi radio" : null);
        };
#endif

        ApplyLayout(_vm.Mode, animate: false);
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = new WindowInteropHelper(this).Handle;

        // WS_EX_TOOLWINDOW: fora do Alt+Tab.
        SetWindowLong(hwnd, GWL_EXSTYLE, GetWindowLong(hwnd, GWL_EXSTYLE) | WS_EX_TOOLWINDOW);

        HwndSource.FromHwnd(hwnd)!.AddHook((IntPtr h, int msg, IntPtr wParam, IntPtr lParam, ref bool handled) =>
        {
            const int WM_ACTIVATE = 0x0006;
            if (msg == WM_ACTIVATE && (wParam.ToInt64() & 0xFFFF) != 0 && lParam != IntPtr.Zero) _prevFg = lParam;
            return IntPtr.Zero;
        });

        _hotKey = new GlobalHotKey(hwnd);
        _hotKey.Pressed += () =>
        {
            // Oculta = app em tela cheia: Activate() tiraria o foco do jogo.
            if (_userHidden || _vm.Mode == IslandMode.Oculta) return;
            if (_vm.Mode == IslandMode.Expandida) _vm.Collapse();
            else ExpandFocused();
        };

        _fullscreen = new FullscreenWatcher(hwnd);
        _fullscreen.Changed += on => _vm.SetFullscreen(on);
    }

    // Em Closing (HWND ainda vivo): ABM_REMOVE e UnregisterHotKey precisam do handle válido.
    protected override void OnClosing(CancelEventArgs e)
    {
        // Só sai pela bandeja; Alt+F4 na ilha ativa é ignorado.
        if (!_exiting) { e.Cancel = true; return; }
        ReleaseResources();
        base.OnClosing(e);
    }

    // Idempotente. Chamado no "Sair" (OnClosing) e pelo App (OnExit e falha no startup) —
    // cobre encerramentos que não passam pela bandeja, senão o ícone fica fantasma.
    public void ReleaseResources()
    {
        if (_released) return;
        _released = true;
        try
        {
            _tray.Dispose(); // primeiro: se os outros lançarem, o ícone não fica fantasma
            SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged; // evento estático: vaza se não soltar
            _hotKey?.Dispose();
            _fullscreen?.Dispose();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Falha ao liberar recursos da ilha");
        }
    }

    // Resolução/DPI/monitor principal mudou: recentraliza no topo do principal.
    private void OnDisplaySettingsChanged(object? sender, EventArgs e) => Dispatcher.BeginInvoke(Recenter);

    private void Recenter() => Left = IslandGeometry.LeftFor(SystemParameters.PrimaryScreenWidth);

    private void OnModeChanged()
    {
        var mode = _vm.Mode;
        if (mode == IslandMode.Alerta && !_userHidden) _peek.Start();
        else _peek.Stop();
        UpdateVisibility();
        // Devolve o teclado pra quem estava digitando antes da hotkey/clique.
        if (mode != IslandMode.Expandida && IsActive && _prevFg != IntPtr.Zero) SetForegroundWindow(_prevFg);
        ApplyLayout(mode, animate: true);
    }

    // Abertura intencional (hotkey/clique) pega o foco pra Esc e "clique fora" funcionarem.
    // Hover NÃO ativa: encostar o mouse no topo não pode roubar o foco de quem está digitando.
    private void ExpandFocused()
    {
        _vm.Expand();
        Activate();
    }

    private void UpdateVisibility() =>
        Visibility = _userHidden || _vm.Mode == IslandMode.Oculta ? Visibility.Hidden : Visibility.Visible;

    // Anima a pílula até o tamanho do estado e só então revela a camada de conteúdo.
    private void ApplyLayout(IslandMode mode, bool animate)
    {
        int gen = ++_layoutGen;
        var size = IslandGeometry.SizeFor(mode);
        var r = IslandGeometry.RadiusFor(mode);
        Pill.CornerRadius = new CornerRadius(0, 0, r, r);

        ActivityLayer.Visibility = Visibility.Collapsed;
        PeekLayer.Visibility = Visibility.Collapsed;
        ExpandedLayer.Visibility = Visibility.Collapsed;
        UIElement? layer = mode switch
        {
            IslandMode.Atividade => ActivityLayer,
            IslandMode.Alerta => PeekLayer,
            IslandMode.Expandida => ExpandedLayer,
            _ => null,
        };

        if (!animate)
        {
            Pill.BeginAnimation(WidthProperty, null);
            Pill.BeginAnimation(HeightProperty, null);
            Pill.Width = size.Width;
            Pill.Height = size.Height;
            if (layer is not null) layer.Visibility = Visibility.Visible;
            return;
        }

        bool growing = size.Width * size.Height > Pill.ActualWidth * Pill.ActualHeight;
        IEasingFunction ease = growing
            ? new BackEase { Amplitude = 0.3, EasingMode = EasingMode.EaseOut }
            : new CubicEase { EasingMode = EasingMode.EaseInOut };
        var duration = TimeSpan.FromMilliseconds(growing ? 320 : 220);

        var w = new DoubleAnimation(size.Width, duration) { EasingFunction = ease };
        var h = new DoubleAnimation(size.Height, duration) { EasingFunction = ease };
        if (layer is not null)
            h.Completed += (_, _) => { if (gen == _layoutGen) FadeIn(layer); };

        Pill.BeginAnimation(WidthProperty, w);
        Pill.BeginAnimation(HeightProperty, h);
    }

    private static void FadeIn(UIElement element)
    {
        element.Visibility = Visibility.Visible;
        element.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(140)));
    }

    private void OnPillEnter(object sender, MouseEventArgs e)
    {
        _leave.Stop();
        if (_vm.Mode is IslandMode.Repouso or IslandMode.Atividade) _hover.Start();
    }

    private void OnPillLeave(object sender, MouseEventArgs e)
    {
        _hover.Stop();
        // Digitando num campo: só recolhe por Esc/clique fora/hotkey, não pela saída do mouse.
        if (_vm.Mode == IslandMode.Expandida && Keyboard.FocusedElement is not TextBox) _leave.Start();
    }

    private void OnPillWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Delta >= 0 || _vm.Mode == IslandMode.Expandida) return;
        _hover.Stop();
        _vm.Expand();
    }

    private void OnPillClick(object sender, MouseButtonEventArgs e)
    {
        if (_vm.Mode == IslandMode.Alerta) { _vm.OpenPeekTab(); Activate(); }
        else if (_vm.Mode != IslandMode.Expandida) ExpandFocused();
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) _vm.Collapse();
    }

    private void OnDeactivated(object? sender, EventArgs e) => _vm.Collapse();

    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hWnd, int nIndex);
    [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
}
