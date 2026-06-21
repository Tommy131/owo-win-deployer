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

    public ObservableCollection<NavItemViewModel> NavItems { get; } = new();
    public InstallCenterViewModel Install { get; } = new();
    public ProgressViewModel Progress { get; } = new();
    public ConfigSyncViewModel ConfigSync { get; } = new();
    public ExportViewModel Export { get; } = new();
    public SettingsViewModel Settings { get; } = new();

    public MainViewModel()
    {
        Install.StartRequested += OnStartRequested;
        Install.DetailRequested += OnDetailRequested;
        Settings.Saved += () => Secrets.ExtraKeywords = SettingsViewModel.ParseKeywords(Settings.RedactKeywords);
        Load();

        NavItems.Add(new NavItemViewModel("", "软件安装中心", Install));
        NavItems.Add(new NavItemViewModel("", "配置同步", ConfigSync));
        NavItems.Add(new NavItemViewModel("", "运行进度", Progress));
        NavItems.Add(new NavItemViewModel("", "导出", Export));
        NavItems.Add(new NavItemViewModel("", "设置", Settings));
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
        _ = DetectAllAsync();
    }

    /// <summary>Detect every item (throttled-parallel) BEFORE revealing the list.</summary>
    private async Task DetectAllAsync()
    {
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
        Install.IsLoading = false;
        _ = PrefetchDetailsAsync(items);
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
        => Current = new DetailViewModel(item, back: () => Current = Install);

    private async Task RunAsync(List<CatalogItem> selected)
    {
        var dispatcher = Application.Current.Dispatcher;
        var plan = await _engine.BuildPlanAsync(selected, _resolver);
        Progress.Begin(plan);

        var ctx = new EngineContext { Path = _resolver, RepoRoot = _repoRoot, Ct = CancellationToken.None };
        await _engine.ApplyAsync(plan, ctx, dryRun: false,
            onStart: pi => dispatcher.Invoke(() => Progress.OnStart(pi)),
            onDone: r => dispatcher.Invoke(() => Progress.OnDone(r)));

        dispatcher.Invoke(() => Progress.Complete());
    }
}
