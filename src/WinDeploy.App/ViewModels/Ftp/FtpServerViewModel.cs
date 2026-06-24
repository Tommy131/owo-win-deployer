using System.Collections.ObjectModel;
using System.Net;
using System.Net.Sockets;
using System.Windows;
using System.Windows.Threading;
using WinDeploy.App;
using WinDeploy.App.Services;
using WinDeploy.App.Services.Ftp;
using WinDeploy.Core.I18n;

namespace WinDeploy.App.ViewModels.Ftp;

/// <summary>A live view of one connected session for the monitor table.</summary>
public sealed class FtpConnRowVm : ObservableObject
{
    private readonly FtpConnectionInfo _info;
    public FtpConnRowVm(FtpConnectionInfo info) { _info = info; }
    public int Id => _info.Id;
    public string Remote => _info.Remote;
    public string User => _info.User;
    public string Activity => _info.Activity;
    public string SinceText
    {
        get
        {
            var d = DateTime.Now - _info.ConnectedAt;
            return d.TotalHours >= 1 ? $"{(int)d.TotalHours}h{d.Minutes}m" : d.TotalMinutes >= 1 ? $"{(int)d.TotalMinutes}m{d.Seconds}s" : $"{(int)d.TotalSeconds}s";
        }
    }
    public string TransferText => $"↑{Mb(_info.BytesUp)} ↓{Mb(_info.BytesDown)}";
    public void Refresh() { OnPropertyChanged(nameof(User)); OnPropertyChanged(nameof(Activity)); OnPropertyChanged(nameof(SinceText)); OnPropertyChanged(nameof(TransferText)); }
    private static string Mb(long b) => b >= 1024 * 1024 ? $"{b / 1024.0 / 1024:0.0}M" : b >= 1024 ? $"{b / 1024.0:0.0}K" : $"{b}B";
}

/// <summary>The 服务端 tab: starts/stops the FTP/FTPS listener using the saved config, and shows live status,
/// the connection table, reachable addresses, and a rolling protocol log.</summary>
public sealed class FtpServerViewModel : LocalizedObject
{
    private readonly FtpServer _server;
    private readonly Func<FtpServerConfig> _configProvider;
    private readonly Queue<string> _logLines = new();
    private DispatcherTimer? _timer;

    public FtpServerViewModel(FtpServer server, Func<FtpServerConfig> configProvider)
    {
        _server = server;
        _configProvider = configProvider;
        StartCommand = new RelayCommand(_ => Start(), _ => !Running);
        StopCommand = new RelayCommand(_ => Stop(), _ => Running);
        ClearLogCommand = new RelayCommand(_ => { _logLines.Clear(); LogText = ""; });

        _server.Logged += OnLogged;
        _server.ConnectionsChanged += OnConnectionsChanged;
        LocalAddresses = string.Join("   ", LocalIPv4());
    }

    public ObservableCollection<FtpConnRowVm> Connections { get; } = new();

    public RelayCommand StartCommand { get; }
    public RelayCommand StopCommand { get; }
    public RelayCommand ClearLogCommand { get; }

    public bool Running => _server.Running;

    // ── tray / external control (start / stop / restart from the system-tray menu) ──────────────
    public void StartServer() => Start();
    public void StopServer() { if (Running) Stop(); }
    public void RestartServer() { if (Running) Stop(); Start(); }

    public string StatusText => _server.Running ? Localizer.T("ftp.server.running") : Localizer.T("ftp.server.stopped");
    public string StatusBrush => _server.Running ? "OkFg" : "TextTertiary";
    public string LocalAddresses { get; }

    private string _endpointText = Localizer.T("ftp.server.notStarted");
    public string EndpointText { get => _endpointText; private set => Set(ref _endpointText, value); }

    private string _uptimeText = "—";
    public string UptimeText { get => _uptimeText; private set => Set(ref _uptimeText, value); }

    private string _connCountText = Localizer.Format("ftp.server.connCount", 0);
    public string ConnCountText { get => _connCountText; private set => Set(ref _connCountText, value); }

    private string _logText = "";
    public string LogText { get => _logText; private set => Set(ref _logText, value); }

    public bool NoConnections => Connections.Count == 0;

