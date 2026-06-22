using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using WinDeploy.App.Services;
using WinDeploy.Core;
using WinDeploy.Core.Engine;
using WinDeploy.Core.Models;

namespace WinDeploy.App.ViewModels;

/// <summary>Software detail page: catalog / ARP / winget metadata, a selectable install version &amp;
/// path, and the install location. Operations (install / update / uninstall / launch / stop / restart)
/// are raised as events so the host routes them through the 运行进度 page.</summary>
public sealed class DetailViewModel : ObservableObject
{
    private readonly PathResolver _resolver;

    public AppItemViewModel Item { get; }
    public RelayCommand BackCommand { get; }
    public RelayCommand OpenHomepageCommand { get; }
    public RelayCommand OpenLocationCommand { get; }
    public RelayCommand InstallCommand { get; }
    public RelayCommand UpdateCommand { get; }
    public RelayCommand DowngradeCommand { get; }
    public RelayCommand UninstallCommand { get; }
    public RelayCommand LaunchCommand { get; }
    public RelayCommand StopCommand { get; }
    public RelayCommand RestartCommand { get; }

    public event Action<CatalogItem>? InstallRequested;
    public event Action<CatalogItem>? UpdateRequested;
    public event Action<CatalogItem>? DowngradeRequested;
    public event Action<CatalogItem, bool>? UninstallRequested;
    public event Action<CatalogItem>? LaunchRequested;
    public event Action<CatalogItem>? StopRequested;
    public event Action<CatalogItem>? RestartRequested;

    public DetailViewModel(AppItemViewModel item, PathResolver resolver, Action back)
    {
        Item = item;
        _resolver = resolver;
        BackCommand = new RelayCommand(_ => back());
        OpenHomepageCommand = new RelayCommand(_ => OpenHomepage());
        OpenLocationCommand = new RelayCommand(_ => OpenLocation(), _ => HasInstallLocation);
        InstallCommand = new RelayCommand(_ => InstallRequested?.Invoke(Item.Model), _ => ShowInstall);
        UpdateCommand = new RelayCommand(_ => UpdateRequested?.Invoke(Item.Model), _ => ShowUpdate);
        DowngradeCommand = new RelayCommand(_ => DowngradeRequested?.Invoke(Item.Model), _ => ShowDowngrade);
        UninstallCommand = new RelayCommand(_ => RequestUninstall(), _ => CanUninstall);
        LaunchCommand = new RelayCommand(_ => LaunchRequested?.Invoke(Item.Model), _ => CanLaunch);
        StopCommand = new RelayCommand(_ => StopRequested?.Invoke(Item.Model), _ => CanStop);
        RestartCommand = new RelayCommand(_ => RestartRequested?.Invoke(Item.Model), _ => CanRestart);

        var ins = item.Model.Install;
        Source = ins.Method switch
        {
            "winget" => "winget",
            "winget-bundle" => "winget（合集）",
            "portable" => "便携包（zip）",
            "git" => "Git 仓库",
            "exe" => "官方安装包",
            "conda" => "conda 环境",
            "vscode-ext" => "VS Code 扩展",
            "script" => "脚本",
            _ => ins.Method,
        };
        PackageId = ins.Id ?? (ins.Ids is { Count: > 0 } ? string.Join(", ", ins.Ids) : "—");

        CanChooseVersion = ins.Method == "winget" && !string.IsNullOrEmpty(ins.Id);
        _selectedVersion = string.IsNullOrEmpty(item.Model.Version) ? Latest : item.Model.Version!;
        Versions.Add(Latest);
        if (_selectedVersion != Latest) Versions.Add(_selectedVersion);

        CanSetPath = ins.Method is "winget" or "portable" or "git";
        _defaultPath = ins.Method switch
        {
            "portable" => ins.ExtractTo != null ? resolver.Resolve(ins.ExtractTo) : "",
            "git" => ins.Dest != null ? resolver.Resolve(ins.Dest) : "",
            _ => "",
        };
        _installPath = item.Model.InstallPathOverride ?? _defaultPath;

        // Default install location from the spec (works without ARP for portable / git).
        _installLocation = DefaultLocation(ins) ?? "—";

        var cached = DetailService.GetCached(item.Model.Id);
        if (cached != null) Apply(cached);
        _ = LoadAsync();
        if (CanChooseVersion) _ = LoadVersionsAsync();
        if (item.IsInstalled) _ = CheckRunningAsync();
    }

