using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using WinDeploy.App.Services;
using WinDeploy.Core;
using WinDeploy.Core.Config;
using WinDeploy.Core.Engine;
using WinDeploy.Core.Engine.Installers;
using WinDeploy.Core.Models;

namespace WinDeploy.App.ViewModels;

public sealed class MainViewModel : ObservableObject
{
    private readonly InstallEngine _engine = new();
    private PathResolver _resolver = new(new Dictionary<string, string>());
    private string _repoRoot = "";
    private Catalog? _catalog;
    private CancellationTokenSource? _cts;
    private readonly SemaphoreSlim _opGate = new(1, 1);   // serialize operations so a new task queues, not replaces
    private DispatcherTimer? _installWatch;
    private readonly Dictionary<int, string?> _installPathCache = new();
    // Ids whose catalog method is github-release (captured at load, before the picker mutates the method)
    // so install always routes through the cached release-list picker.
    private readonly HashSet<string> _githubReleaseIds = new(StringComparer.OrdinalIgnoreCase);

    public ObservableCollection<NavGroupViewModel> NavGroups { get; } = new();
    public InstallCenterViewModel Install { get; } = new();
    public ProgressViewModel Progress { get; } = new();
    public ConfigSyncViewModel ConfigSync { get; } = new();
    public ExportViewModel Export { get; } = new();
    public EnvVarsViewModel EnvVars { get; } = new();
    public TerminalViewModel Terminal { get; } = new();
    public ProcessManagerViewModel Processes { get; } = new();
    public StartupViewModel Startup { get; } = new();
    public LogViewModel Logs { get; } = new();
    public SettingsViewModel Settings { get; } = new();
    public SystemOverviewViewModel SystemOverview { get; } = new();
    public MaintenanceViewModel Maintenance { get; } = new();
    public WslViewModel Wsl { get; } = new();
    public TweaksViewModel Tweaks { get; } = new();
    public AdvancedToolsViewModel AdvancedTools { get; } = new();
    public ServiceConfigViewModel ServiceConfig { get; } = new();
    public FtpViewModel Ftp { get; } = new();

    public string AppName => WinDeploy.App.AppInfo.Name;
    public string WindowTitle => WinDeploy.App.AppInfo.TitleWithRole;
    public string Copyright => $"{WinDeploy.App.AppInfo.Copyright} · v{WinDeploy.App.AppInfo.Version}";

    public MainViewModel()
    {
        Install.StartRequested += OnStartRequested;
        Install.UpdateRequested += OnUpdateRequested;
        Install.DetailRequested += OnDetailRequested;
        Install.LaunchRequested += item => _ = RunQuickOpAsync(item.Model, "launch");
        Install.StopRequested += item => _ = ConfirmRiskAndRun(item.Model, "stop");
        Install.InstallCardRequested += item => _ = QuickInstallAsync(item.Model);
        Install.UninstallCardRequested += item => _ = ConfirmUninstallAndRun(item.Model);
        Install.RestartCardRequested += item => _ = ConfirmRiskAndRun(item.Model, "restart");
        Install.UpdateCardRequested += item => _ = ConfirmUpdateAndRun(item.Model);
        Install.OpenDirRequested += item => OpenInstallDir(item.Model);
        Install.OpenHomepageRequested += item => OpenHomepage(item.Model);
        Install.RefreshRequested += () => _ = DetectAllAsync();
        Processes.OperationRequested += (item, op) => _ = ConfirmRiskAndRun(item, op);
        Progress.CancelRequested += () => _cts?.Cancel();
        Settings.Saved += () => Secrets.ExtraKeywords = SettingsViewModel.ParseKeywords(Settings.RedactKeywords);
        Settings.ConfirmEnableDeveloperMode += ShowDevModeConfirmDialog;
        Settings.DeveloperModeChanged += on => { _devMode = on; Install.SetDeveloperMode(on); RebuildNav(); };
        Settings.RefreshIconsRequested += () => _ = RefreshIconsManualAsync();
        SelectNavCommand = new RelayCommand(p => { if (p is NavItemViewModel n) SelectedNav = n; });
        Load();

        BuildNav();
        RebuildNav();

        _ = CheckSelfUpdateAsync();
    }

