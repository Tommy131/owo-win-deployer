using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Input;
using WinDeploy.App.Services;
using WinDeploy.Core;
using WinDeploy.Core.Engine;
using WinDeploy.Core.Models;

namespace WinDeploy.App.ViewModels;

/// <summary>The "软件安装中心" page: grouped, icon-forward, per-item selectable software cards.</summary>
public sealed class InstallCenterViewModel : ObservableObject
{
    private static readonly Dictionary<string, string> CategoryNames = new()
    {
        ["dev"] = "开发工具链", ["system"] = "系统依赖", ["ide"] = "IDE / 编辑器",
        ["ai"] = "AI 工具", ["office"] = "办公 / 通讯", ["media"] = "媒体",
        ["db-api"] = "数据库 / API", ["vm"] = "虚拟化", ["games"] = "游戏平台",
        ["browser"] = "浏览器", ["proxy"] = "网络代理", ["dict"] = "词典", ["hwmon"] = "硬件监控",
        ["tools"] = "实用工具",
    };

    /// <summary>普通用户（非开发人员模式）可见的分类。其余分类（dev/ide/ai/db-api/vm/tools）
    /// 仅在开发人员模式下显示，且永不被全选/方案勾选，以免被悄悄安装。</summary>
    private static readonly HashSet<string> BasicCategories =
        new(StringComparer.OrdinalIgnoreCase) { "office", "games", "system", "media" };

    private bool _developerMode = SettingsStore.Load().DeveloperMode;

    private bool CategoryVisible(string key) => _developerMode || BasicCategories.Contains(key);

    /// <summary>设置页切换开发人员模式时调用：重算分类可见性，并取消隐藏分类的勾选。</summary>
    public void SetDeveloperMode(bool on)
    {
        if (_developerMode == on) return;
        _developerMode = on;
        ApplyFilter();
        CommandManager.InvalidateRequerySuggested();
    }

    private Catalog? _catalog;
    private string _catalogDir = "";

    public ObservableCollection<CategoryGroupViewModel> Groups { get; } = new();
    public ObservableCollection<string> Profiles { get; } = new();
    public RelayCommand StartCommand { get; }
    public RelayCommand UpdateSelectedCommand { get; }
    public RelayCommand OpenDetailCommand { get; }
    public RelayCommand LaunchCommand { get; }
    public RelayCommand StopCommand { get; }
    public RelayCommand InstallCardCommand { get; }
    public RelayCommand UninstallCardCommand { get; }
    public RelayCommand RestartCardCommand { get; }
    public RelayCommand UpdateCardCommand { get; }
    public RelayCommand OpenDirCommand { get; }
    public RelayCommand OpenHomepageCommand { get; }
    public RelayCommand SelectAllCommand { get; }
    public RelayCommand InvertCommand { get; }
    public RelayCommand RestoreCommand { get; }
    public RelayCommand RefreshCommand { get; }
    public RelayCommand ToggleUpdatesCommand { get; }
    private Dictionary<string, bool>? _snapshot;

    /// <summary>Raised when the user clicks 刷新 (re-detect installed status + update availability).</summary>
    public event Action? RefreshRequested;

    /// <summary>Raised when the user clicks 开始安装.</summary>
    public event Action? StartRequested;

    /// <summary>Raised when the user clicks 更新选中.</summary>
    public event Action? UpdateRequested;

    /// <summary>Raised when the user clicks a software card.</summary>
    public event Action<AppItemViewModel>? DetailRequested;

    /// <summary>Raised when the user clicks a card's ▶ quick-launch button.</summary>
    public event Action<AppItemViewModel>? LaunchRequested;

    /// <summary>Raised when the user clicks a card's ■ stop button.</summary>
    public event Action<AppItemViewModel>? StopRequested;

    /// <summary>Right-click context-menu actions on a card.</summary>
    public event Action<AppItemViewModel>? InstallCardRequested;
    public event Action<AppItemViewModel>? UninstallCardRequested;
    public event Action<AppItemViewModel>? RestartCardRequested;
    public event Action<AppItemViewModel>? UpdateCardRequested;
    public event Action<AppItemViewModel>? OpenDirRequested;
    public event Action<AppItemViewModel>? OpenHomepageRequested;

