using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using WinDeploy.App.Services;
using WinDeploy.Core;
using WinDeploy.Core.I18n;
using WinDeploy.Core.Models;

namespace WinDeploy.App.ViewModels.Net;

/// <summary>The "服务配置" page host. Implements nested navigation: it shows a <see cref="ServerListViewModel"/>
/// (home — all supported servers) and, when one is opened, swaps to a <see cref="ServerDetailViewModel"/>
/// (per-server management). The active sub-page is exposed via <see cref="Current"/>.</summary>
public sealed class ServiceConfigViewModel : LocalizedObject
{
    private Catalog? _catalog;
    private PathResolver _resolver = new(new Dictionary<string, string>());
    private ServerListViewModel? _list;

    private object? _current;
    public object? Current { get => _current; private set => Set(ref _current, value); }

    public void Initialize(Catalog catalog, PathResolver resolver)
    {
        _catalog = catalog;
        _resolver = resolver;
        _list = new ServerListViewModel(catalog, resolver, OpenServer);
        Current = _list;
    }

    /// <summary>Called every time the 服务配置 page becomes visible. If an open server-detail page now points
    /// at a server that no longer exists (e.g. it was uninstalled while the user was on another page), fall
    /// back to the (refreshed) server list instead of stranding the user on a stale detail page.</summary>
    public void Activate()
    {
        if (_catalog == null) return;
        if (_current is ServerDetailViewModel detail)
        {
            var stillInstalled = ServiceConfig.Detect(_catalog, _resolver).Any(s =>
                s.Id == detail.Info.Id && string.Equals(s.Dir, detail.Info.Dir, StringComparison.OrdinalIgnoreCase));
            if (stillInstalled) return;             // detail still valid — leave the user where they were
            _list?.Refresh();
            Current = _list;
        }
        else
        {
            _list?.Refresh();                        // already on the list — re-detect installed servers
        }
    }

    private void OpenServer(ServerInfo info)
    {
        var detail = new ServerDetailViewModel(info, () => { _list?.Refresh(); Current = _list; });
        Current = detail;
    }

    protected override void OnCultureChanged()
    {
        base.OnCultureChanged();
        if (_current is LocalizedObject vm) vm.RaiseAllPropertiesChanged();
    }
}

// ── home: list of supported servers ──────────────────────────────────────────
public sealed class ServerCardViewModel : ObservableObject
{
    public ServerInfo Info { get; }
    public ServerCardViewModel(ServerInfo info) { Info = info; }
    public string Name => Info.Name;
    public string Dir => Info.Dir;
    public string KindTag => Info.Id;

    private System.Windows.Media.ImageSource? _icon;
    public System.Windows.Media.ImageSource? Icon => _icon ??= IconResolver.FromCatalogId(Info.Id) ?? IconCache.Load(Info.Id);
    public bool HasIcon => Icon != null;
    public bool NoIcon => !HasIcon;
    public int ConfigCount => Info.Configs.Count;
    public string Summary
    {
        get
        {
            var bits = new List<string> { Localizer.Format("svc.summary.configCount", ConfigCount) };
            if (Info.SupportsVhost) bits.Add(Localizer.T("svc.summary.vhost"));
            if (Info.SupportsSsl) bits.Add(Localizer.T("svc.summary.ssl"));
            if (Info.HasService) bits.Add(Localizer.T("svc.summary.processMgmt"));
            return string.Join(" · ", bits);
        }
    }

    private bool _running;
    public bool Running { get => _running; set { if (Set(ref _running, value)) { OnPropertyChanged(nameof(StatusText)); OnPropertyChanged(nameof(StatusBrush)); } } }
    public string StatusText => !Info.HasService ? "—" : _running ? Localizer.T("svc.status.running") : Localizer.T("svc.status.stopped");
    public string StatusBrush => _running ? "OkFg" : "TextTertiary";
}

public sealed class ServerListViewModel : LocalizedObject
{
    private readonly Catalog _catalog;
    private readonly PathResolver _resolver;
    private readonly Action<ServerInfo> _open;

    public ObservableCollection<ServerCardViewModel> Servers { get; } = new();
    public RelayCommand RefreshCommand { get; }
    public RelayCommand OpenCommand { get; }