    private void Start()
    {
        FtpServerConfig cfg;
        try { cfg = _configProvider(); }
        catch (Exception ex) { Error(Localizer.Format("ftp.server.readConfigFailed", ex.Message)); return; }

        if (cfg.Users.Count == 0 && !cfg.AllowAnonymous)
        {
            if (Dialogs.Show(Localizer.T("ftp.server.startNoUserConfirm"),
                    Localizer.T("ftp.server.startNoUserTitle"), MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        }

        try
        {
            _server.Start(cfg);
            AuditLog.Action($"FTP 服务端启动 · 端口 {cfg.Port} · TLS {cfg.TlsMode}");
            RefreshState();
            StartLive();
        }
        catch (Exception ex)
        {
            AuditLog.Action("FTP 服务端启动失败：" + ex.Message);
            Error(Localizer.Format("ftp.server.startFailed", ex.Message));
        }
    }

    private void Stop()
    {
        _server.Stop();
        AuditLog.Action("FTP 服务端停止");
        RefreshState();
    }

    private void RefreshState()
    {
        OnPropertyChanged(nameof(Running));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(StatusBrush));
        CommandManager_Invalidate();
        if (_server.Running)
        {
            var cfg = _server.Config;
            var bits = new List<string> { Localizer.Format("ftp.server.endpointControl", cfg.Port) };
            if (cfg.ImplicitTls) bits.Add(Localizer.Format("ftp.server.endpointImplicit", cfg.ImplicitPort));
            else if (cfg.TlsEnabled) bits.Add(Localizer.T("ftp.server.endpointExplicit"));
            else bits.Add(Localizer.T("ftp.server.endpointPlain"));
            bits.Add(Localizer.Format("ftp.server.endpointPassive", cfg.PassiveMin, cfg.PassiveMax));
            EndpointText = string.Join(" · ", bits);
        }
        else EndpointText = Localizer.T("ftp.server.notStarted");
        RefreshConnections();
    }

    private static void CommandManager_Invalidate()
        => System.Windows.Input.CommandManager.InvalidateRequerySuggested();

    private void OnLogged(FtpLogEntry e)
    {
        var line = $"{e.Time:HH:mm:ss} " + (e.ConnId > 0 ? $"[#{e.ConnId}] " : "") + e.Text;
        Dispatcher().BeginInvoke(() =>
        {
            _logLines.Enqueue(line);
            while (_logLines.Count > 500) _logLines.Dequeue();
            LogText = string.Join("\n", _logLines);
        });
    }

    private void OnConnectionsChanged() => Dispatcher().BeginInvoke(RefreshConnections);

    private void RefreshConnections()
    {
        var live = _server.Connections;
        Connections.Clear();
        foreach (var c in live) Connections.Add(new FtpConnRowVm(c));
        ConnCountText = Localizer.Format("ftp.server.connCount", live.Count);
        OnPropertyChanged(nameof(NoConnections));
    }

    public void StartLive()
    {
        _timer ??= new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick -= OnTick;
        _timer.Tick += OnTick;
        _timer.Start();
        RefreshState();
    }

    public void StopLive() => _timer?.Stop();

    protected override void OnCultureChanged()
    {
        base.OnCultureChanged();
        RefreshState();   // recompute cached endpoint / connection-count text in the new language
    }

    private void OnTick(object? s, EventArgs e)
    {
        if (_server.Running && _server.StartedAt is DateTime t)
            UptimeText = FormatUptime(DateTime.Now - t);
        else UptimeText = "—";
        foreach (var c in Connections) c.Refresh();
    }

    private static string FormatUptime(TimeSpan up)
    {
        if (up.TotalSeconds < 0) up = TimeSpan.Zero;
        if (up.TotalDays >= 1) return Localizer.Format("ftp.server.uptimeDays", (int)up.TotalDays, up.Hours);
        if (up.TotalHours >= 1) return Localizer.Format("ftp.server.uptimeHours", (int)up.TotalHours, up.Minutes);
        if (up.TotalMinutes >= 1) return Localizer.Format("ftp.server.uptimeMinutes", (int)up.TotalMinutes, up.Seconds);
        return Localizer.Format("ftp.server.uptimeSeconds", (int)up.TotalSeconds);
    }

    private static IEnumerable<string> LocalIPv4()
    {
        var list = new List<string> { "127.0.0.1" };
        try
        {
            foreach (var ip in Dns.GetHostAddresses(Dns.GetHostName()))
                if (ip.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(ip))
                    list.Add(ip.ToString());
        }
        catch { /* best effort */ }
        return list.Distinct();
    }

    private static Dispatcher Dispatcher() => Application.Current.Dispatcher;

    private static void Error(string msg) => Dialogs.Show(msg, Localizer.T("ftp.server.errorTitle"), MessageBoxButton.OK, MessageBoxImage.Warning);
}