    public InstallCenterViewModel()
    {
        StartCommand = new RelayCommand(_ => StartRequested?.Invoke(), _ => SelectedCount > 0);
        UpdateSelectedCommand = new RelayCommand(_ => UpdateRequested?.Invoke(), _ => UpdatableSelectedCount > 0);
        OpenDetailCommand = new RelayCommand(p => { if (p is AppItemViewModel vm) DetailRequested?.Invoke(vm); });
        LaunchCommand = new RelayCommand(p => { if (p is AppItemViewModel vm) LaunchRequested?.Invoke(vm); });
        StopCommand = new RelayCommand(p => { if (p is AppItemViewModel vm) StopRequested?.Invoke(vm); });
        InstallCardCommand = new RelayCommand(p => { if (p is AppItemViewModel vm) InstallCardRequested?.Invoke(vm); });
        UninstallCardCommand = new RelayCommand(p => { if (p is AppItemViewModel vm) UninstallCardRequested?.Invoke(vm); });
        RestartCardCommand = new RelayCommand(p => { if (p is AppItemViewModel vm) RestartCardRequested?.Invoke(vm); });
        UpdateCardCommand = new RelayCommand(p => { if (p is AppItemViewModel vm) UpdateCardRequested?.Invoke(vm); });
        OpenDirCommand = new RelayCommand(p => { if (p is AppItemViewModel vm) OpenDirRequested?.Invoke(vm); });
        OpenHomepageCommand = new RelayCommand(p => { if (p is AppItemViewModel vm) OpenHomepageRequested?.Invoke(vm); });
        SelectAllCommand = new RelayCommand(_ => SetAll(true));
        InvertCommand = new RelayCommand(_ => Invert());
        RestoreCommand = new RelayCommand(_ => Restore(), _ => _snapshot != null);
        RefreshCommand = new RelayCommand(_ => RefreshRequested?.Invoke(), _ => !IsLoading);
        ToggleUpdatesCommand = new RelayCommand(_ => ToggleUpdates());
    }

    private bool _hideUpdates;
    public int UpdatableCount => Groups.Sum(g => g.Items.Count(i => i.HasUpdate));
    public bool HasUpdates => UpdatableCount > 0;
    public string UpdateToggleLabel => _hideUpdates ? $"显示可更新 ({UpdatableCount})" : $"忽略可更新 ({UpdatableCount})";

    private void ToggleUpdates()
    {
        _hideUpdates = !_hideUpdates;
        foreach (var g in Groups)
            foreach (var i in g.Items) i.BadgeHidden = _hideUpdates;
        OnPropertyChanged(nameof(UpdateToggleLabel));
    }

    /// <summary>Recompute the updatable count / toggle after the startup update check set HasUpdate.</summary>
    public void RefreshUpdateState()
    {
        foreach (var g in Groups)
            foreach (var i in g.Items) i.BadgeHidden = _hideUpdates;
        OnPropertyChanged(nameof(UpdatableCount));
        OnPropertyChanged(nameof(HasUpdates));
        OnPropertyChanged(nameof(UpdateToggleLabel));
    }

    /// <summary>Selected items that are installed and support updating — gates 更新选中.</summary>
    public int UpdatableSelectedCount => Groups.Sum(g => g.Items.Count(
        i => i.IsSelected && i.IsInstalled && WinDeploy.Core.Engine.Updater.CanUpdate(i.Model)));

    private bool _isLoading = true;
    public bool IsLoading
    {
        get => _isLoading;
        set { if (Set(ref _isLoading, value)) OnPropertyChanged(nameof(IsReady)); }
    }
    public bool IsReady => !_isLoading;

    /// <summary>Remembered scroll position, restored when returning from the detail page.</summary>
    public double ScrollOffset { get; set; }

    public void Initialize(Catalog catalog, string catalogDir)
    {
        _catalog = catalog;
        _catalogDir = catalogDir;
        var repoRoot = Path.GetDirectoryName(catalogDir)!;

        Groups.Clear();
        foreach (var grp in catalog.Items.GroupBy(i => i.Category))
        {
            var g = new CategoryGroupViewModel(grp.Key, CategoryNames.GetValueOrDefault(grp.Key, grp.Key));
            foreach (var item in grp)
            {
                var vm = new AppItemViewModel(item);
                vm.LoadIcon(repoRoot);
                vm.SelectionChanged += () => OnSelectionChanged(g);
                g.Items.Add(vm);
            }
            Groups.Add(g);
        }

        Profiles.Clear();
        var pdir = Path.Combine(catalogDir, "profiles");
        if (Directory.Exists(pdir))
            foreach (var f in Directory.GetFiles(pdir, "*.json"))
                Profiles.Add(Path.GetFileNameWithoutExtension(f));

        ApplyFilter();   // 按开发人员模式隐藏开发类分组（RefreshCounts 在内部已调用）
    }

    public int SelectedCount => Groups.Sum(g => g.Items.Count(i => i.IsSelected));
    public string StartLabel => $"开始安装 ({SelectedCount})";
    public string UpdateLabel => $"更新选中 ({UpdatableSelectedCount})";

    private string _pathNote = "";
    public string PathNote { get => _pathNote; set => Set(ref _pathNote, value); }

    /// <summary>Set an install root for every selected item: base dir + the app's own folder name, so each
    /// app installs into a distinct directory (D:\Tools\System\GPU-Z, …\CPU-Z) instead of sharing the root.</summary>
    public void SetPathForSelected(string baseDir)
    {
        var n = 0;
        foreach (var g in Groups)
            foreach (var i in g.Items)
                if (i.IsSelected)
                {
                    i.Model.InstallPathOverride = ComposeInstallPath(baseDir, i.Model.Name);
                    n++;
                }
        PathNote = n > 0 ? $"已为 {n} 个选中项设置安装路径（以 {baseDir} 为根目录，按软件名分目录）" : "未选中任何软件";
        if (n > 0) AuditLog.Action($"设置安装路径：{baseDir}（{n} 项，按软件名分目录）");
    }