    public ServerListViewModel(Catalog catalog, PathResolver resolver, Action<ServerInfo> open)
    {
        _catalog = catalog;
        _resolver = resolver;
        _open = open;
        RefreshCommand = new RelayCommand(_ => Refresh());
        OpenCommand = new RelayCommand(p => { if (p is ServerCardViewModel c) _open(c.Info); });
        Refresh();
    }

    public bool HasServers => Servers.Count > 0;
    public bool NoServers => Servers.Count == 0;

    private string _note = "";
    public string Note { get => _note; set => Set(ref _note, value); }

    public void Refresh()
    {
        Servers.Clear();
        foreach (var s in ServiceConfig.Detect(_catalog, _resolver))
            Servers.Add(new ServerCardViewModel(s));
        OnPropertyChanged(nameof(HasServers));
        OnPropertyChanged(nameof(NoServers));
        Note = Servers.Count == 0
            ? Localizer.T("svc.list.empty")
            : Localizer.Format("svc.list.found", Servers.Count);
        _ = ProbeStatusAsync();
    }

    protected override void OnCultureChanged()
    {
        base.OnCultureChanged();
        Note = Servers.Count == 0
            ? Localizer.T("svc.list.empty")
            : Localizer.Format("svc.list.found", Servers.Count);
        foreach (var c in Servers) c.RaiseAllPropertiesChanged();
    }

    private async Task ProbeStatusAsync()
    {
        foreach (var c in Servers.ToList())
        {
            if (!c.Info.HasService) continue;
            try { var rt = await ServerManager.GetRuntimeAsync(c.Info); c.Running = rt.Running; }
            catch { /* ignore */ }
        }
    }
}

// ── detail: per-server management ────────────────────────────────────────────
public sealed class ServerDetailViewModel : LocalizedObject
{
    public ServerInfo Info { get; }
    private readonly Action _back;
    private DispatcherTimer? _timer;
    private bool _busy;

    public ServerDetailViewModel(ServerInfo info, Action back)
    {
        Info = info;
        _back = back;

        BackCommand = new RelayCommand(_ => { StopLive(); _back(); });
        OpenDirCommand = new RelayCommand(_ => OpenPath(Info.Dir));

        OpenConfigCommand = new RelayCommand(p => { if (p is ConfigFile f) LoadFile(f.Path); });
        SaveCommand = new RelayCommand(_ => Save(), _ => CanSave);

        StartCommand = new RelayCommand(_ => Act(SvcAction.Start));
        StopCommand = new RelayCommand(_ => Act(SvcAction.Stop));
        ReloadCommand = new RelayCommand(_ => Act(SvcAction.Reload));
        RestartCommand = new RelayCommand(_ => Act(SvcAction.Restart));

        ShowLogCommand = new RelayCommand(p => { if (p is LogFile l) new LogViewerDialog(Info.Name, l.Path) { Owner = App() }.ShowDialog(); RefreshLogs(); });
        ClearLogCommand = new RelayCommand(p => { if (p is LogFile l) { var (_, m) = ServerManager.ClearLog(l.Path); Note = $"{l.Name}：{m}"; RefreshLogs(); } });
        CollectLogsCommand = new RelayCommand(_ => CollectLogs());
        OpenLogDirCommand = new RelayCommand(_ => OpenPath(Info.LogDir));
        RefreshLogsCommand = new RelayCommand(_ => RefreshLogs());

        CreateCertCommand = new RelayCommand(_ => CreateCert());
        ImportCertCommand = new RelayCommand(_ => ImportCert());
        DeleteCertCommand = new RelayCommand(p => { if (p is CertFile c) DeleteCert(c); });
        OpenSslDirCommand = new RelayCommand(_ => OpenPath(Info.SslDir));
        RefreshCertsCommand = new RelayCommand(_ => RefreshCerts());

        CreateVhostCommand = new RelayCommand(_ => CreateVhost());
        EditVhostCommand = new RelayCommand(p => { if (p is ConfigFile f) LoadFile(f.Path); });
        DeleteVhostCommand = new RelayCommand(p => { if (p is ConfigFile f) DeleteVhost(f); });
        OpenVhostDirCommand = new RelayCommand(_ => OpenPath(Info.VhostDir));
        RefreshVhostsCommand = new RelayCommand(_ => RefreshVhosts());

        foreach (var c in Info.Configs) Configs.Add(c);
        RefreshLogs();
        if (Info.SupportsSsl) RefreshCerts();
        if (Info.SupportsVhost) RefreshVhosts();
    }