    /// <summary>Check GitHub for a newer release of the app itself; if found, ask the user to update.
    /// Best-effort and silent on failure (offline / no releases / rate-limited).</summary>
    private async Task CheckSelfUpdateAsync()
    {
        try
        {
            var r = await SelfUpdate.CheckAsync();
            if (!r.Available) return;   // up-to-date / offline / no release → stay quiet at startup
            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                var msg = $"发现新版本 {WinDeploy.App.AppInfo.Name} v{r.Latest}（当前 v{r.Current}）。\n\n是否前往发布页下载更新？";
                if (MessageBox.Show(msg, "检查更新", MessageBoxButton.YesNo, MessageBoxImage.Information) == MessageBoxResult.Yes)
                {
                    AuditLog.Action($"自更新：用户前往下载 v{r.Latest}");
                    try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(r.HtmlUrl) { UseShellExecute = true }); }
                    catch { /* ignore */ }
                }
            });
        }
        catch { /* stay quiet */ }
    }

    private bool _devMode = SettingsStore.Load().DeveloperMode;

    private static bool ShowDevModeConfirmDialog()
    {
        var dlg = new Views.DevModeConfirmDialog { Owner = Application.Current.MainWindow };
        return dlg.ShowDialog() == true;
    }

    /// <summary>Click a nav item to navigate to its page.</summary>
    public RelayCommand SelectNavCommand { get; }

    private IEnumerable<NavItemViewModel> AllNavItems => NavGroups.SelectMany(g => g.Items);

    // ── tray-menu navigation / actions ──────────────────────────────────────────
    // Public entry points the system-tray context menu drives. Navigation prefers the matching nav item
    // (so its highlight follows); falls back to setting the page directly if that item is hidden.
    public void GoToTerminal() => NavigateTo(Terminal);
    public void GoToLogs() => NavigateTo(Logs);
    public void GoToSettings() => NavigateTo(Settings);
    public void GoToSystemOverview() => NavigateTo(SystemOverview);

    private void NavigateTo(object page)
    {
        var item = AllNavItems.FirstOrDefault(n => ReferenceEquals(n.Page, page));
        if (item != null && item.IsVisible) SelectedNav = item;
        else Current = page;   // page not in the (visible) nav — still show it
    }

    /// <summary>The installed Web servers (nginx / Apache / Tomcat / PHP) that support process control, for the
    /// tray's per-service start/stop/restart submenu. Empty until the catalog is loaded.</summary>
    public IReadOnlyList<ServerInfo> InstalledWebServices()
    {
        if (_catalog == null) return Array.Empty<ServerInfo>();
        // Fully qualified: `ServiceConfig` here is the page VM property, not the static service helper.
        try { return Services.ServiceConfig.Detect(_catalog, _resolver).Where(s => s.HasService).ToList(); }
        catch { return Array.Empty<ServerInfo>(); }
    }

    /// <summary>Run a service action from the tray (no UI navigation); report the result as a toast + audit.</summary>
    public void RunWebServiceAction(ServerInfo info, SvcAction action)
    {
        var verb = action switch { SvcAction.Start => "启动", SvcAction.Stop => "停止", SvcAction.Reload => "重载", _ => "重启" };
        try
        {
            var (ok, msg) = Services.ServiceConfig.Run(info, action);
            AuditLog.Action($"托盘：{verb} {info.Name} — {(ok ? "成功" : "失败")} {msg}".TrimEnd());
            ToastService.TryShow($"{verb} {info.Name}", ok ? (string.IsNullOrWhiteSpace(msg) ? "操作成功" : msg) : "失败：" + msg);
        }
        catch (Exception ex)
        {
            AuditLog.Action($"托盘：{verb} {info.Name} 异常 — {ex.Message}");
            ToastService.TryShow($"{verb} {info.Name}", "异常：" + ex.Message);
        }
    }

    // ── tray: cached web-server runtime status (probed async, read synchronously by the tray menu) ──
    private readonly object _webStatusLock = new();
    private Dictionary<string, (bool Running, string Detail)> _webStatus = new(StringComparer.OrdinalIgnoreCase);
    private long _lastWebProbe;

    /// <summary>Last probed runtime status of a web server (by id) for the tray's status line; null if unprobed.</summary>
    public (bool Running, string Detail)? WebServiceStatus(string id)
    {
        lock (_webStatusLock) return _webStatus.TryGetValue(id, out var v) ? v : null;
    }

    /// <summary>Probe each installed web server's runtime status (async — Tomcat via CIM) into a cache the tray
    /// menu reads synchronously. Throttled so repeated menu opens don't spawn probes back-to-back.</summary>
    public async Task RefreshWebServiceStatusAsync(bool force = false)
    {
        var now = Environment.TickCount64;
        if (!force && now - _lastWebProbe < 1500) return;
        _lastWebProbe = now;
        var map = new Dictionary<string, (bool, string)>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in InstalledWebServices())
        {
            try
            {
                var rt = await Services.ServerManager.GetRuntimeAsync(s);
                map[s.Id] = (rt.Running, rt.Running ? $"运行中 · PID {rt.PidText}" : "已停止");
            }
            catch { map[s.Id] = (false, "未知"); }
        }
        lock (_webStatusLock) _webStatus = map;
    }

    /// <summary>Build the grouped, collapsible nav. Items flagged Advanced only show in 开发人员模式;
    /// items with a MinBuild only show on a high-enough Windows build. Built once; visibility toggled live.</summary>
    private void BuildNav()
    {
        NavGroups.Clear();

        var deploy = new NavGroupViewModel("", "部署");
        deploy.Items.Add(new("", "软件安装中心", Install));
        deploy.Items.Add(new("", "配置同步", ConfigSync));
        deploy.Items.Add(new("", "运行进度", Progress));
        deploy.Items.Add(new("", "导出", Export));

        var system = new NavGroupViewModel("", "系统");
        system.Items.Add(new("", "系统概览", SystemOverview));
        system.Items.Add(new("", "系统维护", Maintenance, advanced: true));
        system.Items.Add(new("", "进程管理", Processes, advanced: true));
        system.Items.Add(new("", "启动项", Startup, advanced: true));
        system.Items.Add(new("", "环境变量", EnvVars, advanced: true));

        var dev = new NavGroupViewModel("", "开发");
        dev.Items.Add(new("", "终端", Terminal, advanced: true));
        dev.Items.Add(new("", "服务配置", ServiceConfig, advanced: true));
        dev.Items.Add(new("", "FTP 传输", Ftp, advanced: true));
        dev.Items.Add(new("", "WSL", Wsl, advanced: true, minBuild: OsInfo.Win10_1607));
        dev.Items.Add(new("", "系统调优", Tweaks, advanced: true));
        dev.Items.Add(new("", "高级工具", AdvancedTools, advanced: true));

        var misc = new NavGroupViewModel("", "其他");
        misc.Items.Add(new("", "日志", Logs));
        misc.Items.Add(new("", "设置", Settings));

        foreach (var g in new[] { deploy, system, dev, misc }) NavGroups.Add(g);
    }

    /// <summary>Recompute each item's visibility (developer mode + OS build) and each group's visibility,
    /// keeping the current page selected if still visible (else select the first visible item).</summary>
    private void RebuildNav()
    {
        foreach (var g in NavGroups)
        {
            foreach (var n in g.Items)
                n.IsVisible = (!n.Advanced || _devMode) && OsInfo.AtLeastBuild(n.MinBuild);
            g.RaiseVisibility();
        }
        var visible = AllNavItems.Where(i => i.IsVisible).ToList();
        if (_selectedNav == null || !_selectedNav.IsVisible)
            SelectedNav = visible.FirstOrDefault();
    }

    private NavItemViewModel? _selectedNav;
    public NavItemViewModel? SelectedNav
    {
        get => _selectedNav;
        set
        {
            if (!Set(ref _selectedNav, value)) return;
            foreach (var n in AllNavItems) n.IsSelected = ReferenceEquals(n, value);
            if (value != null) Current = value.Page;
        }
    }

    private object? _current;
    public object? Current
    {
        get => _current;
        set { if (Set(ref _current, value)) UpdateInstallWatch(); }
    }

    /// <summary>Run a 2-second running-state scan only while the install center is the visible page,
    /// so cards can show 运行中 / toggle ▶↔■ live.</summary>
    private void UpdateInstallWatch()
    {
        if (ReferenceEquals(_current, Install))
        {
            _installWatch ??= new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            _installWatch.Tick -= OnInstallWatch;
            _installWatch.Tick += OnInstallWatch;
            _ = UpdateInstallRunningAsync();
            _installWatch.Start();
        }
        else _installWatch?.Stop();
    }

    private void OnInstallWatch(object? sender, EventArgs e) => _ = UpdateInstallRunningAsync();

    private async Task UpdateInstallRunningAsync()
    {
        var items = Install.Groups.SelectMany(g => g.Items).Where(i => i.IsInstalled).Select(i => i.Model).ToList();
        if (items.Count == 0)
        {
            foreach (var vm in Install.Groups.SelectMany(g => g.Items)) vm.HasRunningProc = false;
            return;
        }
        var pr = _resolver;
        var cache = _installPathCache;
        var running = await Task.Run(() =>
        {
            var targets = new List<(string Id, string Proc, string? Dir)>();
            foreach (var it in items)
            {
                try { var t = ProcessControl.ResolveTarget(it, pr); if (t != null) targets.Add((it.Id, t.Value.Proc, t.Value.Dir)); }
                catch { /* skip */ }
            }
            return ProcessControl.ScanAll(targets, cache).Select(x => x.Id).ToHashSet();
        });
        foreach (var vm in Install.Groups.SelectMany(g => g.Items))
            vm.HasRunningProc = vm.IsInstalled && running.Contains(vm.Id);
    }

    private void Load()
    {
        var dir = CatalogLoader.FindCatalogDir(AppContext.BaseDirectory)
                  ?? CatalogLoader.FindCatalogDir(Environment.CurrentDirectory);
        if (dir == null) { Install.LoadError = "找不到 catalog/catalog.json"; Install.IsLoading = false; return; }

        var path = Path.Combine(dir, "catalog.json");
        _repoRoot = Path.GetDirectoryName(dir)!;
        Terminal.WorkingDir = _repoRoot;
        try { _catalog = CatalogLoader.Load(path); }
        catch (Exception ex) { Install.LoadError = ex.Message; Install.IsLoading = false; return; }

        var settings = SettingsStore.Load();
        var vars = new Dictionary<string, string>(_catalog.PathVars, StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(settings.DevRoot)) vars["DevRoot"] = settings.DevRoot!;
        if (!string.IsNullOrWhiteSpace(settings.ToolsDir)) vars["ToolsDir"] = settings.ToolsDir!;
        vars["DownloadDir"] = string.IsNullOrWhiteSpace(settings.DownloadDir) ? "%USERPROFILE%/Downloads/WinDeploy" : settings.DownloadDir!;
        _resolver = new PathResolver(vars);
        Secrets.ExtraKeywords = SettingsViewModel.ParseKeywords(settings.RedactKeywords);

        // Restore custom install locations so portable/git apps installed outside their default folder
        // are still detected, launchable, and process-matched after a restart.
        if (settings.InstallPaths is { Count: > 0 } paths)
        {
            // Migrate away mis-set shared base dirs: a path used by 2+ items isn't a real per-app location
            // (e.g. several winget apps all pointed at D:\Tools\System). Such a broad dir makes process
            // matching catch unrelated neighbours, so drop those entries (revert to default detection).
            var shared = paths.GroupBy(kv => (kv.Value ?? "").TrimEnd('\\', '/'), StringComparer.OrdinalIgnoreCase)
                              .Where(grp => grp.Key.Length > 0 && grp.Count() > 1)
                              .SelectMany(grp => grp.Select(kv => kv.Key)).ToList();
            foreach (var badId in shared) { paths.Remove(badId); SettingsStore.SetInstallPath(badId, null); }

            foreach (var item in _catalog.Items)
                if (paths.TryGetValue(item.Id, out var p) && !string.IsNullOrWhiteSpace(p))
                    item.InstallPathOverride = p;
        }

        foreach (var i in _catalog.Items)
            if (i.Install.Method == "github-release") _githubReleaseIds.Add(i.Id);

        IconResolver.Init(_catalog, _repoRoot);
        Install.Initialize(_catalog, dir);
        ConfigSync.Initialize(_catalog, _resolver, _repoRoot);
        Export.Initialize(_catalog, _resolver, _repoRoot);
        Processes.Initialize(_catalog, _resolver, _repoRoot);
        AdvancedTools.Initialize(_catalog, _resolver, _repoRoot, dir);
        ServiceConfig.Initialize(_catalog, _resolver);
        _ = DetectAllAsync();
    }

    /// <summary>Detect every item (throttled-parallel) and check update availability BEFORE revealing
    /// the list (the blue loading state), then prefetch detail. Re-runnable from the 刷新 button.</summary>
    private async Task DetectAllAsync()
    {
        Install.IsLoading = true;
        Detection.ResetCache();
        Arp.Refresh();
        UpdateChecker.Reset();

        var items = Install.Groups.SelectMany(g => g.Items).ToList();
        _ = FetchIconCacheAsync(items);   // 联网补全缺失图标到本地缓存（与检测并行，先行启动）
        using var gate = new SemaphoreSlim(8);
        var tasks = items.Select(async vm =>
        {
            await gate.WaitAsync();
            try { vm.Installed = await Detection.IsInstalledAsync(vm.Model, _resolver); }
            catch { vm.Installed = false; }
            finally { gate.Release(); }
        });
        await Task.WhenAll(tasks);

        // Store / MSIX apps (no ARP / winget id) — detect via Get-StartApps so ▶启动 appears.
        try
        {
            var store = await Task.Run(() => StoreApps.All());
            if (store.Count > 0)
                foreach (var vm in items.Where(i => i.Installed != true))
                    if (StoreApps.HasMsixApp(vm.Model.Name)) vm.Installed = true;
        }
        catch { /* StartApps unavailable */ }

        // Toolchains exposed via an env var (GOROOT/JAVA_HOME/PHP_HOME/LUA_HOME/GCC_HOME/CATALINA_HOME):
        // if the var points to an existing dir, treat as installed and use it as the install path so
        // 打开目录 / 进程状态 / 启动 all work, even if it was installed outside this tool.
        foreach (var vm in items)
        {
            var ev = vm.Model.Detect?.EnvVar;
            if (string.IsNullOrEmpty(ev)) continue;
            var envDir = Detection.EnvVarDir(ev);
            if (envDir == null) continue;
            vm.Installed = true;
            if (string.IsNullOrEmpty(vm.Model.InstallPathOverride))
            {
                vm.Model.InstallPathOverride = envDir;
                SettingsStore.SetInstallPath(vm.Model.Id, envDir);
            }
        }

        // Portable/git apps installed to a custom folder the catalog/settings don't know about
        // (e.g. cc-switch under D:\Tools): locate the real .exe (ARP / install-spec / Run key / Start menu)
        // and remember its directory so detection, launch, and process status all work afterwards.
        foreach (var vm in items.Where(i => i.Installed != true
                     && i.Model.Install.Method is "portable" or "git" or "exe" or "manual" or "github-release"))
        {
            try
            {
                var exe = await Task.Run(() => Launcher.ResolveExePath(vm.Model, _resolver));
                if (exe == null) continue;
                vm.Installed = true;
                BackfillInstallPath(vm.Model, exe);
            }
            catch { /* skip */ }
        }

        await RunUpdateCheckAsync(items);   // badge installed apps that have an upgrade

        Install.IsLoading = false;
        _ = PrefetchDetailsAsync(items);
        _ = RefreshIconsAsync(items);
    }

    /// <summary>Manual 联网刷新软件图标 from Settings: fetch missing icons behind a busy dialog, then report.</summary>
    private async Task RefreshIconsManualAsync()
    {
        var items = Install.Groups.SelectMany(g => g.Items).ToList();
        Settings.IconNote = "正在联网获取 …";
        var n = await Views.BusyDialog.RunAsync(Application.Current.MainWindow, "刷新软件图标",
            "正在联网获取缺失的软件图标并缓存到本地…", () => FetchIconCacheAsync(items));
        Settings.IconNote = n > 0 ? $"已补全并缓存 {n} 个图标" : "未发现需要补全的图标（均已就绪 / 本次未获取到）";
    }

    /// <summary>Batch-download HD brand icons for items that have no bundled icon, cache them under
    /// %LOCALAPPDATA%, then adopt them — so missing icons don't leave letter badges. Best-effort.</summary>
    private async Task<int> FetchIconCacheAsync(IReadOnlyList<AppItemViewModel> items)
    {
        try
        {
            var need = items.Where(vm => !vm.HasIcon)
                            .Select(vm => (vm.Model.Id, vm.Model.Homepage, vm.Model.Name)).ToList();
            if (need.Count == 0) return 0;
            var n = await Services.IconCache.FetchMissingAsync(need, _repoRoot);
            if (n > 0)
            {
                await Application.Current.Dispatcher.InvokeAsync(() => { foreach (var vm in items) vm.ReloadFromCache(); });
                AuditLog.Action($"软件图标缓存：联网补全 {n} 个");
            }
            return n;
        }
        catch { return 0; /* best-effort; letter badge remains */ }
    }

    /// <summary>For installed items, replace the bundled icon with the app's real icon from its .exe.</summary>
    private async Task RefreshIconsAsync(IReadOnlyList<AppItemViewModel> items)
    {
        foreach (var vm in items)
        {
            if (!vm.IsInstalled) continue;
            string? exe = null;
            try { exe = await Task.Run(() => Launcher.ResolveExePath(vm.Model, _resolver)); }
            catch { /* skip */ }
            if (exe != null) vm.SetIconFromExe(exe);
        }
    }

    /// <summary>One `winget upgrade` pass → flag installed items that have an available upgrade.</summary>
    private async Task RunUpdateCheckAsync(IReadOnlyList<AppItemViewModel>? items = null)
    {
        items ??= Install.Groups.SelectMany(g => g.Items).ToList();
        var output = await UpdateChecker.WingetUpgradeOutputAsync(force: true);
        foreach (var vm in items)
            vm.HasUpdate = vm.IsInstalled && UpdateChecker.HasUpgrade(vm.Model, output);
        Install.RefreshUpdateState();
    }

    /// <summary>Prefetch and cache detail metadata for every item so card clicks are instant.</summary>
    private static async Task PrefetchDetailsAsync(IReadOnlyList<AppItemViewModel> items)
    {
        using var gate = new SemaphoreSlim(6);
        var tasks = items.Select(async vm =>
        {
            await gate.WaitAsync();
            try { await DetailService.FetchAsync(vm.Model); }
            catch { /* ignore */ }
            finally { gate.Release(); }
        });
        await Task.WhenAll(tasks);
    }

    private void OnStartRequested()
    {
        if (_catalog == null) return;
        var selected = Install.Groups.SelectMany(g => g.Items)
            .Where(i => i.IsSelected).Select(i => i.Model).ToList();
        if (selected.Count == 0) return;

        SelectedNav = AllNavItems.First(n => ReferenceEquals(n.Page, Progress));
        _ = RunAsync(selected);
    }

    private void OnDetailRequested(AppItemViewModel item)
    {
        var vm = new DetailViewModel(item, _resolver, back: () => Current = Install);
        vm.InstallRequested += m => _ = DispatchInstall(m);
        vm.UpdateRequested += m => _ = ConfirmUpdateAndRun(m);
        vm.DowngradeRequested += m => _ = ConfirmDowngradeAndRun(m);
        vm.UninstallRequested += (m, purge) => _ = RunOpAsync(m, "uninstall", purge);
        vm.LaunchRequested += m => _ = RunQuickOpAsync(m, "launch");
        vm.StopRequested += m => _ = ConfirmRiskAndRun(m, "stop");
        vm.RestartRequested += m => _ = ConfirmRiskAndRun(m, "restart");
        vm.EnvVarsRequested += () => SelectedNav = AllNavItems.First(n => ReferenceEquals(n.Page, EnvVars));
        Current = vm;
    }

    /// <summary>Route an install by item: MinGW / GitHub-release pickers, else the standard install.</summary>
    private Task DispatchInstall(CatalogItem m) => m.Id switch
    {
        "mingw" => InstallMinGwAsync(m),
        "mingw-builds" => InstallMingwBuildsAsync(m),
        "php" => InstallPhpAsync(m),
        _ when _githubReleaseIds.Contains(m.Id) => InstallFromGitHubReleaseAsync(m),
        _ => RunOpAsync(m, "install"),
    };

    // PHP has no winget package and ships many parallel versions; offer a picker and install each to its
    // own ${ToolsDir}/php/<version> folder so versions coexist.
    private static readonly (string Label, string Version, string Url)[] PhpVersions =
    {
        ("PHP 8.4.3  (VS17 x64)",  "8.4.3",  "https://windows.php.net/downloads/releases/archives/php-8.4.3-Win32-vs17-x64.zip"),
        ("PHP 8.3.14 (VS16 x64)",  "8.3.14", "https://windows.php.net/downloads/releases/archives/php-8.3.14-Win32-vs16-x64.zip"),
        ("PHP 8.2.26 (VS16 x64)",  "8.2.26", "https://windows.php.net/downloads/releases/archives/php-8.2.26-Win32-vs16-x64.zip"),
        ("PHP 8.1.31 (VS16 x64)",  "8.1.31", "https://windows.php.net/downloads/releases/archives/php-8.1.31-Win32-vs16-x64.zip"),
        ("PHP 7.4.33 (VC15 x64)",  "7.4.33", "https://windows.php.net/downloads/releases/archives/php-7.4.33-Win32-vc15-x64.zip"),
    };

    /// <summary>PHP version picker: install the chosen version to ${ToolsDir}/php/&lt;version&gt; (versions
    /// coexist), then point PHP_HOME + PATH at it (the active version). Re-run to add or switch versions.</summary>
    private async Task InstallPhpAsync(CatalogItem item)
    {
        var labels = PhpVersions.Select(v => v.Label + (IsPhpVersionInstalled(v.Version) ? "    ✓ 已装" : "")).ToList();
        var dlg = new Views.ChoiceDialog("选择 PHP 版本",
            "多版本可共存（各装到 ${ToolsDir}/php/<版本>）。选中的版本将成为活动版本（PHP_HOME + PATH）：",
            labels, 0) { Owner = Application.Current.MainWindow };
        if (dlg.ShowDialog() != true || dlg.SelectedIndex < 0) return;
        var v = PhpVersions[dlg.SelectedIndex];

        item.Install.Method = "portable";
        item.Install.Url = v.Url;
        item.Install.ExtractTo = $"${{ToolsDir}}/php/{v.Version}";
        item.Install.Strip = 0;
        item.Install.Sha256 = null;
        item.Install.Path = new List<string> { $"${{ToolsDir}}/php/{v.Version}" };
        item.InstallPathOverride = null;
        await RunOpAsync(item, "install");

        // Make the just-installed version the active one (overwrite PHP_HOME; ensure its dir is on PATH).
        try
        {
            var dir = _resolver.Resolve($"${{ToolsDir}}/php/{v.Version}");
            if (Directory.Exists(dir))
            {
                WinDeploy.Core.Util.EnvPath.SetUserVar("PHP_HOME", dir);
                Environment.SetEnvironmentVariable("PHP_HOME", dir);
                WinDeploy.Core.Util.EnvPath.AddToUserPath(dir);
                item.InstallPathOverride = dir;
                SettingsStore.SetInstallPath(item.Id, dir);
                AuditLog.Action($"PHP：活动版本设为 {v.Version}（PHP_HOME={dir}）");
            }
        }
        catch { /* best effort */ }
    }

    private bool IsPhpVersionInstalled(string version)
    {
        try { return Directory.Exists(_resolver.Resolve($"${{ToolsDir}}/php/{version}")); }
        catch { return false; }
    }

    /// <summary>Right-click「快速安装到默认路径」: install into a category-based root + 软件名 (tools → ${ToolsDir},
    /// ai → %LOCALAPPDATA%/ai_workspace, others → installer default), unless a path is already set.</summary>
    private async Task QuickInstallAsync(CatalogItem m)
    {
        if (string.IsNullOrEmpty(m.InstallPathOverride))
        {
            try
            {
                // 默认安装根目录按类别：实用工具 → ${ToolsDir}；AI 工具 → %LOCALAPPDATA%/ai_workspace；
                // 其他 → 不指定（用安装包默认位置 Program Files，由安装包自动决定 x86/x64）。
                string? baseDir = m.Category switch
                {
                    "tools" => _resolver.Resolve("${ToolsDir}"),
                    "ai" => Environment.ExpandEnvironmentVariables(@"%LOCALAPPDATA%\ai_workspace"),
                    _ => null,
                };
                if (!string.IsNullOrWhiteSpace(baseDir) && !baseDir.Contains("${"))
                    m.InstallPathOverride = InstallCenterViewModel.ComposeInstallPath(baseDir, m.Name);
            }
            catch { /* fall back to the method's default location */ }
        }
        await DispatchInstall(m);
    }

    private async Task ConfirmUninstallAndRun(CatalogItem item)
    {
        var choice = MessageBox.Show(
            $"卸载 {item.Name}？\n\n【是】彻底删除（含用户数据）\n【否】仅卸载，保留数据\n【取消】不操作",
            "卸载", MessageBoxButton.YesNoCancel, MessageBoxImage.Warning);
        if (choice == MessageBoxResult.Cancel) return;
        await RunOpAsync(item, "uninstall", purge: choice == MessageBoxResult.Yes);
    }

    private void OpenInstallDir(CatalogItem item)
    {
        string? dir = null;
        try
        {
            var exe = Launcher.ResolveExePath(item, _resolver);
            if (exe != null) dir = Path.GetDirectoryName(exe);
            if (string.IsNullOrEmpty(dir) && ProcessControl.ResolveTarget(item, _resolver) is { } t) dir = t.Dir;
        }
        catch { /* ignore */ }
        if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
        {
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(dir) { UseShellExecute = true }); }
            catch { /* ignore */ }
        }
        else MessageBox.Show($"未能定位 {item.Name} 的安装目录。", "打开软件目录", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private static void OpenHomepage(CatalogItem item)
    {
        if (string.IsNullOrWhiteSpace(item.Homepage)) return;
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(item.Homepage) { UseShellExecute = true }); }
        catch { /* ignore */ }
    }

    private async Task ConfirmRiskAndRun(CatalogItem item, string op)
    {
        var verb = Verb(op);
        var detail = op == "stop" ? "将结束该软件的所有进程。" : op == "restart" ? "将结束并重新启动该软件。" : "";
        if (MessageBox.Show($"确定要{verb} {item.Name}？\n\n{detail}".TrimEnd(),
                verb, MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        await RunQuickOpAsync(item, op);
    }

    private async Task ConfirmUpdateAndRun(CatalogItem item)
    {
        if (MessageBox.Show($"是否检查并更新 {item.Name}？\n\n（若已是最新版本会提示「已是最新」）",
                "更新", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        await RunOpAsync(item, "update");
    }

    private Task InstallMinGwAsync(CatalogItem item)
        => ChooseToolchainAsync(item, "MinGW-w64 (WinLibs)", WinLibs.GetVariantsAsync);

    private Task InstallMingwBuildsAsync(CatalogItem item)
        => ChooseToolchainAsync(item, "MinGW-builds (niXman)", WinLibs.GetMingwBuildsAsync);

    /// <summary>Fetch the latest release (cached), filter by arch, let the user pick a compiler build
    /// (posix recommended), then portable-install the chosen archive. extractTo/strip/path come from the catalog.</summary>
    private async Task ChooseToolchainAsync(CatalogItem item, string title, Func<bool, Task<List<WinLibsVariant>>> fetch)
    {
        List<WinLibsVariant> variants;
        try { variants = await fetch(Environment.Is64BitOperatingSystem); }
        catch (Exception ex)
        {
            MessageBox.Show($"获取 {title} 版本失败：{ex.Message}\n\n可前往项目发布页手动下载。",
                title, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (variants.Count == 0)
        {
            MessageBox.Show($"未找到匹配当前系统架构的 {title} 压缩包。", title, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var labels = variants.Select(v => v.Label + (v.Recommended ? "    ✓ 推荐" : "")).ToList();
        var rec = variants.FindIndex(v => v.Recommended);
        var arch = Environment.Is64BitOperatingSystem ? "x86_64（64 位）" : "i686（32 位）";
        var dlg = new Views.ChoiceDialog($"选择 {title} 编译版",
            $"已根据系统架构筛选：{arch}\n请选择要下载安装的编译版本（推荐 posix 线程 + UCRT）：",
            labels, rec) { Owner = Application.Current.MainWindow };
        if (dlg.ShowDialog() != true || dlg.SelectedIndex < 0) return;

        item.Install.Method = "portable";
        item.Install.Url = variants[dlg.SelectedIndex].Url;
        await RunOpAsync(item, "install");
    }

    /// <summary>Pick a GitHub release tag (all tags, cached), then an asset, then download + install it:
    /// zip/7z → portable-extract to ${ToolsDir}/&lt;id&gt;; otherwise run the downloaded installer.</summary>
    private async Task InstallFromGitHubReleaseAsync(CatalogItem item)
    {
        var repo = GitHubRepoFromUrl(item.Install.Repo) ?? GitHubRepoFromUrl(item.Homepage);
        if (repo == null)
        {
            MessageBox.Show($"无法识别 GitHub 仓库（install.repo / homepage）：\n{item.Install.Repo ?? item.Homepage}", item.Name,
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        List<GhRelease> releases;
        try { releases = await GitHub.ReleasesAsync(repo); }
        catch (Exception ex)
        {
            MessageBox.Show($"获取 {item.Name} 发布列表失败：{ex.Message}\n\n可前往项目发布页手动下载。",
                item.Name, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (releases.Count == 0)
        {
            MessageBox.Show($"{item.Name} 仓库 {repo} 暂无发布版。", item.Name, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var tagLabels = releases.Select(r =>
            r.Tag + (r.Prerelease ? "  · 预发布" : "") +
            (string.IsNullOrWhiteSpace(r.Name) || r.Name == r.Tag ? "" : "  · " + r.Name)).ToList();
        var dlgTag = new Views.ChoiceDialog($"选择 {item.Name} 版本",
            $"仓库 {repo} 共 {releases.Count} 个发布版（已缓存，避免频繁请求）。请选择版本：",
            tagLabels, 0) { Owner = Application.Current.MainWindow };
        if (dlgTag.ShowDialog() != true || dlgTag.SelectedIndex < 0) return;
        var rel = releases[dlgTag.SelectedIndex];

        if (rel.Assets.Count == 0)
        {
            MessageBox.Show($"{rel.Tag} 没有可下载的文件（仅源码）。", item.Name, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        // 按系统平台过滤掉用不上的发行版（如 x64 机器上的 arm64）；过滤后为空则全部列出。
        // 再按当前 CPU 架构排序：匹配当前架构的排最前，作为默认选中项。
        var assets = rel.Assets.Where(a => WinDeploy.Core.Util.Arch.AssetUsable(a.Name)).ToList();
        if (assets.Count == 0) assets = rel.Assets.ToList();
        assets = assets.OrderBy(a => WinDeploy.Core.Util.Arch.PreferScore(a.Name)).ToList();

        var assetLabels = assets.Select(a => $"{a.Name}   （{Mb(a.Size)}）").ToList();
        var dlgAsset = new Views.ChoiceDialog($"选择文件 · {rel.Tag}",
            "请选择要下载的文件（已按当前系统平台过滤；压缩包自动解压、便携 exe 放入安装目录、Setup 直接运行）：",
            assetLabels, 0) { Owner = Application.Current.MainWindow };
        if (dlgAsset.ShowDialog() != true || dlgAsset.SelectedIndex < 0) return;
        var asset = assets[dlgAsset.SelectedIndex];

        var name = asset.Name.ToLowerInvariant();
        var isInstaller = (name.EndsWith(".exe") || name.EndsWith(".msi")) && name.Contains("setup");
        if (isInstaller)
        {
            item.Install.Method = "exe";   // a real installer decides its own location
            item.Install.Url = asset.Url;
        }
        else
        {
            // archive → extract; standalone portable file → copied in. Both honor InstallPathOverride.
            item.Install.Method = "portable";
            item.Install.Url = asset.Url;
            item.Install.ExtractTo = $"${{ToolsDir}}/{item.Id}";
            item.Install.Strip = 0;
            item.Install.Path = null;
            item.Install.Sha256 = null;
        }
        await RunOpAsync(item, "install");
    }

    /// <summary>"https://github.com/owner/repo/releases" → "owner/repo", or null.</summary>
    private static string? GitHubRepoFromUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        var m = System.Text.RegularExpressions.Regex.Match(url, @"github\.com/([^/]+)/([^/#?]+)",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return m.Success ? $"{m.Groups[1].Value}/{m.Groups[2].Value}" : null;
    }

    private async Task ConfirmDowngradeAndRun(CatalogItem item)
    {
        if (MessageBox.Show($"确定将 {item.Name} 降级到 {item.Version}？\n\n降级可能导致配置不兼容。",
                "降级", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        await RunOpAsync(item, "downgrade");
    }

    private static string Zh(StepStatus s) => s switch
    {
        StepStatus.Ok => "成功", StepStatus.Failed => "失败", _ => "跳过",
    };

    private static string Verb(string op) => op switch
    {
        "install" => "安装", "update" => "更新", "uninstall" => "卸载", "downgrade" => "降级",
        "launch" => "启动", "stop" => "结束", "restart" => "重启", _ => "操作",
    };

    /// <summary>Run ONE operation, mirrored on the 运行进度 page (auto-navigated to). Cancellable;
    /// a cancelled install cleans its residue, and a successful uninstall offers leftover cleanup.</summary>
    private async Task RunOpAsync(CatalogItem item, string op, bool purge = false)
    {
        SelectedNav = AllNavItems.First(n => ReferenceEquals(n.Page, Progress));
        var verb = Verb(op);
        var row = Progress.Enqueue(item.Id, item.Name, item.Install.Method);   // 排队 until the lock frees

        await _opGate.WaitAsync();
        try
        {
            var ctx = NewCtx(out var ct);
            Progress.BeginRun(verb, 1);
            Progress.Start(row);
            var preCandidates = op == "uninstall" ? LeftoverScanner.Candidates(item, _resolver) : null;

            StepOutcome outcome;
            var cancelled = false;
            try
            {
                outcome = op switch
                {
                    "install" => await _engine.RunOneAsync(item, ctx),
                    "update" => await Updater.UpdateAsync(item, ctx),
                    "downgrade" => await Updater.DowngradeAsync(item, ctx),
                    "uninstall" => await Uninstaller.UninstallAsync(item, _resolver, purge, ct, ctx.Report),
                    _ => StepOutcome.Fail("未知操作"),
                };
            }
            catch (OperationCanceledException) { cancelled = true; ctx.Step("用户已取消"); outcome = StepOutcome.Fail("已取消"); }
            catch (Exception ex) { ctx.Step("出错：" + ex.Message); outcome = StepOutcome.Fail(ex.Message); }

            if (cancelled && op == "install")
            {
                var freed = Cleanup.RemoveInstallResidue(item, _resolver);
                var msg = freed > 0 ? $"已取消，已清理残留 {Mb(freed)}" : "已取消（无残留）";
                ctx.Step(msg);
                outcome = StepOutcome.Fail(msg);
            }

            if (op == "install" && outcome.Status == StepStatus.Ok)
                ApplyToolEnv(item, ctx);

            AuditLog.Action($"{verb} {item.Name}：{Zh(outcome.Status)} {outcome.Message}".TrimEnd());
            Progress.Done(row, outcome.Status, outcome.Message);
            Progress.EndRun();

            if (op is "install" or "update" or "uninstall" or "downgrade")
            {
                if (outcome.Status == StepStatus.Ok)
                {
                    if (op == "uninstall") SettingsStore.SetInstallPath(item.Id, null);
                    else PersistInstallPath(item);
                }
                Detection.ResetCache();
                Arp.Refresh();
                DetailService.Invalidate(item.Id);
                UpdateChecker.Reset();
                await RefreshInstalledFlag(item);
                await RunUpdateCheckAsync();
            }

            if (op == "uninstall" && !cancelled && outcome.Status == StepStatus.Ok && preCandidates != null)
                PromptLeftoverCleanup(item, preCandidates);
        }
        finally { _opGate.Release(); }
    }

    /// <summary>Process-level ops (启动 / 结束 / 重启) run IMMEDIATELY — they don't take the operation
    /// lock and don't touch the active run, so they never wait behind a long install/update.</summary>
    private async Task RunQuickOpAsync(CatalogItem item, string op)
    {
        SelectedNav = AllNavItems.First(n => ReferenceEquals(n.Page, Progress));
        var verb = Verb(op);
        var row = Progress.AddRunningRow(item.Id, item.Name, item.Install.Method, verb);
        var ctx = QuickCtx(row);

        StepOutcome outcome;
        try
        {
            outcome = op switch
            {
                "launch" => await Task.Run(() => LaunchOp(item, ctx)),
                "stop" => await Task.Run(() => StopOp(item, ctx)),
                "restart" => await RestartOp(item, ctx),
                _ => StepOutcome.Fail("未知操作"),
            };
        }
        catch (Exception ex) { outcome = StepOutcome.Fail(ex.Message); }

        AuditLog.Action($"{verb} {item.Name}：{Zh(outcome.Status)} {outcome.Message}".TrimEnd());
        Progress.FinishRow(row, outcome.Status, outcome.Message, verb);
    }

    /// <summary>Context whose step log targets a specific quick-op row (not the active run's row).</summary>
    private EngineContext QuickCtx(ProgressItemViewModel row)
    {
        var disp = Application.Current.Dispatcher;
        return new EngineContext
        {
            Path = _resolver,
            RepoRoot = _repoRoot,
            Ct = CancellationToken.None,
            Report = msg => disp.Invoke(() => row.AddDetail($"{DateTime.Now:HH:mm:ss}  {msg}")),
        };
    }

    private EngineContext NewCtx(out CancellationToken ct)
    {
        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        ct = _cts.Token;
        var disp = Application.Current.Dispatcher;
        return new EngineContext
        {
            Path = _resolver,
            RepoRoot = _repoRoot,
            Ct = ct,
            DownloadDir = ResolveDownloadDir(),
            Report = msg => disp.Invoke(() => Progress.OnStep(msg)),
            Progress = msg => disp.Invoke(() => Progress.OnLiveProgress(msg)),
        };
    }

    private string? ResolveDownloadDir()
    {
        try
        {
            var d = _resolver.Resolve("${DownloadDir}");
            return string.IsNullOrWhiteSpace(d) || d.Contains("${") ? null : d;
        }
        catch { return null; }
    }

    private void PromptLeftoverCleanup(CatalogItem item, List<string> candidates)
    {
        var junk = LeftoverScanner.Scan(candidates);
        if (junk.Count == 0) return;
        var total = junk.Sum(j => j.Bytes);
        var lines = string.Join("\n", junk.Select(j => $"· {j.Path}  （{j.SizeText}）"));
        var msg = $"{item.Name} 卸载后仍有 {junk.Count} 处残留，约 {Mb(total)} 可释放：\n\n{lines}\n\n是否立即清理？";
        if (MessageBox.Show(msg, "清理残留", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;
        var (count, freed) = LeftoverScanner.Delete(junk);
        AuditLog.Action($"清理残留 {item.Name}：删除 {count} 处，释放 {Mb(freed)}");
        MessageBox.Show($"已清理 {count} 处，释放 {Mb(freed)}。", "清理残留",
            MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private static string Mb(long bytes) => bytes >= 1024L * 1024 * 1024
        ? $"{bytes / 1024.0 / 1024 / 1024:0.0} GB"
        : $"{bytes / 1024.0 / 1024:0.0} MB";

    private StepOutcome LaunchOp(CatalogItem item, EngineContext ctx)
    {
        var ok = Launcher.TryLaunch(item, _resolver, out var detail, ctx.Step);
        return ok ? StepOutcome.Done("已启动 " + detail) : StepOutcome.Fail(detail);
    }

    private StepOutcome StopOp(CatalogItem item, EngineContext ctx)
    {
        var procs = ProcessControl.Find(item, _resolver);
        ctx.Step($"找到 {procs.Count} 个进程");
        var n = 0;
        foreach (var p in procs)
            if (ProcessControl.Kill(p.Pid)) { n++; ctx.Step($"已结束 {p.Name} (PID {p.Pid})"); }
        return StepOutcome.Done(n > 0 ? $"已结束 {n} 个进程" : "没有正在运行的进程");
    }

    private async Task<StepOutcome> RestartOp(CatalogItem item, EngineContext ctx)
    {
        var n = await Task.Run(() =>
        {
            var k = 0;
            foreach (var p in ProcessControl.Find(item, _resolver)) if (ProcessControl.Kill(p.Pid)) k++;
            return k;
        });
        ctx.Step($"已结束 {n} 个进程");
        await Task.Delay(600);
        var (ok, detail) = await Task.Run(() =>
        {
            var r = Launcher.TryLaunch(item, _resolver, out var d, ctx.Step);
            return (r, d);
        });
        return ok
            ? StepOutcome.Done($"已重启（结束 {n} 个进程后启动）")
            : StepOutcome.Fail("重启失败：" + detail);
    }

    /// <summary>On first install of a toolchain with a catalog <c>detect.envVar</c> (GCC_HOME / GOROOT /
    /// JAVA_HOME / PHP_HOME / LUA_HOME / CATALINA_HOME …), set that env var to the install root and add its
    /// bin to PATH. Skips if the var is already set (respects the user). Driven by the same field detection
    /// uses, so detect ↔ auto-set stay in sync.</summary>
    private void ApplyToolEnv(CatalogItem item, EngineContext? ctx = null)
    {
        var varName = item.Detect?.EnvVar;
        if (string.IsNullOrEmpty(varName)) return;
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable(varName, EnvironmentVariableTarget.User)))
            return;   // 首次安装才设置；已存在则尊重用户现有配置

        var home = ResolveToolHome(item);
        if (home == null) { ctx?.Step($"未能确定 {varName} 安装路径，跳过环境变量设置"); return; }

        WinDeploy.Core.Util.EnvPath.SetUserVar(varName, home);
        Environment.SetEnvironmentVariable(varName, home);   // reflect into this process
        var bin = Path.Combine(home, "bin");
        var addedPath = Directory.Exists(bin) && WinDeploy.Core.Util.EnvPath.AddToUserPath(bin);
        AuditLog.Action($"环境变量：{varName}={home}" + (addedPath ? $"；已加入 PATH：{bin}" : ""));
        ctx?.Step($"已设置环境变量 {varName}={home}" + (addedPath ? "（并加入 PATH）" : ""));
    }

    /// <summary>The install root for a toolchain: the portable extract dir, else the folder above the
    /// tool's bin (found on the registry PATH or common roots after a winget install).</summary>
    private string? ResolveToolHome(CatalogItem item)
    {
        try
        {
            if (item.Install.Method == "portable" && item.Install.ExtractTo != null)
            {
                var d = _resolver.Resolve(item.InstallPathOverride ?? item.Install.ExtractTo);
                if (Directory.Exists(d)) return d.TrimEnd('\\', '/');
            }

            var cmd = item.Detect?.Cmd;
            if (string.IsNullOrWhiteSpace(cmd)) return null;
            var exe = FindToolExe(cmd!, item.Id);
            if (exe == null) return null;
            var dir = Path.GetDirectoryName(exe);
            if (dir == null) return null;
            return string.Equals(Path.GetFileName(dir), "bin", StringComparison.OrdinalIgnoreCase)
                ? Path.GetDirectoryName(dir) : dir;
        }
        catch { return null; }
    }

    /// <summary>Find a tool's exe right after a winget install: registry PATH (Machine+User, where winget
    /// writes — the current process env is stale), then common install roots for go / java.</summary>
    private static string? FindToolExe(string cmd, string id)
    {
        foreach (var target in new[] { EnvironmentVariableTarget.Machine, EnvironmentVariableTarget.User, EnvironmentVariableTarget.Process })
        {
            var path = Environment.GetEnvironmentVariable("PATH", target) ?? "";
            foreach (var dir in path.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                try { var p = Path.Combine(Environment.ExpandEnvironmentVariables(dir), cmd + ".exe"); if (File.Exists(p)) return p; }
                catch { /* bad path entry */ }
            }
        }

        var roots = id == "go"
            ? new[] { @"C:\Program Files\Go" }
            : new[] { @"C:\Program Files\Java", @"C:\Program Files\Eclipse Adoptium", @"C:\Program Files\Microsoft" };
        foreach (var root in roots)
        {
            try
            {
                if (File.Exists(Path.Combine(root, "bin", cmd + ".exe"))) return Path.Combine(root, "bin", cmd + ".exe");
                if (!Directory.Exists(root)) continue;
                foreach (var sub in Directory.GetDirectories(root))
                {
                    var p = Path.Combine(sub, "bin", cmd + ".exe");
                    if (File.Exists(p)) return p;
                }
            }
            catch { /* skip */ }
        }
        return null;
    }

    /// <summary>Persist (or clear) an item's custom install location after a successful install/update so
    /// it survives a restart. Only methods that have a meaningful install folder are remembered.</summary>
    private static void PersistInstallPath(CatalogItem item)
    {
        if (item.Install.Method is "portable" or "git" or "winget")
            SettingsStore.SetInstallPath(item.Id, item.InstallPathOverride);
    }

    /// <summary>Remember where a custom-located app actually lives (its exe's folder), discovered at
    /// detection time, so detection / launch / process-status keep working on later launches.</summary>
    private static void BackfillInstallPath(CatalogItem item, string exePath)
    {
        try
        {
            var dir = Path.GetDirectoryName(exePath);
            if (string.IsNullOrWhiteSpace(dir)) return;
            if (string.Equals(item.InstallPathOverride, dir, StringComparison.OrdinalIgnoreCase)) return;
            item.InstallPathOverride = dir;
            SettingsStore.SetInstallPath(item.Id, dir);
        }
        catch { /* best effort */ }
    }

    private async Task RefreshInstalledFlag(CatalogItem item)
    {
        var vm = Install.Groups.SelectMany(g => g.Items).FirstOrDefault(i => i.Id == item.Id);
        if (vm == null) return;
        try
        {
            var installed = await Detection.IsInstalledAsync(item, _resolver);
            if (!installed) installed = await Task.Run(() => StoreApps.HasMsixApp(item.Name));
            vm.Installed = installed;
            if (installed)
            {
                var exe = await Task.Run(() => Launcher.ResolveExePath(item, _resolver));
                if (exe != null) vm.SetIconFromExe(exe);
            }
        }
        catch { /* leave as-is */ }
    }

    private async Task RunAsync(List<CatalogItem> selected)
    {
        var dispatcher = Application.Current.Dispatcher;
        AuditLog.Action($"开始安装 {selected.Count} 项：{string.Join(", ", selected.Select(s => s.Id))}");
        var rows = selected.GroupBy(s => s.Id).ToDictionary(g => g.Key, g => Progress.Enqueue(g.Key, g.First().Name, g.First().Install.Method));

        await _opGate.WaitAsync();
        try
        {
            var ctx = NewCtx(out var ct);
            var plan = await _engine.BuildPlanAsync(selected, _resolver);
            Progress.BeginRun("安装", plan.Count(p => p.Status == PlanStatus.ToInstall));

            CatalogItem? current = null;
            var summary = await _engine.ApplyAsync(plan, ctx, dryRun: false,
                onStart: pi => { current = pi.Item; dispatcher.Invoke(() => { if (rows.TryGetValue(pi.Item.Id, out var r)) Progress.Start(r); }); },
                onDone: r =>
                {
                    if (r.Message != "already installed")
                        AuditLog.Action($"安装 {r.Item.Name}：{Zh(r.Status)} {r.Message}".TrimEnd());
                    if (r.Status == StepStatus.Ok) { PersistInstallPath(r.Item); ApplyToolEnv(r.Item); }
                    dispatcher.Invoke(() => { if (rows.TryGetValue(r.Item.Id, out var row)) Progress.Done(row, r.Status, r.Message); });
                });

            if (ct.IsCancellationRequested && current != null)
            {
                var freed = Cleanup.RemoveInstallResidue(current, _resolver);
                AuditLog.Action($"已取消安装 {current.Name}" + (freed > 0 ? $"，清理残留 {Mb(freed)}" : ""));
            }

            // Persist any rows abandoned by cancellation (still 排队/运行中) so they don't linger
            // on the page unrecorded.
            foreach (var row in rows.Values.Where(r => r.Kind is "queued" or "running"))
                dispatcher.Invoke(() => Progress.Done(row, StepStatus.Failed, "已取消"));

            AuditLog.Action($"安装结束 · 成功 {summary.Ok} · 失败 {summary.Failed} · 跳过 {summary.Skipped}");
            Progress.EndRun();

            Detection.ResetCache();
            Arp.Refresh();
            UpdateChecker.Reset();
            foreach (var it in selected) await RefreshInstalledFlag(it);
            await RunUpdateCheckAsync();
        }
        finally { _opGate.Release(); }
    }

    private void OnUpdateRequested()
    {
        if (_catalog == null) return;
        var items = Install.Groups.SelectMany(g => g.Items)
            .Where(i => i.IsSelected && i.IsInstalled && Updater.CanUpdate(i.Model))
            .Select(i => i.Model).ToList();
        if (items.Count == 0) return;
        if (MessageBox.Show($"是否更新选中的 {items.Count} 个软件？", "更新",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;

        SelectedNav = AllNavItems.First(n => ReferenceEquals(n.Page, Progress));
        _ = RunUpdatesAsync(items);
    }

    private async Task RunUpdatesAsync(List<CatalogItem> items)
    {
        var dispatcher = Application.Current.Dispatcher;
        AuditLog.Action($"开始更新 {items.Count} 项：{string.Join(", ", items.Select(i => i.Id))}");
        var rows = items.GroupBy(i => i.Id).ToDictionary(g => g.Key, g => Progress.Enqueue(g.Key, g.First().Name, g.First().Install.Method));

        await _opGate.WaitAsync();
        try
        {
            var ctx = NewCtx(out var ct);
            Progress.BeginRun("更新", items.Count);
            var ok = 0; var failed = 0;
            foreach (var item in items)
            {
                if (ct.IsCancellationRequested) break;
                var row = rows[item.Id];
                dispatcher.Invoke(() => Progress.Start(row));
                StepOutcome outcome;
                try { outcome = await Updater.UpdateAsync(item, ctx); }
                catch (OperationCanceledException) { outcome = StepOutcome.Fail("已取消"); }
                catch (Exception ex) { outcome = StepOutcome.Fail(ex.Message); }
                if (outcome.Status == StepStatus.Failed) failed++; else ok++;
                AuditLog.Action($"更新 {item.Name}：{Zh(outcome.Status)} {outcome.Message}".TrimEnd());
                dispatcher.Invoke(() => Progress.Done(row, outcome.Status, outcome.Message));
            }

            foreach (var row in rows.Values.Where(r => r.Kind is "queued" or "running"))
                dispatcher.Invoke(() => Progress.Done(row, StepStatus.Failed, "已取消"));

            Detection.ResetCache();
            Arp.Refresh();
            foreach (var i in items) DetailService.Invalidate(i.Id);
            UpdateChecker.Reset();
            AuditLog.Action($"更新结束 · 成功 {ok} · 失败 {failed}");
            Progress.EndRun();

            foreach (var i in items) await RefreshInstalledFlag(i);
            await RunUpdateCheckAsync();
        }
        finally { _opGate.Release(); }
    }
}
