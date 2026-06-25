using System.ComponentModel;
using System.Windows;
using WinDeploy.App.Services;
using WinDeploy.App.ViewModels;
using WinDeploy.App.Views;
using WinDeploy.Core.I18n;

namespace WinDeploy.App;

public partial class MainWindow : Window
{
    private TrayIcon? _tray;
    private bool _exiting;    // set when a real shutdown is in progress
    private bool _resident;   // tray icon stays visible at all times (设置 → 始终在系统托盘显示常驻图标)

    public MainWindow()
    {
        InitializeComponent();
        var vm = new MainViewModel();
        DataContext = vm;
        // Window / taskbar icon comes from the embedded ApplicationIcon (app.ico) — same as the .exe file icon.
        // Native title bar gets its handle only at SourceInitialized; theme it then.
        SourceInitialized += (_, _) => ThemeManager.ApplyTitleBar(this);
        // Always-resident tray icon: react to the setting live, and apply the persisted choice once shown.
        vm.Settings.AlwaysShowTrayChanged += SetResidentTray;
        Loaded += (_, _) => { if (SettingsStore.Load().AlwaysShowTray) ApplyResidentTray(true, announce: false); };
        // Returning to the app while a device keeps overheating → show the advanced ignore/adjust prompt.
        Activated += (_, _) => (DataContext as MainViewModel)?.ShowOverheatPromptIfPending();
        Closing += OnClosing;
        Closed += (_, _) =>
        {
            _tray?.Dispose();
            (DataContext as MainViewModel)?.Terminal.Dispose();
            (DataContext as MainViewModel)?.Ftp.Shutdown();
            (DataContext as MainViewModel)?.Cloudflare.Shutdown();
        };
    }

    private void EnsureTray()
        => _tray ??= new TrayIcon(WinDeploy.App.AppInfo.TitleWithVersion, RestoreFromTray, ExitFromTray, BuildTrayMenu);

    /// <summary>Live toggle from 设置 → 始终在系统托盘显示常驻图标. Announces (one-off notification) so the user
    /// can find the freshly-added icon — Windows 11 tucks it into the "^" overflow.</summary>
    public void SetResidentTray(bool on) => ApplyResidentTray(on, announce: true);

    /// <summary>Apply the always-resident tray state. When on, the icon shows even while the main window is open;
    /// when off, it's hidden again — unless the window is currently minimized to tray (then the icon must stay,
    /// or the user couldn't get back). <paramref name="announce"/> pops a confirming notification (only on the
    /// user's explicit toggle, not on every startup).</summary>
    private void ApplyResidentTray(bool on, bool announce)
    {
        _resident = on;
        if (on)
        {
            EnsureTray();
            _tray!.Show(hint: false);
            if (announce) _tray.Notify(Localizer.T("tray.resident.title"), Localizer.T("tray.resident.body"));
            if (DataContext is MainViewModel vm) _ = vm.RefreshWebServiceStatusAsync(force: true);
        }
        else if (IsVisible)
        {
            _tray?.Hide();
        }
    }