    // identity
    public string Name => Info.Name;
    public string Dir => Info.Dir;
    public string KindTag => Info.Id;
    private System.Windows.Media.ImageSource? _icon;
    public System.Windows.Media.ImageSource? Icon => _icon ??= IconResolver.FromCatalogId(Info.Id) ?? IconCache.Load(Info.Id);
    public bool HasIcon => Icon != null;
    public bool NoIcon => !HasIcon;
    public bool SupportsSsl => Info.SupportsSsl;
    public bool SupportsVhost => Info.SupportsVhost;
    public bool HasService => Info.HasService;

    public ObservableCollection<ConfigFile> Configs { get; } = new();
    public ObservableCollection<LogFile> Logs { get; } = new();
    public ObservableCollection<CertFile> Certs { get; } = new();
    public ObservableCollection<ConfigFile> Vhosts { get; } = new();

    public RelayCommand BackCommand { get; }
    public RelayCommand OpenDirCommand { get; }
    public RelayCommand OpenConfigCommand { get; }
    public RelayCommand SaveCommand { get; }
    public RelayCommand StartCommand { get; }
    public RelayCommand StopCommand { get; }
    public RelayCommand ReloadCommand { get; }
    public RelayCommand RestartCommand { get; }
    public RelayCommand ShowLogCommand { get; }
    public RelayCommand ClearLogCommand { get; }
    public RelayCommand CollectLogsCommand { get; }
    public RelayCommand OpenLogDirCommand { get; }
    public RelayCommand RefreshLogsCommand { get; }
    public RelayCommand CreateCertCommand { get; }
    public RelayCommand ImportCertCommand { get; }
    public RelayCommand DeleteCertCommand { get; }
    public RelayCommand OpenSslDirCommand { get; }
    public RelayCommand RefreshCertsCommand { get; }
    public RelayCommand CreateVhostCommand { get; }
    public RelayCommand EditVhostCommand { get; }
    public RelayCommand DeleteVhostCommand { get; }
    public RelayCommand OpenVhostDirCommand { get; }
    public RelayCommand RefreshVhostsCommand { get; }

    /// <summary>Raised when a file is loaded into the editor: (content, path).</summary>
    public event Action<string, string>? FileOpened;

    private string _note = Localizer.T("svc.detail.note");
    public string Note { get => _note; set => Set(ref _note, value); }

    public bool HasLogs => Logs.Count > 0;

    // ── runtime status ───────────────────────────────────────────────────────
    private bool _running;
    public bool Running
    {
        get => _running;
        set { if (Set(ref _running, value)) { OnPropertyChanged(nameof(StatusText)); OnPropertyChanged(nameof(StatusBrush)); RaiseActionVisibility(); } }
    }
    public string StatusText => !HasService ? Localizer.T("svc.status.noProcess") : _running ? Localizer.T("svc.status.running") : Localizer.T("svc.status.stopped");
    public string StatusBrush => !HasService ? "TextTertiary" : _running ? "OkFg" : "FailFg";

    private string _pidText = "—";
    public string PidText { get => _pidText; set => Set(ref _pidText, value); }

    private string _uptimeText = "—";
    public string UptimeText { get => _uptimeText; set => Set(ref _uptimeText, value); }

    public bool ShowStart => Info.CanStart && !_running;
    public bool ShowStop => Info.CanStop && _running;
    public bool ShowReload => Info.CanReload && _running;
    public bool ShowRestart => Info.CanRestart && _running;

    private void RaiseActionVisibility()
    {
        OnPropertyChanged(nameof(ShowStart));
        OnPropertyChanged(nameof(ShowStop));
        OnPropertyChanged(nameof(ShowReload));
        OnPropertyChanged(nameof(ShowRestart));
    }

    public void StartLive()
    {
        if (!HasService) return;
        _timer ??= new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _timer.Tick -= OnTick;
        _timer.Tick += OnTick;
        _ = RefreshRuntimeAsync();
        _timer.Start();
    }

