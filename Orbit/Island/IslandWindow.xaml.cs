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
    private readonly DispatcherTimer _trim = new() { Interval = TimeSpan.FromSeconds(2) };
    private GlobalHotKey? _hotKey;
    private FullscreenWatcher? _fullscreen;
    private ClipboardWatcher? _clipboard;
    private bool _userHidden;
    private bool _exiting;
    private bool _released;
    private int _layoutGen;
    private IslandMode _lastMode = IslandMode.Repouso;
    private IntPtr _prevFg;
    private WinEventProc? _foregroundHook; // campo: o delegate não pode ser coletado enquanto o hook vive
    private IntPtr _foregroundHookHandle;
#if DEBUG
    private bool _debugActivity;
#endif

    public IslandWindow(IslandViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
        vm.MonitorChanged += Recenter;

        _hover.Tick += (_, _) => { _hover.Stop(); _vm.Expand(); };
        _leave.Tick += (_, _) => { _leave.Stop(); _vm.Collapse(); };
        _peek.Tick += (_, _) => { _peek.Stop(); _vm.PeekExpired(); };
        // Parada, a ilha quase não aloca, então o GC nunca roda sozinho e o que as abas carregaram
        // (views, capas, contadores, JSON) fica retido. Uma coleta agressiva após recolher devolve
        // isso ao Windows (~100 MB → de volta perto do repouso). Custo: alguns ms, uma vez.
        // ponytail: GC manual; trocar por limite de heap (GCHeapHardLimit) se um dia incomodar.
        _trim.Tick += (_, _) =>
        {
            _trim.Stop();
            if (_vm.Mode != IslandMode.Expandida) GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        };
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
        _vm.Clips.PasteRequested += PasteIntoPrevious;
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
        Recenter(); // antes do Show: já nasce no monitor certo

        // Último app em primeiro plano que não é a ilha (alvo do "colar" e de quem recebe o foco de
        // volta ao recolher). WM_ACTIVATE não serve: o lParam vem nulo quando o app anterior é de
        // outro processo. EVENT_SYSTEM_FOREGROUND é por evento (zero polling).
        var fg = GetForegroundWindow();
        if (fg != hwnd) _prevFg = fg;
        _foregroundHook = (_, _, window, _, _, _, _) =>
        {
            if (window != IntPtr.Zero && window != hwnd) _prevFg = window;
        };
        _foregroundHookHandle = SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND, IntPtr.Zero,
            _foregroundHook, 0, 0, WINEVENT_OUTOFCONTEXT);

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

        _clipboard = new ClipboardWatcher(hwnd);
        _clipboard.Changed += () => _ = _vm.Clips.CaptureAsync();
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
            _clipboard?.Dispose();
            if (_foregroundHookHandle != IntPtr.Zero) UnhookWinEvent(_foregroundHookHandle);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Falha ao liberar recursos da ilha");
        }
    }

    // Cola no app que estava em foco antes da ilha: recolhe (OnModeChanged devolve o foco pro
    // _prevFg) e, se ele de fato voltou ao primeiro plano, manda Ctrl+V.
    private void PasteIntoPrevious()
    {
        var target = _prevFg;
        Log.Information("Colar: alvo {Alvo:X}, ilha ativa {Ativa}", target.ToInt64(), IsActive);
        _vm.Collapse();
        if (target == IntPtr.Zero) return;
        var wait = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        wait.Tick += (_, _) =>
        {
            wait.Stop();
            var fg = GetForegroundWindow();
            if (fg != target)
            {
                Log.Warning("Colar: o foco não voltou pro alvo (primeiro plano {Fg:X})", fg.ToInt64());
                return;
            }
            uint sent = Input.SendCtrlV();
            Log.Information("Colar: Ctrl+V enviado ({Enviados}/4 eventos)", sent);
        };
        wait.Start();
    }

    // Monitor conectado/desconectado, resolução ou escala mudou: reposiciona (o monitor salvo
    // some → principal; volta → ele de novo).
    private void OnDisplaySettingsChanged(object? sender, EventArgs e) => Dispatcher.BeginInvoke(Recenter);

    // Topo central do monitor escolhido em Ajustes, em pixels físicos (processo PerMonitorV2).
    private void Recenter()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;
        var screens = System.Windows.Forms.Screen.AllScreens;
        var b = screens[IslandGeometry.PickScreen(Array.ConvertAll(screens, s => s.DeviceName),
            Array.FindIndex(screens, s => s.Primary), _vm.MonitorDevice)].Bounds;
        var monitor = MonitorFromPoint(new POINT { X = b.X + 1, Y = b.Y + 1 }, MONITOR_DEFAULTTONEAREST);
        double scale = GetDpiForMonitor(monitor, 0, out uint dpi, out _) == 0 ? dpi / 96.0 : 1;
        // 1º leva a janela pro monitor (o WPF reescala no WM_DPICHANGED), depois crava o retângulo.
        SetWindowPos(hwnd, IntPtr.Zero, b.X, b.Y, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
        var r = IslandGeometry.PlaceOn(b.X, b.Y, b.Width, scale);
        SetWindowPos(hwnd, IntPtr.Zero, r.X, r.Y, r.W, r.H, SWP_NOZORDER | SWP_NOACTIVATE);
    }

    private void OnModeChanged()
    {
        var mode = _vm.Mode;
        // Campo de texto (UpdateSourceTrigger=LostFocus) ainda com foco ao recolher: grava agora,
        // senão desativar a janela por Esc/clique fora perderia o valor digitado.
        if (mode != IslandMode.Expandida && FocusManager.GetFocusedElement(this) is TextBox tb)
            tb.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
        if (mode == IslandMode.Alerta && !_userHidden) _peek.Start();
        else _peek.Stop();
        UpdateVisibility();
        // Devolve o teclado pra quem estava digitando antes da hotkey/clique.
        if (mode != IslandMode.Expandida && IsActive && _prevFg != IntPtr.Zero) SetForegroundWindow(_prevFg);
        ApplyLayout(mode, animate: true);
        if (mode == IslandMode.Expandida) _trim.Stop();
        else if (_lastMode == IslandMode.Expandida) _trim.Start(); // depois da animação de recolher
        _lastMode = mode;
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

    // Arquivo arrastado pra pílula: expande (vira alvo grande) e, ao soltar, manda pro iPhone.
    // Preview*: nenhum campo do painel engole o arquivo. DragLeave dispara a cada filho cruzado,
    // então só agenda o recolher; o próximo DragEnter cancela.
    private void OnPillDragEnter(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        e.Effects = DragDropEffects.Copy;
        e.Handled = true;
        _leave.Stop();
        DropHint.Visibility = Visibility.Visible;
        if (_vm.Mode != IslandMode.Expandida) _vm.Expand();
    }

    private void OnPillDragOver(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        e.Effects = DragDropEffects.Copy;
        e.Handled = true;
    }

    private void OnPillDragLeave(object sender, DragEventArgs e)
    {
        DropHint.Visibility = Visibility.Collapsed;
        _leave.Start();
    }

    private void OnPillDrop(object sender, DragEventArgs e)
    {
        DropHint.Visibility = Visibility.Collapsed;
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] paths) return;
        e.Handled = true;
        _vm.Collapse();
        _vm.SendFilesToIPhone(SendToIPhone.FilesFrom(paths));
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) _vm.Collapse();
    }

    private void OnDeactivated(object? sender, EventArgs e) => _vm.Collapse();

    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();

    private const uint EVENT_SYSTEM_FOREGROUND = 0x0003, WINEVENT_OUTOFCONTEXT = 0x0000;
    private delegate void WinEventProc(IntPtr hook, uint ev, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);
    [DllImport("user32.dll")]
    private static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr hmod, WinEventProc proc,
        uint idProcess, uint idThread, uint flags);
    [DllImport("user32.dll")] private static extern bool UnhookWinEvent(IntPtr hook);
    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hWnd, int nIndex);
    [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    private const uint SWP_NOSIZE = 0x1, SWP_NOZORDER = 0x4, SWP_NOACTIVATE = 0x10, MONITOR_DEFAULTTONEAREST = 2;
    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromPoint(POINT pt, uint flags);
    [DllImport("shcore.dll")] private static extern int GetDpiForMonitor(IntPtr monitor, int type, out uint dpiX, out uint dpiY);
}
