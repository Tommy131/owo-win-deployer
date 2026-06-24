using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using WinDeploy.App.Services;
using WinDeploy.Core;
using WinDeploy.Core.I18n;
using WinDeploy.Core.Models;

namespace WinDeploy.App.ViewModels.Sys;

/// <summary>One process row (live-updated memory + CPU%). Carries its exe path so the row's right-click
/// menu (打开文件位置 / 属性) works per-process — including a group's sub-processes.</summary>
public sealed class ProcRowViewModel : ObservableObject
{
    public int Pid { get; }
    public string Name { get; }

    public ProcRowViewModel(int pid, string name, long memBytes, double cpu, string? path)
    {
        Pid = pid;
        Name = name;
        Path = path;
        Update(memBytes, cpu);
    }

    /// <summary>Full module path of the process, or null when it couldn't be read (protected / denied).</summary>
    public string? Path { get; private set; }
    public bool HasPath => !string.IsNullOrEmpty(Path);

    public long MemBytes { get; private set; }

    private string _mem = "—";
    public string MemText { get => _mem; private set => Set(ref _mem, value); }

    private string _cpu = "0.0%";
    public string CpuText { get => _cpu; private set => Set(ref _cpu, value); }

    public void Update(long memBytes, double cpu)
    {
        MemBytes = memBytes;
        MemText = memBytes > 0 ? $"{memBytes / 1024.0 / 1024:0.0} MB" : "—";
        CpuText = $"{cpu:0.0}%";
    }

    /// <summary>Fill in the path once it becomes readable (e.g. resolved on a later tick).</summary>
    public void EnsurePath(string? path)
    {
        if (HasPath || string.IsNullOrEmpty(path)) return;
        Path = path;
        OnPropertyChanged(nameof(Path));
        OnPropertyChanged(nameof(HasPath));
    }
}

/// <summary>A process group (a catalog app, or — in 全部进程 mode — any process by name), its icon, and
/// its running processes (expand/collapse).</summary>
public sealed class AppProcGroupViewModel : ObservableObject
{
    /// <summary>The catalog item this group maps to, or null for a generic (non-catalog) process group.</summary>
    public CatalogItem? Model { get; }
    public string Key { get; }
    public string Name { get; }
    public bool IsCatalog => Model != null;
    public ObservableCollection<ProcRowViewModel> Processes { get; } = new();
    public RelayCommand ToggleCommand { get; }

    public AppProcGroupViewModel(string key, string name, CatalogItem? model, string repoRoot)
    {
        Key = key;
        Name = name;
        Model = model;
        ToggleCommand = new RelayCommand(_ => IsExpanded = !IsExpanded);
        LoadIcon(repoRoot);
    }

    private bool _isExpanded = true;
    public bool IsExpanded
    {
        get => _isExpanded;
        set { if (Set(ref _isExpanded, value)) { OnPropertyChanged(nameof(ExpandGlyph)); OnPropertyChanged(nameof(ExpandMenuLabel)); } }
    }
    public string ExpandGlyph => _isExpanded ? "▼" : "▶";
    /// <summary>Header right-click label for the toggle item — mirrors <see cref="ExpandGlyph"/>.</summary>
    public string ExpandMenuLabel => _isExpanded ? Localizer.T("proc.group.collapse") : Localizer.T("proc.group.expand");

    public string CountText => Localizer.Format("proc.count", Processes.Count);
    public void RaiseCount() => OnPropertyChanged(nameof(CountText));

    /// <summary>True when this group's processes all run outside the interactive session (services / SYSTEM)
    /// — used to place it in the 系统进程 section. Only meaningful in 全部进程 mode.</summary>
    public bool IsSystemGroup { get; set; }

    private bool _showSectionHeader;
    public bool ShowSectionHeader { get => _showSectionHeader; private set => Set(ref _showSectionHeader, value); }

    private string _sectionLabel = "";
    public string SectionLabel { get => _sectionLabel; private set => Set(ref _sectionLabel, value); }

    /// <summary>Set the Task-Manager-style section divider rendered above this group (only the first group of
    /// each section shows one).</summary>
    public void SetSection(bool show, string label)
    {
        ShowSectionHeader = show;
        SectionLabel = label;
    }

    public ImageSource? IconImage { get; private set; }
    public bool HasIcon => IconImage != null;
    public bool ShowLetter => IconImage == null;
    public string Badge => Name.Length > 0 ? Name[..1].ToUpperInvariant() : "?";

    private void LoadIcon(string repoRoot) => IconImage = Model != null ? IconResolver.FromCatalogId(Model.Id) : null;