    public void StopLive() => _timer?.Stop();

    private void OnTick(object? s, EventArgs e) => _ = RefreshRuntimeAsync();

    private async Task RefreshRuntimeAsync()
    {
        if (_busy) return;
        _busy = true;
        try
        {
            var rt = await ServerManager.GetRuntimeAsync(Info);
            Running = rt.Running;
            PidText = rt.PidText;
            UptimeText = rt.Started is DateTime t ? FormatUptime(DateTime.Now - t) : "—";
        }
        catch { /* ignore transient */ }
        finally { _busy = false; }
    }

    private static string FormatUptime(TimeSpan up)
    {
        if (up.TotalSeconds < 0) up = TimeSpan.Zero;
        if (up.TotalDays >= 1) return Localizer.Format("svc.runtime.days", (int)up.TotalDays, up.Hours);
        if (up.TotalHours >= 1) return Localizer.Format("svc.runtime.hours", (int)up.TotalHours, up.Minutes);
        if (up.TotalMinutes >= 1) return Localizer.Format("svc.runtime.minutes", (int)up.TotalMinutes, up.Seconds);
        return Localizer.Format("svc.runtime.seconds", (int)up.TotalSeconds);
    }

    // ── config editor ──────────────────────────────────────────────────────────
    private string? _currentPath;
    public string CurrentPath => _currentPath ?? Localizer.T("svc.editor.noFile");
    public bool HasOpenFile => _currentPath != null;

    private string _editor = "";
    public string Editor { get => _editor; set { if (Set(ref _editor, value)) OnPropertyChanged(nameof(CanSave)); } }
    public bool CanSave => _currentPath != null;

    private void LoadFile(string path)
    {
        try
        {
            Editor = File.ReadAllText(path);
            _currentPath = path;
            OnPropertyChanged(nameof(CurrentPath));
            OnPropertyChanged(nameof(HasOpenFile));
            OnPropertyChanged(nameof(CanSave));
            FileOpened?.Invoke(Editor, path);
        }
        catch (Exception ex) { Note = Localizer.Format("svc.file.readFailed", ex.Message); }
    }

    private void Save()
    {
        if (_currentPath == null) return;
        try
        {
            if (File.Exists(_currentPath))
                File.Copy(_currentPath, $"{_currentPath}.bak.{DateTime.Now:yyyyMMddHHmmss}", true);
            File.WriteAllText(_currentPath, Editor);
            AuditLog.Action($"服务配置：保存 {_currentPath}");
            Note = Localizer.Format("svc.file.saved", Path.GetFileName(_currentPath));
        }
        catch (Exception ex) { Note = Localizer.Format("svc.file.saveFailed", ex.Message); }
    }

    // ── service actions ──────────────────────────────────────────────────────
    private void Act(SvcAction action)
    {
        var (ok, msg) = ServiceConfig.Run(Info, action);
        var auditVerb = action switch { SvcAction.Start => "启动", SvcAction.Stop => "停止", SvcAction.Reload => "重载", _ => "重启" };
        var verb = action switch
        {
            SvcAction.Start => Localizer.T("svc.action.start"),
            SvcAction.Stop => Localizer.T("svc.action.stop"),
            SvcAction.Reload => Localizer.T("svc.action.reload"),
            _ => Localizer.T("svc.action.restart"),
        };
        AuditLog.Action($"服务配置：{auditVerb} {Info.Name} — {(ok ? "成功" : "失败")} {msg}".TrimEnd());
        Note = $"{verb}：{msg}";
        if (!ok) Dialogs.Show(msg, $"{verb} {Info.Name}", MessageBoxButton.OK, MessageBoxImage.Warning);
        _ = DelayedRuntimeRefresh();
    }

    private async Task DelayedRuntimeRefresh()
    {
        await Task.Delay(900);
        await RefreshRuntimeAsync();
    }

    // ── logs ───────────────────────────────────────────────────────────────────
    private void RefreshLogs()
    {
        Logs.Clear();
        foreach (var l in ServerManager.Logs(Info)) Logs.Add(l);
        OnPropertyChanged(nameof(HasLogs));
    }

