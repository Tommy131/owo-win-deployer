using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using WinDeploy.App.Services;
using WinDeploy.Core;
using WinDeploy.Core.Config;
using WinDeploy.Core.Engine;
using WinDeploy.Core.Engine.Installers;
using WinDeploy.Core.I18n;
using WinDeploy.Core.Models;

namespace WinDeploy.App.ViewModels;

public sealed class MainViewModel : LocalizedObject
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
    public PowerViewModel Power { get; } = new();
    public MaintenanceViewModel Maintenance { get; } = new();
    public WslViewModel Wsl { get; } = new();
    public TweaksViewModel Tweaks { get; } = new();
    public AdvancedToolsViewModel AdvancedTools { get; } = new();
    public ServiceConfigViewModel ServiceConfig { get; } = new();
    public FtpViewModel Ftp { get; } = new();
    public CloudflareDdnsViewModel Cloudflare { get; } = new();

    public string AppName => WinDeploy.App.AppInfo.Name;
    public string WindowTitle => WinDeploy.App.AppInfo.TitleWithRole;
    public string Copyright => $"{WinDeploy.App.AppInfo.Copyright} · v{WinDeploy.App.AppInfo.Version}";

    /// <summary>Window chrome that contains localized fragments (admin suffix) refreshes on language switch.</summary>
    protected override void OnCultureChanged() => OnPropertyChanged(nameof(WindowTitle));

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
                var msg = Localizer.Format("update.selfFoundBody", WinDeploy.App.AppInfo.Name, r.Latest, r.Current);
                if (Dialogs.Show(msg, Localizer.T("settings.update.dialogTitle"), MessageBoxButton.YesNo, MessageBoxImage.Information) == MessageBoxResult.Yes)
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
        var dlg = new DevModeConfirmDialog { Owner = Application.Current.MainWindow };
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
    public void GoToCloudflare() => NavigateTo(Cloudflare);

    /// <summary>Developer mode is on — gates the tray's system-affecting entries (终端 / 服务 / 环境变量 /
    /// Cloudflare DDNS) so a non-developer can't reach them and risk the system. Read live by the tray on each open.</summary>
    public bool IsDeveloperMode => _devMode;

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
        // Fully qualified: Services.Net.ServiceConfig is the static service helper, not the `ServiceConfig` page-VM property.
        try { return Services.Net.ServiceConfig.Detect(_catalog, _resolver).Where(s => s.HasService).ToList(); }
        catch { return Array.Empty<ServerInfo>(); }
    }

    /// <summary>Run a service action from the tray (no UI navigation); report the result as a toast + audit.</summary>
    public void RunWebServiceAction(ServerInfo info, SvcAction action)
    {
        var verb = action switch { SvcAction.Start => Localizer.T("verb.svcStart"), SvcAction.Stop => Localizer.T("verb.svcStop"), SvcAction.Reload => Localizer.T("verb.svcReload"), _ => Localizer.T("verb.restart") };
        try
        {
            var (ok, msg) = Services.Net.ServiceConfig.Run(info, action);
            AuditLog.Action($"托盘：{verb} {info.Name} — {(ok ? "成功" : "失败")} {msg}".TrimEnd());
            ToastService.TryShow($"{verb} {info.Name}", ok ? (string.IsNullOrWhiteSpace(msg) ? Localizer.T("ops.tray.opOk") : msg) : Localizer.Format("ops.tray.opFail", msg));
        }
        catch (Exception ex)
        {
            AuditLog.Action($"托盘：{verb} {info.Name} 异常 — {ex.Message}");
            ToastService.TryShow($"{verb} {info.Name}", Localizer.Format("ops.tray.opError", ex.Message));
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
                var rt = await ServerManager.GetRuntimeAsync(s);
                map[s.Id] = (rt.Running, rt.Running ? Localizer.Format("ops.web.running", rt.PidText) : Localizer.T("ops.web.stopped"));
            }
            catch { map[s.Id] = (false, Localizer.T("ops.web.unknown")); }
        }
        lock (_webStatusLock) _webStatus = map;
    }

    /// <summary>Build the grouped, collapsible nav. Items flagged Advanced only show in 开发人员模式;
    /// items with a MinBuild only show on a high-enough Windows build. Built once; visibility toggled live.</summary>
    private void BuildNav()
    {
        NavGroups.Clear();

        var deploy = new NavGroupViewModel("", "nav.group.deploy");
        deploy.Items.Add(new("", "nav.installCenter", Install));
        deploy.Items.Add(new("", "nav.configSync", ConfigSync));
        deploy.Items.Add(new("", "nav.progress", Progress));
        deploy.Items.Add(new("", "nav.export", Export));

        var system = new NavGroupViewModel("", "nav.group.system");
        system.Items.Add(new("", "nav.systemOverview", SystemOverview));
        system.Items.Add(new("", "nav.power", Power, advanced: true));
        system.Items.Add(new("", "nav.maintenance", Maintenance, advanced: true));
        system.Items.Add(new("", "nav.processes", Processes, advanced: true));
        system.Items.Add(new("", "nav.startup", Startup, advanced: true));
        system.Items.Add(new("", "nav.envVars", EnvVars, advanced: true));

        var dev = new NavGroupViewModel("", "nav.group.dev");
        dev.Items.Add(new("", "nav.terminal", Terminal, advanced: true));
        dev.Items.Add(new("", "nav.serviceConfig", ServiceConfig, advanced: true));
        dev.Items.Add(new("", "nav.ftp", Ftp, advanced: true));
        dev.Items.Add(new("", "Cloudflare DDNS", Cloudflare, advanced: true));
        dev.Items.Add(new("", "nav.wsl", Wsl, advanced: true, minBuild: OsInfo.Win10_1607));
        dev.Items.Add(new("", "nav.tweaks", Tweaks, advanced: true));
        dev.Items.Add(new("", "nav.advancedTools", AdvancedTools, advanced: true));

        var misc = new NavGroupViewModel("", "nav.group.misc");
        misc.Items.Add(new("", "nav.logs", Logs));
        misc.Items.Add(new("", "nav.settings", Settings));

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
        set
        {
            // DetailViewModel is created per card-click (a LocalizedObject); dispose the outgoing one so it
            // doesn't stay rooted on the static CultureChanged event for the app's lifetime (memory leak).
            if (_current is DetailViewModel old && !ReferenceEquals(old, value)) old.Dispose();
            if (Set(ref _current, value)) UpdateInstallWatch();
        }
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
        if (dir == null) { Install.LoadError = Localizer.T("ops.loadError.noCatalog"); Install.IsLoading = false; return; }

        var path = Path.Combine(dir, "catalog.json");
        _repoRoot = Path.GetDirectoryName(dir)!;
        Terminal.WorkingDir = _repoRoot;
        try { _catalog = CatalogLoader.Load(path); }
        catch (Exception ex) { Install.LoadError = ex.Message; Install.IsLoading = false; return; }

        // Preload localized software summaries (en/de) so install cards switch language without a reload.
        foreach (var lang in new[] { "en", "de" })
            CatalogLoader.ApplyLocalizedSummaries(_catalog, dir, lang);

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
        Settings.IconNote = Localizer.T("ops.icon.fetching");
        var n = await BusyDialog.RunAsync(Application.Current.MainWindow, Localizer.T("ops.icon.busyTitle"),
            Localizer.T("ops.icon.busyBody"), () => FetchIconCacheAsync(items));
        Settings.IconNote = n > 0 ? Localizer.Format("ops.icon.doneN", n) : Localizer.T("ops.icon.none");
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
            var n = await IconCache.FetchMissingAsync(need, _repoRoot);
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
        var labels = PhpVersions.Select(v => v.Label + (IsPhpVersionInstalled(v.Version) ? Localizer.T("ops.status.installed") : "")).ToList();
        var dlg = new ChoiceDialog(Localizer.T("ops.php.pickTitle"),
            Localizer.T("ops.php.pickBody"),
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
        var choice = Dialogs.Show(
            Localizer.Format("ops.uninstall.body", item.Name),
            Localizer.T("verb.uninstall"), MessageBoxButton.YesNoCancel, MessageBoxImage.Warning);
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
        else Dialogs.Show(Localizer.Format("ops.openDir.fail", item.Name), Localizer.T("install.ctx.openDir"), MessageBoxButton.OK, MessageBoxImage.Information);
    }

    /// <summary>Open the software's homepage. Every item supports this: prefer the catalog homepage, then a
    /// GitHub repo URL (for git-installed apps), and finally fall back to a web search of the name — so the
    /// 「前往官网」menu entry is never a dead click, even for items without a configured homepage.</summary>
    private static void OpenHomepage(CatalogItem item)
    {
        var url = FirstHttpUrl(item.Homepage, item.Install?.Repo)
                  ?? "https://www.bing.com/search?q=" + Uri.EscapeDataString((item.Name + Localizer.T("ops.homepageSearchSuffix")).Trim());
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }); }
        catch { /* ignore */ }
    }

    private static string? FirstHttpUrl(params string?[] candidates)
        => candidates.FirstOrDefault(c => !string.IsNullOrWhiteSpace(c) && c.StartsWith("http", StringComparison.OrdinalIgnoreCase));

    private async Task ConfirmRiskAndRun(CatalogItem item, string op)
    {
        var verb = Verb(op);
        var detail = op == "stop" ? Localizer.T("ops.risk.stopDetail") : op == "restart" ? Localizer.T("ops.risk.restartDetail") : "";
        if (Dialogs.Show(Localizer.Format("ops.risk.body", verb, item.Name, detail).TrimEnd(),
                verb, MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        await RunQuickOpAsync(item, op);
    }

    private async Task ConfirmUpdateAndRun(CatalogItem item)
    {
        if (Dialogs.Show(Localizer.Format("ops.update.confirmBody", item.Name),
                Localizer.T("verb.update"), MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
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
            Dialogs.Show(Localizer.Format("ops.toolchain.fetchFail", title, ex.Message),
                title, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (variants.Count == 0)
        {
            Dialogs.Show(Localizer.Format("ops.toolchain.noArch", title), title, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var labels = variants.Select(v => v.Label + (v.Recommended ? Localizer.T("ops.status.recommended") : "")).ToList();
        var rec = variants.FindIndex(v => v.Recommended);
        var arch = Environment.Is64BitOperatingSystem ? Localizer.T("ops.arch.x64") : Localizer.T("ops.arch.x86");
        var dlg = new ChoiceDialog(Localizer.Format("ops.toolchain.pickTitle", title),
            Localizer.Format("ops.toolchain.pickBody", arch),
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
            Dialogs.Show(Localizer.Format("ops.gh.badRepo", item.Install.Repo ?? item.Homepage ?? ""), item.Name,
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        List<GhRelease> releases;
        try { releases = await GitHub.ReleasesAsync(repo); }
        catch (Exception ex)
        {
            Dialogs.Show(Localizer.Format("ops.gh.relFail", item.Name, ex.Message),
                item.Name, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (releases.Count == 0)
        {
            Dialogs.Show(Localizer.Format("ops.gh.noReleases", item.Name, repo), item.Name, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var tagLabels = releases.Select(r =>
            r.Tag + (r.Prerelease ? Localizer.T("ops.gh.prerelease") : "") +
            (string.IsNullOrWhiteSpace(r.Name) || r.Name == r.Tag ? "" : "  · " + r.Name)).ToList();
        var dlgTag = new ChoiceDialog(Localizer.Format("ops.gh.pickTagTitle", item.Name),
            Localizer.Format("ops.gh.pickTagBody", repo, releases.Count),
            tagLabels, 0) { Owner = Application.Current.MainWindow };
        if (dlgTag.ShowDialog() != true || dlgTag.SelectedIndex < 0) return;
        var rel = releases[dlgTag.SelectedIndex];

        if (rel.Assets.Count == 0)
        {
            Dialogs.Show(Localizer.Format("ops.gh.noAssets", rel.Tag), item.Name, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        // 按系统平台过滤掉用不上的发行版（如 x64 机器上的 arm64）；过滤后为空则全部列出。
        // 再按当前 CPU 架构排序：匹配当前架构的排最前，作为默认选中项。
        var assets = rel.Assets.Where(a => WinDeploy.Core.Util.Arch.AssetUsable(a.Name)).ToList();
        if (assets.Count == 0) assets = rel.Assets.ToList();
        assets = assets.OrderBy(a => WinDeploy.Core.Util.Arch.PreferScore(a.Name)).ToList();

        var assetLabels = assets.Select(a => Localizer.Format("ops.assetLabel", a.Name, Mb(a.Size))).ToList();
        var dlgAsset = new ChoiceDialog(Localizer.Format("ops.gh.pickAssetTitle", rel.Tag),
            Localizer.T("ops.gh.pickAssetBody"),
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
        if (Dialogs.Show(Localizer.Format("ops.downgrade.body", item.Name, item.Version ?? ""),
                Localizer.T("verb.downgrade"), MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        await RunOpAsync(item, "downgrade");
    }

    private static string Zh(StepStatus s) => s switch
    {
        StepStatus.Ok => "成功", StepStatus.Failed => "失败", _ => "跳过",
    };

    private static string Verb(string op) => op switch
    {
        "install" => Localizer.T("verb.install"), "update" => Localizer.T("verb.update"),
        "uninstall" => Localizer.T("verb.uninstall"), "downgrade" => Localizer.T("verb.downgrade"),
        "launch" => Localizer.T("verb.launch"), "stop" => Localizer.T("verb.stop"),
        "restart" => Localizer.T("verb.restart"), _ => Localizer.T("verb.generic"),
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
                    _ => StepOutcome.Fail(Localizer.T("ops.result.unknownOp")),
                };
            }
            catch (OperationCanceledException) { cancelled = true; ctx.Step(Localizer.T("ops.step.userCancelled")); outcome = StepOutcome.Fail(Localizer.T("ops.result.cancelled")); }
            catch (Exception ex) { ctx.Step(Localizer.Format("ops.step.error", ex.Message)); outcome = StepOutcome.Fail(ex.Message); }

            if (cancelled && op == "install")
            {
                var freed = Cleanup.RemoveInstallResidue(item, _resolver);
                var msg = freed > 0 ? Localizer.Format("ops.cancel.cleaned", Mb(freed)) : Localizer.T("ops.cancel.noResidue");
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
                _ => StepOutcome.Fail(Localizer.T("ops.result.unknownOp")),
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
        var lines = string.Join("\n", junk.Select(j => Localizer.Format("ops.leftover.line", j.Path, j.SizeText)));
        var msg = Localizer.Format("ops.leftover.body", item.Name, junk.Count, Mb(total), lines);
        if (Dialogs.Show(msg, Localizer.T("ops.leftover.title"), MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;
        var (count, freed) = LeftoverScanner.Delete(junk);
        AuditLog.Action($"清理残留 {item.Name}：删除 {count} 处，释放 {Mb(freed)}");
        Dialogs.Show(Localizer.Format("ops.leftover.done", count, Mb(freed)), Localizer.T("ops.leftover.title"),
            MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private static string Mb(long bytes) => bytes >= 1024L * 1024 * 1024
        ? $"{bytes / 1024.0 / 1024 / 1024:0.0} GB"
        : $"{bytes / 1024.0 / 1024:0.0} MB";

    private StepOutcome LaunchOp(CatalogItem item, EngineContext ctx)
    {
        var ok = Launcher.TryLaunch(item, _resolver, out var detail, ctx.Step);
        return ok ? StepOutcome.Done(Localizer.Format("ops.toolchain.launched", detail)) : StepOutcome.Fail(detail);
    }

    private StepOutcome StopOp(CatalogItem item, EngineContext ctx)
    {
        var procs = ProcessControl.Find(item, _resolver);
        ctx.Step(Localizer.Format("ops.step.foundProcs", procs.Count));
        var n = 0;
        foreach (var p in procs)
            if (ProcessControl.Kill(p.Pid)) { n++; ctx.Step(Localizer.Format("ops.step.killed", p.Name, p.Pid)); }
        return StepOutcome.Done(n > 0 ? Localizer.Format("ops.stop.done", n) : Localizer.T("ops.stop.none"));
    }

    private async Task<StepOutcome> RestartOp(CatalogItem item, EngineContext ctx)
    {
        var n = await Task.Run(() =>
        {
            var k = 0;
            foreach (var p in ProcessControl.Find(item, _resolver)) if (ProcessControl.Kill(p.Pid)) k++;
            return k;
        });
        ctx.Step(Localizer.Format("ops.step.killedN", n));
        await Task.Delay(600);
        var (ok, detail) = await Task.Run(() =>
        {
            var r = Launcher.TryLaunch(item, _resolver, out var d, ctx.Step);
            return (r, d);
        });
        return ok
            ? StepOutcome.Done(Localizer.Format("ops.restart.done", n))
            : StepOutcome.Fail(Localizer.Format("ops.restart.fail", detail));
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
        if (home == null) { ctx?.Step(Localizer.Format("ops.env.noPath", varName)); return; }

        WinDeploy.Core.Util.EnvPath.SetUserVar(varName, home);
        Environment.SetEnvironmentVariable(varName, home);   // reflect into this process
        var bin = Path.Combine(home, "bin");
        var addedPath = Directory.Exists(bin) && WinDeploy.Core.Util.EnvPath.AddToUserPath(bin);
        AuditLog.Action($"环境变量：{varName}={home}" + (addedPath ? $"；已加入 PATH：{bin}" : ""));
        ctx?.Step(Localizer.Format("ops.env.set", varName, home) + (addedPath ? Localizer.T("ops.env.andPath") : ""));
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
            Progress.BeginRun(Localizer.T("verb.install"), plan.Count(p => p.Status == PlanStatus.ToInstall));

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
                dispatcher.Invoke(() => Progress.Done(row, StepStatus.Failed, Localizer.T("ops.result.cancelled")));

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
        if (Dialogs.Show(Localizer.Format("ops.update.confirmManyBody", items.Count), Localizer.T("verb.update"),
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
            Progress.BeginRun(Localizer.T("verb.update"), items.Count);
            var ok = 0; var failed = 0;
            foreach (var item in items)
            {
                if (ct.IsCancellationRequested) break;
                var row = rows[item.Id];
                dispatcher.Invoke(() => Progress.Start(row));
                StepOutcome outcome;
                try { outcome = await Updater.UpdateAsync(item, ctx); }
                catch (OperationCanceledException) { outcome = StepOutcome.Fail(Localizer.T("ops.result.cancelled")); }
                catch (Exception ex) { outcome = StepOutcome.Fail(ex.Message); }
                if (outcome.Status == StepStatus.Failed) failed++; else ok++;
                AuditLog.Action($"更新 {item.Name}：{Zh(outcome.Status)} {outcome.Message}".TrimEnd());
                dispatcher.Invoke(() => Progress.Done(row, outcome.Status, outcome.Message));
            }

            foreach (var row in rows.Values.Where(r => r.Kind is "queued" or "running"))
                dispatcher.Invoke(() => Progress.Done(row, StepStatus.Failed, Localizer.T("ops.result.cancelled")));

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