    private const string Latest = "最新";
    private readonly string _defaultPath;

    public ImageSource? IconImage => Item.IconImage;
    public bool HasIcon => Item.HasIcon;
    public bool ShowLetter => Item.ShowLetter;
    public string Badge => Item.Badge;
    public Brush ChipBackground => Item.ChipBackground;
    public Brush ChipForeground => Item.ChipForeground;
    public string Name => Item.Name;
    public string Summary => Item.Summary;
    public bool IsInstalled => Item.IsInstalled;
    public string StatusText => Item.IsInstalled ? "已安装" : "未安装";

    // Version-selector ↔ button linkage:
    //   未安装 → 安装；已安装且选「最新」或 ≥ 当前版本 → 检查/更新；已安装且选低于当前版本 → 降级。
    public bool ShowInstall => !Item.IsInstalled;
    public bool ShowDowngrade => Item.IsInstalled && CanChooseVersion && _selectedVersion != Latest
                                 && _version != "—" && CompareVersions(_selectedVersion, _version) < 0;
    public bool ShowUpdate => Item.IsInstalled && Updater.CanUpdate(Item.Model) && !ShowDowngrade;
    public bool CanUninstall => Item.IsInstalled && Item.Model.Install.Method is "winget" or "winget-bundle" or "portable" or "git";
    public bool CanLaunch => Item.IsInstalled;
    public bool CanStop => Item.IsInstalled && _isRunningProc;   // hide 结束进程 when not running
    public bool CanRestart => Item.IsInstalled;

    private bool _isRunningProc;
    public bool IsRunningProc
    {
        get => _isRunningProc;
        private set { if (Set(ref _isRunningProc, value)) { OnPropertyChanged(nameof(CanStop)); CommandManager.InvalidateRequerySuggested(); } }
    }

    private async Task CheckRunningAsync()
    {
        try { var n = await Task.Run(() => ProcessControl.Find(Item.Model, _resolver).Count); IsRunningProc = n > 0; }
        catch { IsRunningProc = false; }
    }

    private DispatcherTimer? _runWatch;

    /// <summary>Begin polling running-state every 2 s so 「结束进程」 appears/disappears live.</summary>
    public void StartRunningWatch()
    {
        if (!Item.IsInstalled) return;
        _runWatch ??= new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _runWatch.Tick -= OnRunWatch;
        _runWatch.Tick += OnRunWatch;
        _ = CheckRunningAsync();
        _runWatch.Start();
    }

    public void StopRunningWatch() => _runWatch?.Stop();

    private void OnRunWatch(object? sender, EventArgs e) => _ = CheckRunningAsync();

    public string Source { get; }
    public string PackageId { get; }

    public bool CanChooseVersion { get; }
    public bool CannotChooseVersion => !CanChooseVersion;
    public ObservableCollection<string> Versions { get; } = new();

    private string _selectedVersion;
    public string SelectedVersion
    {
        get => _selectedVersion;
        set
        {
            if (!Set(ref _selectedVersion, value)) return;
            Item.Model.Version = value == Latest ? null : value;
            RaiseButtons();
        }
    }

    private void RaiseButtons()
    {
        OnPropertyChanged(nameof(ShowInstall));
        OnPropertyChanged(nameof(ShowUpdate));
        OnPropertyChanged(nameof(ShowDowngrade));
        CommandManager.InvalidateRequerySuggested();
    }

    private static int CompareVersions(string a, string b)
    {
        var pa = a.Split('.');
        var pb = b.Split('.');
        for (var i = 0; i < Math.Max(pa.Length, pb.Length); i++)
        {
            var va = i < pa.Length ? NumPart(pa[i]) : 0;
            var vb = i < pb.Length ? NumPart(pb[i]) : 0;
            if (va != vb) return va.CompareTo(vb);
        }
        return 0;

        static int NumPart(string s)
        {
            var digits = new string(s.TakeWhile(char.IsDigit).ToArray());
            return int.TryParse(digits, out var n) ? n : 0;
        }
    }

