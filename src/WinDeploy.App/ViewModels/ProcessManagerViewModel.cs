using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using WinDeploy.App.Services;
using WinDeploy.Core;
using WinDeploy.Core.Models;

namespace WinDeploy.App.ViewModels;

/// <summary>One process row (live-updated memory + CPU%).</summary>
public sealed class ProcRowViewModel : ObservableObject
{
    public int Pid { get; }
    public string Name { get; }

    public ProcRowViewModel(int pid, string name, long memBytes, double cpu)
    {
        Pid = pid;
        Name = name;
        Update(memBytes, cpu);
    }

    private string _mem = "—";
    public string MemText { get => _mem; private set => Set(ref _mem, value); }

    private string _cpu = "0.0%";
    public string CpuText { get => _cpu; private set => Set(ref _cpu, value); }

    public void Update(long memBytes, double cpu)
    {
        MemText = memBytes > 0 ? $"{memBytes / 1024.0 / 1024:0.0} MB" : "—";
        CpuText = $"{cpu:0.0}%";
    }
}

/// <summary>A catalog item, its icon, and its running processes (expand/collapse).</summary>
public sealed class AppProcGroupViewModel : ObservableObject
{
    public CatalogItem Model { get; }
    public string Name => Model.Name;
    public ObservableCollection<ProcRowViewModel> Processes { get; } = new();
    public RelayCommand ToggleCommand { get; }

    public AppProcGroupViewModel(CatalogItem model, string repoRoot)
    {
        Model = model;
        ToggleCommand = new RelayCommand(_ => IsExpanded = !IsExpanded);
        LoadIcon(repoRoot);
    }

    private bool _isExpanded = true;
    public bool IsExpanded
    {
        get => _isExpanded;
        set { if (Set(ref _isExpanded, value)) OnPropertyChanged(nameof(ExpandGlyph)); }
    }
    public string ExpandGlyph => _isExpanded ? "▼" : "▶";

    public string CountText => $"{Processes.Count} 个进程";
    public void RaiseCount() => OnPropertyChanged(nameof(CountText));

    public ImageSource? IconImage { get; private set; }
    public bool HasIcon => IconImage != null;
    public bool ShowLetter => IconImage == null;
    public string Badge => Model.Name.Length > 0 ? Model.Name[..1].ToUpperInvariant() : "?";

    private void LoadIcon(string repoRoot)
    {
        try
        {
            var path = Path.Combine(repoRoot, "assets", "icons", Model.Id + ".png");
            if (!File.Exists(path)) return;
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            bmp.UriSource = new Uri(path);
            bmp.EndInit();
            bmp.Freeze();
            IconImage = bmp;
        }
        catch { /* letter fallback */ }
    }
}

/// <summary>The "进程管理" page: a task-manager scoped to the catalog. Per-software icon + expandable
/// process tree, live (2 s) memory / CPU%, end one / end all / restart, and a shortcut to Windows
/// Task Manager. End-all / restart route through the run-progress page; single ends are immediate.</summary>
public sealed class ProcessManagerViewModel : ObservableObject
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

    /// <summary>Group-level 结束全部 / 重启 — (item, "stop" | "restart").</summary>
    public event Action<CatalogItem, string>? OperationRequested;

    public ProcessManagerViewModel()
    {
        RefreshCommand = new RelayCommand(_ => _ = RefreshAsync(resolveTargets: true));
        EndProcessCommand = new RelayCommand(p => { if (p is ProcRowViewModel r) EndOne(r); });
        EndAllCommand = new RelayCommand(p => { if (p is AppProcGroupViewModel g) OperationRequested?.Invoke(g.Model, "stop"); });
        RestartCommand = new RelayCommand(p => { if (p is AppProcGroupViewModel g) OperationRequested?.Invoke(g.Model, "restart"); });
        OpenTaskManagerCommand = new RelayCommand(_ => OpenTaskManager());
    }

    public void Initialize(Catalog catalog, PathResolver resolver, string repoRoot)
    {
        _catalog = catalog;
        _resolver = resolver;
        _repoRoot = repoRoot;
    }

    private string _summary = "正在扫描进程 …";
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
            var cache = _pathCache;
            var flat = await Task.Run(() => ProcessControl.ScanAll(targetList, cache));

            var grouped = flat
                .GroupBy(x => x.Id)
                .Select(g => (g.Key, g.Select(x => x.Proc).ToList()))
                .ToList();
            ApplySample(grouped);
        }
        finally { _scanning = false; }
    }

    private void ApplySample(List<(string Id, List<ProcItem> Procs)> sampled)
    {
        if (_catalog == null) return;
        var now = DateTime.Now;
        var seen = new HashSet<string>();
        var newCpu = new Dictionary<int, (TimeSpan, DateTime)>();

        foreach (var (id, procs) in sampled)
        {
            seen.Add(id);
            var item = _catalog.Items.FirstOrDefault(i => i.Id == id);
            if (item == null) continue;

            if (!_groups.TryGetValue(id, out var g))
            {
                g = new AppProcGroupViewModel(item, _repoRoot);
                _groups[id] = g;
                Groups.Add(g);
            }

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
                if (row == null) g.Processes.Add(new ProcRowViewModel(p.Pid, p.Name, p.MemBytes, cpu));
                else row.Update(p.MemBytes, cpu);
            }
            g.RaiseCount();
        }

        foreach (var id in _groups.Keys.ToList())
            if (!seen.Contains(id)) { Groups.Remove(_groups[id]); _groups.Remove(id); }

        _prevCpu.Clear();
        foreach (var kv in newCpu) _prevCpu[kv.Key] = kv.Value;

        Summary = Groups.Count == 0
            ? "未发现软件列表中的软件正在运行"
            : $"{Groups.Count} 个软件运行中 · 共 {Groups.Sum(x => x.Processes.Count)} 个进程 · 每 2 秒刷新";
        OnPropertyChanged(nameof(IsEmpty));
    }

    private void EndOne(ProcRowViewModel r)
    {
        if (MessageBox.Show($"确定结束进程 {r.Name}（PID {r.Pid}）？", "结束进程",
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