    private void CollectLogs()
    {
        var logs = ServerManager.Logs(Info);
        if (logs.Count == 0) { Note = Localizer.T("svc.logs.collect.empty"); return; }
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = Localizer.T("svc.logs.collect.title"),
            FileName = $"{Info.Id}-logs-{Environment.MachineName}.txt",
            Filter = Localizer.T("svc.logs.collect.filter"),
        };
        if (dlg.ShowDialog() != true) return;
        try
        {
            using var w = new StreamWriter(dlg.FileName, false, new System.Text.UTF8Encoding(false));
            foreach (var l in logs)
            {
                w.WriteLine($"================ {l.Name} ({l.SizeText}) ================");
                w.WriteLine(ServerManager.ReadTail(l.Path, 1000));
                w.WriteLine();
            }
            AuditLog.Action($"服务配置：采集 {Info.Name} 日志 {logs.Count} 个 → {dlg.FileName}");
            Note = Localizer.Format("svc.logs.collected", logs.Count, dlg.FileName);
            OpenPath(dlg.FileName);
        }
        catch (Exception ex) { Note = Localizer.Format("svc.logs.collect.failed", ex.Message); }
    }

    // ── SSL ──────────────────────────────────────────────────────────────────
    private void RefreshCerts()
    {
        Certs.Clear();
        foreach (var c in ServerManager.ListCerts(Info)) Certs.Add(c);
    }

    private void CreateCert()
    {
        var dlg = new InputDialog(Localizer.T("svc.cert.create.title"), Localizer.T("svc.cert.create.prompt"), "example.local") { Owner = App() };
        if (dlg.ShowDialog() != true) return;
        var (ok, msg) = ServerManager.CreateSelfSigned(Info, dlg.Value);
        Note = msg;
        if (!ok) Dialogs.Show(msg, Localizer.T("svc.cert.createDialogTitle"), MessageBoxButton.OK, MessageBoxImage.Warning);
        RefreshCerts();
    }

    private void ImportCert()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = Localizer.T("svc.cert.import.title"),
            Filter = Localizer.T("svc.cert.import.filter"),
            Multiselect = true,
        };
        if (dlg.ShowDialog() != true) return;
        var n = 0;
        foreach (var f in dlg.FileNames) { var (ok, _) = ServerManager.ImportCert(Info, f); if (ok) n++; }
        Note = Localizer.Format("svc.cert.imported", n);
        RefreshCerts();
    }

    private void DeleteCert(CertFile c)
    {
        if (Dialogs.Show(Localizer.Format("svc.cert.delete.confirm", c.Name), Localizer.T("svc.cert.delete.title"), MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        var (ok, msg) = ServerManager.DeleteCert(c.Path);
        Note = $"{c.Name}：{msg}";
        if (ok) RefreshCerts();
    }

    // ── vhosts ─────────────────────────────────────────────────────────────────
    private void RefreshVhosts()
    {
        Vhosts.Clear();
        foreach (var v in ServerManager.ListVhosts(Info)) Vhosts.Add(v);
    }

    private void CreateVhost()
    {
        var dlg = new VhostDialog(Info) { Owner = App() };
        if (dlg.ShowDialog() != true || dlg.Result == null) return;
        var (ok, msg) = ServerManager.CreateVhost(Info, dlg.Result);
        Note = msg;
        if (!ok) Dialogs.Show(msg, Localizer.T("svc.vhost.create.title"), MessageBoxButton.OK, MessageBoxImage.Warning);
        RefreshVhosts();
    }

    private void DeleteVhost(ConfigFile f)
    {
        if (Dialogs.Show(Localizer.Format("svc.vhost.delete.confirm", f.Name), Localizer.T("svc.vhost.delete.title"), MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        var (ok, msg) = ServerManager.DeleteVhost(f.Path);
        Note = $"{f.Name}：{msg}";
        if (ok) RefreshVhosts();
    }

    // ── helpers ───────────────────────────────────────────────────────────────
    private static Window? App() => Application.Current.MainWindow;

    private void OpenPath(string path)
    {
        try
        {
            if (Directory.Exists(path) || File.Exists(path))
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
            else Note = Localizer.Format("svc.open.notFound", path);
        }
        catch (Exception ex) { Note = Localizer.Format("svc.open.failed", ex.Message); }
    }
}
