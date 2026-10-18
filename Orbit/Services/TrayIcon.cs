using System;
using System.Drawing;
using System.Windows.Forms;

namespace Orbit.Services;

// Wrapper do NotifyIcon + menu. Callbacks são ligados pela IslandWindow.
public sealed class TrayIcon : IDisposable
{
    private readonly NotifyIcon _icon;
    private readonly ContextMenuStrip _menu;
    private readonly ToolStripMenuItem _autostartItem;

    public event Action? ToggleVisibility;
    public event Action? SettingsRequested;
    public event Action? ExitRequested;
    public event Action? SendClipboardToIPhone;
    public event Action? UpdateRequested;
#if DEBUG
    public event Action? DebugPeek;
    public event Action? DebugActivity;
#endif

    public TrayIcon()
    {
        // Click (e não CheckedChanged): setar Checked por código não deve gravar no registro.
        _autostartItem = new ToolStripMenuItem("Iniciar com o Windows") { CheckOnClick = true };
        _autostartItem.Click += (_, _) => Autostart.Set(_autostartItem.Checked);

        _menu = new ContextMenuStrip();
        _menu.Items.Add("Mostrar/Ocultar ilha", null, (_, _) => ToggleVisibility?.Invoke());
        _menu.Items.Add("Ajustes", null, (_, _) => SettingsRequested?.Invoke());
        _menu.Items.Add("Mandar clipboard pro iPhone (Ctrl+Alt+I)", null, (_, _) => SendClipboardToIPhone?.Invoke());
        _menu.Items.Add("Atualizar Orbit (do código)", null, (_, _) => UpdateRequested?.Invoke());
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(_autostartItem);
#if DEBUG
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add("Debug: disparar alerta", null, (_, _) => DebugPeek?.Invoke());
        _menu.Items.Add("Debug: ligar/desligar atividade", null, (_, _) => DebugActivity?.Invoke());
#endif
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add("Sair", null, (_, _) => ExitRequested?.Invoke());
        _menu.Opening += (_, _) => _autostartItem.Checked = Autostart.IsEnabled();

        _icon = new NotifyIcon
        {
            Icon = LoadAppIcon(),
            Text = "Orbit 2",
            Visible = true,
            ContextMenuStrip = _menu,
        };
        _icon.DoubleClick += (_, _) => ToggleVisibility?.Invoke();
    }

    // Carrega o orbit.ico embutido como Resource; cai no ícone do sistema se faltar.
    private static Icon LoadAppIcon()
    {
        try
        {
            var uri = new Uri("pack://application:,,,/Assets/orbit.ico");
            using var stream = System.Windows.Application.GetResourceStream(uri).Stream;
            return new Icon(stream, SystemInformation.SmallIconSize);
        }
        catch
        {
            return SystemIcons.Application;
        }
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
        _menu.Dispose();
    }
}
