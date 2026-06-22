using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
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

    public ObservableCollection<NavItemViewModel> NavItems { get; } = new();
    public InstallCenterViewModel Install { get; } = new();
    public ProgressViewModel Progress { get; } = new();
    public ConfigSyncViewModel ConfigSync { get; } = new();
    public ExportViewModel Export { get; } = new();
    public EnvVarsViewModel EnvVars { get; } = new();
    public TerminalViewModel Terminal { get; } = new();
    public ProcessManagerViewModel Processes { get; } = new();
    public LogViewModel Logs { get; } = new();
    public SettingsViewModel Settings { get; } = new();

    public MainViewModel()
    {
        Install.StartRequested += OnStartRequested;
        Install.UpdateRequested += OnUpdateRequested;
        Install.DetailRequested += OnDetailRequested;
        Install.LaunchRequested += item => _ = RunOpAsync(item.Model, "launch");
        Install.RefreshRequested += () => _ = DetectAllAsync();
        Processes.OperationRequested += (item, op) => _ = ConfirmRiskAndRun(item, op);
        Progress.CancelRequested += () => _cts?.Cancel();
        Settings.Saved += () => Secrets.ExtraKeywords = SettingsViewModel.ParseKeywords(Settings.RedactKeywords);
        Load();

        NavItems.Add(new NavItemViewModel("", "软件安装中心", Install));
        NavItems.Add(new NavItemViewModel("", "配置同步", ConfigSync));
        NavItems.Add(new NavItemViewModel("", "运行进度", Progress));
        NavItems.Add(new NavItemViewModel("", "进程管理", Processes));
        NavItems.Add(new NavItemViewModel("", "导出", Export));
        NavItems.Add(new NavItemViewModel("", "环境变量", EnvVars));
        NavItems.Add(new NavItemViewModel("", "终端", Terminal));
        NavItems.Add(new NavItemViewModel("", "日志", Logs));
        NavItems.Add(new NavItemViewModel("", "设置", Settings));
        SelectedNav = NavItems[0];
    }

    private NavItemViewModel? _selectedNav;
    public NavItemViewModel? SelectedNav
    {
        get => _selectedNav;
        set { if (Set(ref _selectedNav, value) && value != null) Current = value.Page; }
    }

    private object? _current;
    public object? Current { get => _current; set => Set(ref _current, value); }

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
        _resolver = new PathResolver(vars);
        Secrets.ExtraKeywords = SettingsViewModel.ParseKeywords(settings.RedactKeywords);

        Install.Initialize(_catalog, dir);
        ConfigSync.Initialize(_catalog, _resolver, _repoRoot);
        Export.Initialize(_catalog, _resolver, _repoRoot);
        Processes.Initialize(_catalog, _resolver, _repoRoot);
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
        using var gate = new SemaphoreSlim(8);
        var tasks = items.Select(async vm =>
        {
            await gate.WaitAsync();
            try { vm.Installed = await Detection.IsInstalledAsync(vm.Model, _resolver); }
            catch { vm.Installed = false; }
            finally { gate.Release(); }
        });
        await Task.WhenAll(tasks);

        await RunUpdateCheckAsync(items);   // badge installed apps that have an upgrade

        Install.IsLoading = false;
        _ = PrefetchDetailsAsync(items);
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

        SelectedNav = NavItems.First(n => ReferenceEquals(n.Page, Progress));
        _ = RunAsync(selected);
    }

    private void OnDetailRequested(AppItemViewModel item)
    {
        var vm = new DetailViewModel(item, _resolver, back: () => Current = Install);
        vm.InstallRequested += m => _ = RunOpAsync(m, "install");
        vm.UpdateRequested += m => _ = ConfirmUpdateAndRun(m);
        vm.DowngradeRequested += m => _ = ConfirmDowngradeAndRun(m);
        vm.UninstallRequested += (m, purge) => _ = RunOpAsync(m, "uninstall", purge);
        vm.LaunchRequested += m => _ = RunOpAsync(m, "launch");
        vm.StopRequested += m => _ = ConfirmRiskAndRun(m, "stop");
        vm.RestartRequested += m => _ = ConfirmRiskAndRun(m, "restart");
        Current = vm;
    }

    private async Task ConfirmRiskAndRun(CatalogItem item, string op)
    {
        var verb = Verb(op);
        var detail = op == "stop" ? "将结束该软件的所有进程。" : op == "restart" ? "将结束并重新启动该软件。" : "";
        if (MessageBox.Show($"确定要{verb} {item.Name}？\n\n{detail}".TrimEnd(),
                verb, MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        await RunOpAsync(item, op);
    }

    private async Task ConfirmUpdateAndRun(CatalogItem item)
    {
        if (MessageBox.Show($"是否检查并更新 {item.Name}？\n\n（若已是最新版本会提示「已是最新」）",
                "更新", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        await RunOpAsync(item, "update");
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
        SelectedNav = NavItems.First(n => ReferenceEquals(n.Page, Progress));
        var verb = Verb(op);
        var pi = new PlanItem { Item = item, Status = PlanStatus.ToInstall };
        Progress.Begin(new[] { pi }, verb);
        Progress.OnStart(pi);

        var ctx = NewCtx(out var ct);
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
                "launch" => await Task.Run(() => LaunchOp(item)),
                "stop" => await Task.Run(() => StopOp(item)),
                "restart" => await RestartOp(item),
                _ => StepOutcome.Fail("未知操作"),
            };
        }
        catch (OperationCanceledException) { cancelled = true; outcome = StepOutcome.Fail("已取消"); }
        catch (Exception ex) { outcome = StepOutcome.Fail(ex.Message); }

        if (cancelled && op == "install")
        {
            var freed = Cleanup.RemoveInstallResidue(item, _resolver);
            outcome = StepOutcome.Fail(freed > 0 ? $"已取消，已清理残留 {Mb(freed)}" : "已取消（无残留）");
        }

        var res = new RunResult { Item = item, Status = outcome.Status, Message = outcome.Message };
        AuditLog.Action($"{verb} {item.Name}：{Zh(res.Status)} {res.Message}".TrimEnd());
        Progress.OnDone(res);
        Progress.Complete();

        if (op is "install" or "update" or "uninstall" or "downgrade")
        {
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
            Report = msg => disp.Invoke(() => Progress.OnStep(msg)),
        };
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

    private StepOutcome LaunchOp(CatalogItem item)
    {
        var ok = Launcher.TryLaunch(item, _resolver, out var detail);
        return ok ? StepOutcome.Done("已启动 " + detail) : StepOutcome.Fail(detail);
    }

    private StepOutcome StopOp(CatalogItem item)
    {
        var n = ProcessControl.KillAll(item, _resolver);
        return StepOutcome.Done(n > 0 ? $"已结束 {n} 个进程" : "没有正在运行的进程");
    }

    private async Task<StepOutcome> RestartOp(CatalogItem item)
    {
        var n = await Task.Run(() => ProcessControl.KillAll(item, _resolver));
        await Task.Delay(600);
        var (ok, detail) = await Task.Run(() =>
        {
            var r = Launcher.TryLaunch(item, _resolver, out var d);
            return (r, d);
        });
        return ok
            ? StepOutcome.Done($"已重启（结束 {n} 个进程后启动）")
            : StepOutcome.Fail("重启失败：" + detail);
    }

    private async Task RefreshInstalledFlag(CatalogItem item)
    {
        var vm = Install.Groups.SelectMany(g => g.Items).FirstOrDefault(i => i.Id == item.Id);
        if (vm == null) return;
        try { vm.Installed = await Detection.IsInstalledAsync(item, _resolver); }
        catch { /* leave as-is */ }
    }

    private async Task RunAsync(List<CatalogItem> selected)
    {
        var dispatcher = Application.Current.Dispatcher;
        AuditLog.Action($"开始安装 {selected.Count} 项：{string.Join(", ", selected.Select(s => s.Id))}");
        var ctx = NewCtx(out var ct);
        var plan = await _engine.BuildPlanAsync(selected, _resolver);
        Progress.Begin(plan);

        CatalogItem? current = null;
        var summary = await _engine.ApplyAsync(plan, ctx, dryRun: false,
            onStart: pi => { current = pi.Item; dispatcher.Invoke(() => Progress.OnStart(pi)); },
            onDone: r =>
            {
                if (r.Message != "already installed")
                    AuditLog.Action($"安装 {r.Item.Name}：{Zh(r.Status)} {r.Message}".TrimEnd());
                dispatcher.Invoke(() => Progress.OnDone(r));
            });

        if (ct.IsCancellationRequested && current != null)
        {
            var freed = Cleanup.RemoveInstallResidue(current, _resolver);
            AuditLog.Action($"已取消安装 {current.Name}" + (freed > 0 ? $"，清理残留 {Mb(freed)}" : ""));
        }

        AuditLog.Action($"安装结束 · 成功 {summary.Ok} · 失败 {summary.Failed} · 跳过 {summary.Skipped}");
        dispatcher.Invoke(() => Progress.Complete());

        Detection.ResetCache();
        Arp.Refresh();
        UpdateChecker.Reset();
        foreach (var it in selected) await RefreshInstalledFlag(it);
        await RunUpdateCheckAsync();
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

        SelectedNav = NavItems.First(n => ReferenceEquals(n.Page, Progress));
        _ = RunUpdatesAsync(items);
    }

    private async Task RunUpdatesAsync(List<CatalogItem> items)
    {
        var dispatcher = Application.Current.Dispatcher;
        AuditLog.Action($"开始更新 {items.Count} 项：{string.Join(", ", items.Select(i => i.Id))}");
        var plan = items.Select(i => new PlanItem { Item = i, Status = PlanStatus.ToInstall }).ToList();
        Progress.Begin(plan, verb: "更新");

        var ctx = NewCtx(out var ct);
        var ok = 0; var failed = 0;
        foreach (var pi in plan)
        {
            if (ct.IsCancellationRequested) break;
            dispatcher.Invoke(() => Progress.OnStart(pi));
            RunResult res;
            try
            {
                var outcome = await Updater.UpdateAsync(pi.Item, ctx);
                res = new RunResult { Item = pi.Item, Status = outcome.Status, Message = outcome.Message };
            }
            catch (OperationCanceledException)
            {
                res = new RunResult { Item = pi.Item, Status = StepStatus.Failed, Message = "已取消" };
            }
            catch (Exception ex)
            {
                res = new RunResult { Item = pi.Item, Status = StepStatus.Failed, Message = ex.Message };
            }
            if (res.Status == StepStatus.Failed) failed++; else ok++;
            AuditLog.Action($"更新 {pi.Item.Name}：{Zh(res.Status)} {res.Message}".TrimEnd());
            dispatcher.Invoke(() => Progress.OnDone(res));
        }

        Detection.ResetCache();
        Arp.Refresh();
        foreach (var i in items) DetailService.Invalidate(i.Id);
        UpdateChecker.Reset();
        AuditLog.Action($"更新结束 · 成功 {ok} · 失败 {failed}");
        dispatcher.Invoke(() => Progress.Complete());

        foreach (var i in items) await RefreshInstalledFlag(i);
        await RunUpdateCheckAsync();
    }
}
