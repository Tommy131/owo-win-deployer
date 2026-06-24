using System.Drawing;
using WinDeploy.Core.I18n;
using WF = System.Windows.Forms;

namespace WinDeploy.App.Services.Infra;

/// <summary>A node in the tray context menu: a clickable item, a submenu (<see cref="Children"/>), or a
/// separator (null <see cref="Text"/>). The host rebuilds these each time the menu opens, so dynamic sections
/// (live terminal sessions, installed services, FTP status) always reflect the current state.</summary>
public sealed class TrayMenuItem
{
    public string? Text { get; init; }
    public Action? OnClick { get; init; }
    public bool Enabled { get; init; } = true;
    public IReadOnlyList<TrayMenuItem>? Children { get; init; }

    public static TrayMenuItem Sep { get; } = new();
    public static TrayMenuItem Item(string text, Action onClick, bool enabled = true)
        => new() { Text = text, OnClick = onClick, Enabled = enabled };
    public static TrayMenuItem Disabled(string text)
        => new() { Text = text, Enabled = false };
    public static TrayMenuItem Sub(string text, IReadOnlyList<TrayMenuItem> children, bool enabled = true)
        => new() { Text = text, Children = children, Enabled = enabled };
}

/// <summary>A thin wrapper over <see cref="WF.NotifyIcon"/> for background-resident mode: a system-tray icon
/// with a context menu and double-click to restore. The menu's middle (dynamic) section is rebuilt on every
/// open from <c>buildDynamic</c>; the fixed 打开主界面 / 退出 entries bracket it. Created lazily on first use.</summary>
public sealed class TrayIcon : IDisposable
{
    private readonly WF.NotifyIcon _icon;
    private readonly Action _onOpen;
    private readonly Action _onExit;
    private readonly Func<IReadOnlyList<TrayMenuItem>>? _buildDynamic;
    private bool _tipShown;

    public TrayIcon(string tooltip, Action onOpen, Action onExit, Func<IReadOnlyList<TrayMenuItem>>? buildDynamic = null)
    {
        _onOpen = onOpen;
        _onExit = onExit;
        _buildDynamic = buildDynamic;

        var menu = new WF.ContextMenuStrip();
        menu.Opening += (_, _) => Rebuild(menu);
        Rebuild(menu);   // initial fill (before the first Opening)

        _icon = new WF.NotifyIcon
        {
            Text = tooltip.Length > 63 ? tooltip[..63] : tooltip,   // NotifyIcon.Text caps at 63 chars
            Icon = LoadIcon(),
            Visible = false,
            ContextMenuStrip = menu,
        };
        _icon.DoubleClick += (_, _) => onOpen();
    }

    /// <summary>Re-create every menu item so live sections (sessions / services / FTP) reflect current state.</summary>
    private void Rebuild(WF.ContextMenuStrip menu)
    {
        menu.Items.Clear();
        menu.Items.Add(Localizer.T("tray.open"), null, (_, _) => _onOpen());

        if (_buildDynamic != null)
        {
            IReadOnlyList<TrayMenuItem> dyn;
            try { dyn = _buildDynamic(); }
            catch { dyn = Array.Empty<TrayMenuItem>(); }
            if (dyn.Count > 0)
            {
                menu.Items.Add(new WF.ToolStripSeparator());
                foreach (var it in dyn) menu.Items.Add(Convert(it));
            }
        }

        menu.Items.Add(new WF.ToolStripSeparator());
        menu.Items.Add(Localizer.T("tray.exit"), null, (_, _) => _onExit());
    }

    private static WF.ToolStripItem Convert(TrayMenuItem m)
    {
        if (m.Text == null) return new WF.ToolStripSeparator();
        var item = new WF.ToolStripMenuItem(m.Text) { Enabled = m.Enabled };
        if (m.Children != null)
            foreach (var c in m.Children) item.DropDownItems.Add(Convert(c));
        if (m.OnClick != null)
        {
            var onClick = m.OnClick;
            item.Click += (_, _) => onClick();
        }
        return item;
    }

    private static Icon LoadIcon()
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(exe))
            {
                var ico = Icon.ExtractAssociatedIcon(exe);
                if (ico != null) return ico;
            }
        }
        catch { /* fall through */ }
        return SystemIcons.Application;
    }

    /// <summary>Show the tray icon; pops a one-time hint the first time. Prefers a modern toast (correctly
    /// shows the app name + icon); falls back to the legacy NotifyIcon balloon if toasts are unavailable.</summary>
    public void Show()
    {
        _icon.Visible = true;
        if (_tipShown) return;
        _tipShown = true;

        var title = Localizer.T("tray.minimizedTitle");
        var body = Localizer.T("tray.minimizedBody");
        if (ToastService.TryShow(title, body)) return;

        try
        {
            _icon.BalloonTipTitle = title;
            _icon.BalloonTipText = body;
            _icon.ShowBalloonTip(3000);
        }
        catch { /* balloons can be suppressed by policy */ }
    }

    public void Hide() => _icon.Visible = false;

    public void Dispose()
    {
        try { _icon.Visible = false; _icon.Dispose(); } catch { /* ignore */ }
    }
}