    public bool CanSetPath { get; }
    public string PathHint => Item.Model.Install.Method == "winget"
        ? "留空 = 默认位置；填写则用 winget --location（部分软件支持）"
        : "便携包 / git 的解压目录，可改";

    private string _installPath;
    public string InstallPath
    {
        get => _installPath;
        set
        {
            if (!Set(ref _installPath, value)) return;
            Item.Model.InstallPathOverride =
                string.IsNullOrWhiteSpace(value) || value == _defaultPath ? null : value.Trim();
        }
    }

    private string _installLocation;
    public string InstallLocation
    {
        get => _installLocation;
        private set { if (Set(ref _installLocation, value)) { OnPropertyChanged(nameof(HasInstallLocation)); CommandManager.InvalidateRequerySuggested(); } }
    }
    public bool HasInstallLocation => !string.IsNullOrWhiteSpace(_installLocation) && _installLocation != "—";

    private string _version = "—";
    public string Version
    {
        get => _version;
        private set { if (Set(ref _version, value)) { OnPropertyChanged(nameof(InstalledNote)); RaiseButtons(); } }
    }
    public string InstalledNote => IsInstalled && _version != "—" ? $"当前已装 {_version}" : "";

    private string _size = "—";
    public string Size { get => _size; private set => Set(ref _size, value); }

    private string _installDate = "—";
    public string InstallDate { get => _installDate; private set => Set(ref _installDate, value); }

    private string _publisher = "—";
    public string Publisher { get => _publisher; private set => Set(ref _publisher, value); }

    private string _homepage = "—";
    public string Homepage
    {
        get => _homepage;
        private set { if (Set(ref _homepage, value)) OnPropertyChanged(nameof(HasHomepage)); }
    }
    public bool HasHomepage => _homepage.StartsWith("http", StringComparison.OrdinalIgnoreCase);

    private async Task LoadAsync() => Apply(await DetailService.FetchAsync(Item.Model));

    private async Task LoadVersionsAsync()
    {
        var versions = await DetailService.GetVersionsAsync(Item.Model.Install.Id!);
        foreach (var v in versions)
            if (!Versions.Contains(v)) Versions.Add(v);
    }

    private void Apply(DetailInfo i)
    {
        Version = i.Version;
        Size = i.Size;
        InstallDate = i.InstallDate;
        Publisher = i.Publisher;
        Homepage = i.Homepage;
        if (!string.IsNullOrWhiteSpace(i.InstallLoc)) InstallLocation = i.InstallLoc!;
    }

    private string? DefaultLocation(InstallSpec ins) => ins.Method switch
    {
        "portable" => ins.ExtractTo != null ? _resolver.Resolve(Item.Model.InstallPathOverride ?? ins.ExtractTo) : null,
        "git" => ins.Dest != null ? _resolver.Resolve(Item.Model.InstallPathOverride ?? ins.Dest) : null,
        _ => null,
    };

    private void RequestUninstall()
    {
        var choice = MessageBox.Show(
            $"卸载 {Name}？\n\n【是】彻底删除（含用户数据）\n【否】仅卸载，保留数据\n【取消】不操作",
            "卸载", MessageBoxButton.YesNoCancel, MessageBoxImage.Warning);
        if (choice == MessageBoxResult.Cancel) return;
        UninstallRequested?.Invoke(Item.Model, choice == MessageBoxResult.Yes);
    }

    private void OpenLocation()
    {
        if (!HasInstallLocation) return;
        try { Process.Start(new ProcessStartInfo(_installLocation) { UseShellExecute = true }); }
        catch { /* ignore */ }
    }

    private void OpenHomepage()
    {
        if (!HasHomepage) return;
        try { Process.Start(new ProcessStartInfo(_homepage) { UseShellExecute = true }); }
        catch { /* ignore */ }
    }
}
