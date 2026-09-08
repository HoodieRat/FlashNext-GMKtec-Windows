using System.Drawing;
using System.IO;
using System.Windows.Forms;
using System.ComponentModel;

namespace FlashNext.Dashboard;

public sealed class TrayService : IDisposable
{
    private readonly NotifyIcon _icon;
    private readonly DashboardSession _session;
    private readonly ToolStripMenuItem _statusItem;
    private readonly ToolStripMenuItem _speedItem;
    private readonly Action _statusChanged;
    private readonly PropertyChangedEventHandler _propertyChanged;

    public TrayService(DashboardSession session, Action show, Action quit)
    {
        _session = session;
        ContextMenuStrip menu = new()
        {
            BackColor = Color.FromArgb(18, 18, 28),
            ForeColor = Color.FromArgb(244, 244, 250),
            ShowImageMargin = false,
            Padding = new Padding(6)
        };
        _icon = new NotifyIcon
        {
            Visible = true,
            Text = "FlashNext",
            Icon = LoadAppIcon(),
            ContextMenuStrip = menu
        };
        _statusItem = new ToolStripMenuItem { Enabled = false, Font = new Font("Segoe UI Semibold", 10f), Margin = new Padding(0, 2, 0, 2) };
        _speedItem = new ToolStripMenuItem { Enabled = false, ForeColor = Color.FromArgb(0, 234, 255) };
        menu.Items.Add(_statusItem);
        menu.Items.Add(_speedItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Open FlashNext Dashboard", null, (_, _) => show());
        menu.Items.Add("Start AI server", null, async (_, _) => await session.StartServerAsync());
        menu.Items.Add("Stop AI server", null, async (_, _) => await session.StopServerAsync());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Quit FlashNext", null, (_, _) => quit());
        _icon.DoubleClick += (_, _) => show();
        _statusChanged = UpdateStatus;
        _propertyChanged = (_, args) =>
        {
            if (args.PropertyName is nameof(DashboardSession.LiveTpsText) or nameof(DashboardSession.LiveTpsState)) UpdateStatus();
        };
        session.StatusChanged += _statusChanged;
        session.PropertyChanged += _propertyChanged;
        UpdateStatus();
    }

    public void Dispose()
    {
        _session.StatusChanged -= _statusChanged;
        _session.PropertyChanged -= _propertyChanged;
        _icon.Visible = false;
        _icon.ContextMenuStrip?.Dispose();
        _icon.Dispose();
    }

    private void UpdateStatus()
    {
        string state = _session.ServerState.ToUpperInvariant();
        _statusItem.Text = $"FLASHNEXT  •  {state}";
        _speedItem.Text = $"Speed  {_session.LiveTpsText} tok/s  •  {_session.LiveTpsState}";
        string tip = $"FlashNext • {_session.ServerState} • {_session.LiveTpsText} tok/s";
        _icon.Text = tip.Length <= 63 ? tip : tip[..63];
    }

    private static Icon LoadAppIcon()
    {
        string? process = Environment.ProcessPath;
        if (!string.IsNullOrWhiteSpace(process))
        {
            Icon? associated = Icon.ExtractAssociatedIcon(process);
            if (associated is not null) return associated;
        }
        string nextToExe = Path.Combine(AppContext.BaseDirectory, "flashnext.ico");
        if (File.Exists(nextToExe)) return new Icon(nextToExe);
        return SystemIcons.Application;
    }
}