    /// <summary>If no bundled icon was found, extract the running app's real icon from its .exe. Catalog
    /// groups stay embedded-only (never overwrite a brand icon with a blank shell icon); generic (non-catalog)
    /// process groups take ANY icon — embedded, else the shell's associated icon — so they're recognizable.</summary>
    public void EnsureIconFromExe(string? exePath)
    {
        if (IconImage != null || string.IsNullOrWhiteSpace(exePath)) return;
        try
        {
            IconImage = IsCatalog ? IconExtractor.FromExe(exePath) : IconExtractor.FromExeAnyIcon(exePath);
            OnPropertyChanged(nameof(IconImage));
            OnPropertyChanged(nameof(HasIcon));
            OnPropertyChanged(nameof(ShowLetter));
        }
        catch { /* letter fallback */ }
    }
}

/// <summary>The "进程管理" page: a task-manager scoped to the catalog. Per-software icon + expandable
/// process tree, live (2 s) memory / CPU%, end one / end all / restart, and a shortcut to Windows
/// Task Manager. End-all / restart route through the run-progress page; single ends are immediate.</summary>
public sealed class ProcessManagerViewModel : LocalizedObject
{
    private Catalog? _catalog;
    private PathResolver _resolver = new(new Dictionary<string, string>());
    private string _repoRoot = "";

    private readonly Dictionary<string, (string Proc, string? Dir)> _targets = new();
    private readonly Dictionary<int, (TimeSpan Cpu, DateTime T)> _prevCpu = new();
    private readonly Dictionary<int, string?> _pathCache = new();
    private readonly Dictionary<string, AppProcGroupViewModel> _groups = new();
    private DispatcherTimer? _timer;
    private bool _scanning;

    public ObservableCollection<AppProcGroupViewModel> Groups { get; } = new();
    public RelayCommand RefreshCommand { get; }
    public RelayCommand EndProcessCommand { get; }
    public RelayCommand EndAllCommand { get; }
    public RelayCommand RestartCommand { get; }
    public RelayCommand OpenTaskManagerCommand { get; }
    public RelayCommand ToggleAllCommand { get; }
    public RelayCommand ExpandCollapseAllCommand { get; }
    public RelayCommand OpenFileLocationCommand { get; }
    public RelayCommand ShowPropertiesCommand { get; }
    public RelayCommand OpenGroupFileLocationCommand { get; }
    public RelayCommand ShowGroupPropertiesCommand { get; }

    /// <summary>Group-level 结束全部 / 重启 — (item, "stop" | "restart"). Only fires for catalog groups.</summary>
    public event Action<CatalogItem, string>? OperationRequested;

    private bool _showAll;
    /// <summary>When true, show every running process (grouped by name); else only catalog-software processes.</summary>
    public bool ShowAll { get => _showAll; private set { if (Set(ref _showAll, value)) OnPropertyChanged(nameof(ToggleAllLabel)); } }
    public string ToggleAllLabel => _showAll ? Localizer.T("proc.toggle.lib") : Localizer.T("proc.toggle.all");

    private bool _allExpanded = true;
    public string ExpandAllLabel => _allExpanded ? Localizer.T("proc.expandAll.collapse") : Localizer.T("proc.expandAll.expand");

    public ProcessManagerViewModel()
    {
        RefreshCommand = new RelayCommand(_ => _ = RefreshAsync(resolveTargets: true));
        EndProcessCommand = new RelayCommand(p => { if (p is ProcRowViewModel r) EndOne(r); });
        EndAllCommand = new RelayCommand(p => { if (p is AppProcGroupViewModel g) EndAll(g); });
        RestartCommand = new RelayCommand(p => { if (p is AppProcGroupViewModel { Model: { } m }) OperationRequested?.Invoke(m, "restart"); });
        OpenTaskManagerCommand = new RelayCommand(_ => OpenTaskManager());
        OpenFileLocationCommand = new RelayCommand(p => { if (p is ProcRowViewModel r) ShellOps.RevealInExplorer(r.Path); },
                                                  p => p is ProcRowViewModel { HasPath: true });
        ShowPropertiesCommand = new RelayCommand(p => { if (p is ProcRowViewModel r) ShellOps.ShowProperties(r.Path); },
                                                 p => p is ProcRowViewModel { HasPath: true });
        // Group-header (main process) variants — act on the group's primary path-bearing process.
        OpenGroupFileLocationCommand = new RelayCommand(p => { if (p is AppProcGroupViewModel g) ShellOps.RevealInExplorer(PrimaryPath(g)); },
                                                        p => p is AppProcGroupViewModel g && PrimaryPath(g) != null);
        ShowGroupPropertiesCommand = new RelayCommand(p => { if (p is AppProcGroupViewModel g) ShellOps.ShowProperties(PrimaryPath(g)); },
                                                      p => p is AppProcGroupViewModel g && PrimaryPath(g) != null);
        ToggleAllCommand = new RelayCommand(_ =>
        {
            ShowAll = !ShowAll;
            Groups.Clear(); _groups.Clear(); _prevCpu.Clear();
            _ = RefreshAsync(resolveTargets: true);
        });
        ExpandCollapseAllCommand = new RelayCommand(_ =>
        {
            _allExpanded = !_allExpanded;
            foreach (var g in Groups) g.IsExpanded = _allExpanded;
            OnPropertyChanged(nameof(ExpandAllLabel));
        });
    }