    /// <summary>base + app folder. If the chosen path's leaf already (fuzzily) matches the app name, it's
    /// used as-is to avoid a duplicated segment (…\GPU-Z\GPU-Z).</summary>
    public static string ComposeInstallPath(string baseDir, string appName)
    {
        var b = baseDir.TrimEnd('\\', '/');
        var leaf = Norm(System.IO.Path.GetFileName(b));
        var nn = Norm(appName);
        var already = nn.Length >= 3 ? leaf.Contains(nn) : leaf == nn;
        return already ? b : System.IO.Path.Combine(b, SanitizeFolder(appName));
    }

    private static string Norm(string s)
    {
        var sb = new System.Text.StringBuilder(s.Length);
        foreach (var c in s) if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
        return sb.ToString();
    }

    private static string SanitizeFolder(string name)
    {
        var invalid = System.IO.Path.GetInvalidFileNameChars();
        var cleaned = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim();
        return cleaned.Length == 0 ? "app" : cleaned;
    }
    public string Subtitle => $"勾选要部署到本机的软件 · 已选 {SelectedCount} 项";

    private string? _loadError;
    public string? LoadError
    {
        get => _loadError;
        set { if (Set(ref _loadError, value)) OnPropertyChanged(nameof(HasError)); }
    }
    public bool HasError => !string.IsNullOrEmpty(_loadError);

    private string _searchText = "";
    public string SearchText
    {
        get => _searchText;
        set { if (Set(ref _searchText, value)) ApplyFilter(); }
    }

    private string? _selectedProfile;
    public string? SelectedProfile
    {
        get => _selectedProfile;
        set { if (Set(ref _selectedProfile, value) && value != null) ApplyProfile(value); }
    }

    private void OnSelectionChanged(CategoryGroupViewModel group)
    {
        group.RaiseCount();
        RefreshCounts();
    }

    private void RefreshCounts()
    {
        OnPropertyChanged(nameof(SelectedCount));
        OnPropertyChanged(nameof(StartLabel));
        OnPropertyChanged(nameof(UpdatableSelectedCount));
        OnPropertyChanged(nameof(UpdateLabel));
        OnPropertyChanged(nameof(Subtitle));
    }

    private void Snapshot()
        => _snapshot = Groups.SelectMany(g => g.Items).ToDictionary(i => i.Id, i => i.IsSelected);

    private void SetAll(bool value)
    {
        Snapshot();
        foreach (var g in Groups)
        {
            var catVisible = CategoryVisible(g.Key);
            foreach (var i in g.Items) i.IsSelected = value && catVisible;
            g.RaiseCount();
        }
        RefreshCounts();
        CommandManager.InvalidateRequerySuggested();
    }

    private void Invert()
    {
        Snapshot();
        foreach (var g in Groups)
        {
            var catVisible = CategoryVisible(g.Key);
            foreach (var i in g.Items) i.IsSelected = catVisible && !i.IsSelected;
            g.RaiseCount();
        }
        RefreshCounts();
        CommandManager.InvalidateRequerySuggested();
    }

    private void Restore()
    {
        if (_snapshot == null) return;
        foreach (var g in Groups)
        {
            foreach (var i in g.Items)
                if (_snapshot.TryGetValue(i.Id, out var v)) i.IsSelected = v;
            g.RaiseCount();
        }
        RefreshCounts();
    }

    private void ApplyFilter()
    {
        var q = _searchText.Trim();
        foreach (var g in Groups)
        {
            var catVisible = CategoryVisible(g.Key);
            var any = false;
            foreach (var i in g.Items)
            {
                if (!catVisible) i.IsSelected = false;   // 隐藏分类永不参与安装
                i.IsVisible = q.Length == 0
                    || i.Name.Contains(q, StringComparison.OrdinalIgnoreCase)
                    || i.Id.Contains(q, StringComparison.OrdinalIgnoreCase)
                    || i.Summary.Contains(q, StringComparison.OrdinalIgnoreCase);
                any |= i.IsVisible;
            }
            g.IsVisible = catVisible && any;
        }
        RefreshCounts();
    }

    private void ApplyProfile(string name)
    {
        if (_catalog == null) return;
        try
        {
            Snapshot();
            CommandManager.InvalidateRequerySuggested();
            var profile = CatalogLoader.LoadProfile(_catalogDir, name);
            var ids = Selection.Resolve(_catalog, profile, null, false, null)
                .Select(i => i.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var g in Groups)
            {
                var catVisible = CategoryVisible(g.Key);
                foreach (var i in g.Items) i.IsSelected = catVisible && ids.Contains(i.Id);
                g.RaiseCount();
            }
            RefreshCounts();
        }
        catch { /* ignore bad profile */ }
    }
}