    /// <summary>Close-button behavior: ask (default) → prompt; tray → minimize to tray; exit → really quit.</summary>
    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_exiting) return;   // exit already chosen — let it close

        var action = SettingsStore.Load().CloseAction ?? "ask";

        if (action == "exit") { _exiting = true; return; }
        if (action == "tray") { e.Cancel = true; HideToTray(); return; }

        // ask
        var dlg = new CloseChoiceDialog { Owner = this };
        var ok = dlg.ShowDialog();
        if (ok != true || dlg.Choice == CloseChoice.Cancel) { e.Cancel = true; return; }

        if (dlg.Remember) SettingsStore.SetCloseAction(dlg.Choice == CloseChoice.Tray ? "tray" : "exit");

        if (dlg.Choice == CloseChoice.Tray) { e.Cancel = true; HideToTray(); }
        else _exiting = true;   // CloseChoice.Exit → allow close
    }

    private void HideToTray()
    {
        EnsureTray();
        _tray!.Show();
        Hide();
        // Prime the web-server status cache so the first tray right-click already shows live状态.
        if (DataContext is MainViewModel vm) _ = vm.RefreshWebServiceStatusAsync(force: true);
        AuditLog.App("已最小化到后台常驻（系统托盘）");
    }

    // ── tray context menu ────────────────────────────────────────────────────────
    /// <summary>Build the tray menu's dynamic section, rebuilt each time it opens so live state (terminal
    /// sessions, installed Web servers, FTP status) is always current.</summary>
    private IReadOnlyList<TrayMenuItem> BuildTrayMenu()
    {
        var items = new List<TrayMenuItem>();
        if (DataContext is not MainViewModel vm) return items;

        // System-affecting entries (终端 / 管理服务 / Cloudflare DDNS / 环境变量设置) are shown ONLY in 开发人员模式,
        // so a non-developer can't reach them from the tray and accidentally damage the system. Read live on
        // every open — toggling developer mode in 设置 immediately changes what this menu offers.
        if (vm.IsDeveloperMode)
        {
            items.Add(BuildTerminalMenu(vm));
            items.Add(BuildWebServicesMenu(vm));
            items.Add(BuildFtpMenu(vm));
            items.Add(BuildCloudflareMenu(vm));
            items.Add(TrayMenuItem.Sep);
        }

        items.Add(TrayMenuItem.Item("打开日志", () => RestoreAndNavigate(vm.GoToLogs)));
        items.Add(TrayMenuItem.Item("打开设置", () => RestoreAndNavigate(vm.GoToSettings)));
        if (vm.IsDeveloperMode)
            items.Add(TrayMenuItem.Item("环境变量设置", OpenSystemEnvVars));
        items.Add(TrayMenuItem.Item("系统概览", () => RestoreAndNavigate(vm.GoToSystemOverview)));
        return items;
    }

    /// <summary>Cloudflare DDNS：open the management page, see the resident monitor's status / current IP, and
    /// start / stop / trigger it without leaving the tray. 开发人员模式 only.</summary>
    private TrayMenuItem BuildCloudflareMenu(MainViewModel vm)
    {
        var cf = vm.Cloudflare;
        var running = cf.MonitorRunning;
        var children = new List<TrayMenuItem>
        {
            TrayMenuItem.Item("打开 DDNS 管理", () => RestoreAndNavigate(vm.GoToCloudflare)),
            TrayMenuItem.Sep,
            TrayMenuItem.Disabled("状态：" + cf.TrayStatusLine),
            TrayMenuItem.Item("启动监听", cf.StartMonitor, enabled: !running),
            TrayMenuItem.Item("停止监听", cf.StopMonitor, enabled: running),
            TrayMenuItem.Item("立即检查并更新", cf.RunOnceFromTray),
        };
        return TrayMenuItem.Sub("Cloudflare DDNS" + (running ? "（监听中）" : ""), children);
    }

    /// <summary>终端：quick-open the page, start a new session (per shell), or jump back to a live session.</summary>
    private TrayMenuItem BuildTerminalMenu(MainViewModel vm)
    {
        var children = new List<TrayMenuItem>
        {
            TrayMenuItem.Item("打开终端页面", () => RestoreAndNavigate(vm.GoToTerminal)),
        };

        var shells = vm.Terminal.AvailableShells;
        if (shells.Count > 0)
        {
            var shellItems = shells.Select(s => TrayMenuItem.Item(s.Name, () =>
            {
                vm.Terminal.NewSessionCommand.Execute(s);
                RestoreAndNavigate(vm.GoToTerminal);
            })).ToList();
            children.Add(TrayMenuItem.Sub("新建终端", shellItems));
        }

        var sessions = vm.Terminal.Sessions;
        if (sessions.Count > 0)
        {
            children.Add(TrayMenuItem.Sep);
            foreach (var session in sessions)
            {
                var captured = session;
                var label = (session.IsActive ? "● " : "") + session.Title;
                children.Add(TrayMenuItem.Item(label, () =>
                {
                    vm.Terminal.ActivateCommand.Execute(captured);
                    RestoreAndNavigate(vm.GoToTerminal);
                }));
            }
        }

        return TrayMenuItem.Sub("终端", children);
    }

    /// <summary>管理 Web 服务：one submenu per installed server (nginx / Apache / Tomcat / PHP) with its own
    /// start / restart / stop, gated by what each server supports.</summary>
    private TrayMenuItem BuildWebServicesMenu(MainViewModel vm)
    {
        var services = vm.InstalledWebServices();
        if (services.Count == 0)
            return TrayMenuItem.Sub("管理 Web 服务", new[] { TrayMenuItem.Disabled("（未检测到已安装的服务端）") });

        var subs = new List<TrayMenuItem>();
        foreach (var s in services)
        {
            var captured = s;
            var st = vm.WebServiceStatus(s.Id);
            var known = st != null;
            var running = st?.Running ?? false;

            // Status first (like the FTP menu), then the gated actions.
            var actions = new List<TrayMenuItem>
            {
                TrayMenuItem.Disabled("状态：" + (known ? st!.Value.Detail : "检测中…")),
                TrayMenuItem.Sep,
            };
            if (s.CanStart) actions.Add(TrayMenuItem.Item("启动", () => vm.RunWebServiceAction(captured, SvcAction.Start), enabled: !known || !running));
            if (s.CanRestart) actions.Add(TrayMenuItem.Item("重启", () => vm.RunWebServiceAction(captured, SvcAction.Restart), enabled: !known || running));
            if (s.CanStop) actions.Add(TrayMenuItem.Item("停止", () => vm.RunWebServiceAction(captured, SvcAction.Stop), enabled: !known || running));

            var suffix = known ? (running ? "（运行中）" : "（已停止）") : "（检测中…）";
            subs.Add(TrayMenuItem.Sub(s.Name + suffix, actions));
        }
        // Refresh the cache so the next open reflects current state (throttled inside).
        _ = vm.RefreshWebServiceStatusAsync();
        return TrayMenuItem.Sub("管理 Web 服务", subs);
    }

    /// <summary>管理 FTP 服务：start / restart / stop the self-hosted FTP/FTPS server, gated by run state.</summary>
    private static TrayMenuItem BuildFtpMenu(MainViewModel vm)
    {
        var srv = vm.Ftp.Server;
        var running = srv.Running;
        var children = new List<TrayMenuItem>
        {
            TrayMenuItem.Disabled(running ? "状态：运行中" : "状态：已停止"),
            TrayMenuItem.Sep,
            TrayMenuItem.Item("启动", srv.StartServer, enabled: !running),
            TrayMenuItem.Item("重启", srv.RestartServer, enabled: running),
            TrayMenuItem.Item("停止", srv.StopServer, enabled: running),
        };
        return TrayMenuItem.Sub("管理 FTP 服务" + (running ? "（运行中）" : "（已停止）"), children);
    }

    /// <summary>Open the Windows system-level Environment Variables editor directly.</summary>
    private static void OpenSystemEnvVars()
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                "rundll32.exe", "sysdm.cpl,EditEnvironmentVariables") { UseShellExecute = true });
        }
        catch (Exception ex) { AuditLog.App("打开环境变量设置失败：" + ex.Message); }
    }

    private void RestoreAndNavigate(Action navigate)
    {
        RestoreFromTray();
        navigate();
    }

    private void RestoreFromTray()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
        Topmost = true; Topmost = false;   // nudge to foreground
        if (!_resident) _tray?.Hide();     // keep the icon when it's set to always-resident
    }

    private void ExitFromTray()
    {
        _exiting = true;
        _tray?.Dispose();
        Application.Current.Shutdown();
    }
}