    protected override void OnCultureChanged()
    {
        base.OnCultureChanged();
        OnPropertyChanged(nameof(ToggleAllLabel));
        OnPropertyChanged(nameof(ExpandAllLabel));
        // Re-localize live group / process rows (count text, expand label, section divider).
        foreach (var g in Groups)
        {
            g.RaiseAllPropertiesChanged();
            foreach (var p in g.Processes) p.RaiseAllPropertiesChanged();
        }
    }

    public void Initialize(Catalog catalog, PathResolver resolver, string repoRoot)
    {
        _catalog = catalog;
        _resolver = resolver;
        _repoRoot = repoRoot;
    }

    private string _summary = Localizer.T("proc.scanning");
    public string Summary { get => _summary; private set => Set(ref _summary, value); }
    public bool IsEmpty => Groups.Count == 0;

    /// <summary>Start the live 2-second refresh (call when the page becomes visible).</summary>
    public void StartLive()
    {
        _timer ??= new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _timer.Tick -= OnTick;
        _timer.Tick += OnTick;
        _ = RefreshAsync(resolveTargets: true);
        _timer.Start();
    }

    public void StopLive() => _timer?.Stop();

    private void OnTick(object? sender, EventArgs e) => _ = RefreshAsync(resolveTargets: false);

    public async Task RefreshAsync(bool resolveTargets)
    {
        if (_catalog == null || _scanning) return;
        _scanning = true;
        try
        {
            List<(string Key, string Name, CatalogItem? Model, List<ProcItem> Procs)> grouped;
            var cache = _pathCache;

            if (_showAll)
            {
                var flat = await Task.Run(() => ProcessControl.AllProcesses(cache));
                grouped = flat
                    .GroupBy(x => x.Name)
                    .Select(g => (Key: g.Key, Name: g.Key, Model: (CatalogItem?)null, Procs: g.ToList()))
                    .OrderByDescending(t => t.Procs.Sum(p => p.MemBytes))
                    .ToList();
            }
            else
            {
                if (resolveTargets)
                {
                    var items = _catalog.Items.ToList();
                    var pr = _resolver;
                    var resolved = await Task.Run(() =>
                    {
                        var d = new Dictionary<string, (string, string?)>();
                        foreach (var it in items)
                        {
                            try { var rt = ProcessControl.ResolveTarget(it, pr); if (rt != null) d[it.Id] = rt.Value; }
                            catch { /* skip */ }
                        }
                        return d;
                    });
                    _targets.Clear();
                    foreach (var kv in resolved) _targets[kv.Key] = kv.Value;
                }

                var targetList = _targets.Select(kv => (kv.Key, kv.Value.Proc, kv.Value.Dir)).ToList();
                var flat = await Task.Run(() => ProcessControl.ScanAll(targetList, cache));
                grouped = flat
                    .GroupBy(x => x.Id)
                    .Select(g =>
                    {
                        var item = _catalog!.Items.FirstOrDefault(i => i.Id == g.Key);
                        return (Key: g.Key, Name: item?.Name ?? g.Key, Model: item, Procs: g.Select(x => x.Proc).ToList());
                    })
                    .ToList();
            }
            ApplySample(grouped);
        }
        finally { _scanning = false; }
    }

    private void ApplySample(List<(string Key, string Name, CatalogItem? Model, List<ProcItem> Procs)> sampled)
    {
        var now = DateTime.Now;
        var seen = new HashSet<string>();
        var newCpu = new Dictionary<int, (TimeSpan, DateTime)>();

        foreach (var (key, name, model, procs) in sampled)
        {
            seen.Add(key);

            if (!_groups.TryGetValue(key, out var g))
            {
                g = new AppProcGroupViewModel(key, name, model, _repoRoot) { IsExpanded = _allExpanded };
                g.EnsureIconFromExe(procs.FirstOrDefault(p => p.Path != null)?.Path);   // exe fallback when no bundled icon
                _groups[key] = g;
                Groups.Add(g);
            }

            // A group is "system-level" only when none of its processes run in the interactive session.
            g.IsSystemGroup = !procs.Any(p => p.SessionId == ProcessControl.CurrentSessionId);

            var livePids = procs.Select(p => p.Pid).ToHashSet();
            for (var i = g.Processes.Count - 1; i >= 0; i--)
                if (!livePids.Contains(g.Processes[i].Pid)) g.Processes.RemoveAt(i);

            foreach (var p in procs)
            {
                double cpu = 0;
                if (_prevCpu.TryGetValue(p.Pid, out var prev))
                {
                    var dt = (now - prev.T).TotalMilliseconds;
                    if (dt > 0)
                        cpu = Math.Clamp((p.CpuTime - prev.Cpu).TotalMilliseconds / (dt * Environment.ProcessorCount) * 100, 0, 100);
                }
                newCpu[p.Pid] = (p.CpuTime, now);

                var row = g.Processes.FirstOrDefault(r => r.Pid == p.Pid);
                if (row == null) g.Processes.Add(new ProcRowViewModel(p.Pid, p.Name, p.MemBytes, cpu, p.Path));
                else { row.Update(p.MemBytes, cpu); row.EnsurePath(p.Path); }
            }
            g.RaiseCount();
        }

        foreach (var id in _groups.Keys.ToList())
            if (!seen.Contains(id)) { Groups.Remove(_groups[id]); _groups.Remove(id); }

        ApplySections();

        _prevCpu.Clear();
        foreach (var kv in newCpu) _prevCpu[kv.Key] = kv.Value;

        var procCount = Groups.Sum(x => x.Processes.Count);
        Summary = _showAll
            ? Localizer.Format("proc.summary.all", Groups.Count(g => !g.IsSystemGroup), Groups.Count(g => g.IsSystemGroup), procCount)
            : (Groups.Count == 0 ? Localizer.T("proc.summary.allEmpty")
                                 : Localizer.Format("proc.summary.lib", Groups.Count, procCount));
        OnPropertyChanged(nameof(IsEmpty));
    }

    /// <summary>In 全部进程 mode, lay groups out Task-Manager style: all user-level groups first, then a
    /// 系统进程 divider, then all system-level groups. A stable partition (preserving each section's existing
    /// order) so live memory churn doesn't reshuffle the list. No sections in 软件库 mode.</summary>
    private void ApplySections()
    {
        if (!_showAll)
        {
            foreach (var g in Groups) g.SetSection(false, "");
            return;
        }

        // Stable partition: user groups (in current order) before system groups (in current order).
        var desired = Groups.Where(g => !g.IsSystemGroup).Concat(Groups.Where(g => g.IsSystemGroup)).ToList();
        for (var i = 0; i < desired.Count; i++)
        {
            var cur = Groups.IndexOf(desired[i]);
            if (cur != i) Groups.Move(cur, i);
        }

        bool userHeader = false, sysHeader = false;
        foreach (var g in Groups)
        {
            if (!g.IsSystemGroup && !userHeader) { g.SetSection(true, Localizer.T("proc.section.user")); userHeader = true; }
            else if (g.IsSystemGroup && !sysHeader) { g.SetSection(true, Localizer.T("proc.section.system")); sysHeader = true; }
            else g.SetSection(false, "");
        }
    }

    private void EndAll(AppProcGroupViewModel g)
    {
        if (g.Model is { } m) { OperationRequested?.Invoke(m, "stop"); return; }
        if (g.Processes.Count == 0) return;
        if (Dialogs.Show(Localizer.Format("proc.endAll.confirm", g.Name, g.Processes.Count), Localizer.T("proc.endAll.title"),
                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        var n = 0;
        foreach (var r in g.Processes.ToList()) if (ProcessControl.Kill(r.Pid)) n++;
        AuditLog.Action($"结束全部进程：{g.Name} — 结束 {n} 个");
        _ = RefreshAsync(resolveTargets: false);
    }

    /// <summary>The path of the group's primary (first path-bearing) process — backs the header right-click
    /// 打开文件位置 / 属性. Null when no process in the group exposed a readable module path.</summary>
    private static string? PrimaryPath(AppProcGroupViewModel g) => g.Processes.FirstOrDefault(r => r.HasPath)?.Path;

    private void EndOne(ProcRowViewModel r)
    {
        if (Dialogs.Show(Localizer.Format("proc.endOne.confirm", r.Name, r.Pid), Localizer.T("proc.endOne.title"),
                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        var ok = ProcessControl.Kill(r.Pid);
        AuditLog.Action($"结束进程 {r.Name} (PID {r.Pid})：{(ok ? "成功" : "失败")}");
        _ = RefreshAsync(resolveTargets: false);
    }

    private void OpenTaskManager()
    {
        try { Process.Start(new ProcessStartInfo("taskmgr.exe") { UseShellExecute = true }); }
        catch { /* ignore */ }
    }
}
