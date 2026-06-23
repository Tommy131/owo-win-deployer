using System.Windows;
using System.Windows.Media;
using WinDeploy.App.Services;

namespace WinDeploy.App.ViewModels;

public sealed class SettingsViewModel : ObservableObject
{
    private readonly AppSettings _s;

    public SettingsViewModel()
    {
        _s = SettingsStore.Load();
        _devRoot = _s.DevRoot ?? "%USERPROFILE%/dev";
        _toolsDir = _s.ToolsDir ?? "%LOCALAPPDATA%/tools";
        _downloadDir = _s.DownloadDir ?? "%USERPROFILE%/Downloads/WinDeploy";
        _repoUrl = _s.RepoUrl ?? "https://github.com/Tommy131/owo-win-deployer.git";
        _mirror = _s.Mirror ?? "";
        _redactKeywords = _s.RedactKeywords ?? "";
        _theme = _s.Theme ?? "system";
        _developerMode = _s.DeveloperMode;
        SettingsPath = SettingsStore.FilePath;
        SaveCommand = new RelayCommand(_ => Save());
        OpenFolderCommand = new RelayCommand(_ => OpenFolder());
        CheckUpdateCommand = new RelayCommand(_ => _ = CheckUpdateAsync());
        OpenLinkCommand = new RelayCommand(p => OpenUrl(p as string));
        RefreshIconsCommand = new RelayCommand(_ => RefreshIconsRequested?.Invoke());
    }

    // ── About / developer ───────────────────────────────────────────────
    public string AppTitle => WinDeploy.App.AppInfo.TitleWithVersion;
    public string AppCopyright => WinDeploy.App.AppInfo.Copyright;
    public string AuthorName => WinDeploy.App.AppInfo.Author;
    public ImageSource? AuthorAvatar => IconResolver.FromCatalogId("author");

    public RelayCommand CheckUpdateCommand { get; }
    public RelayCommand OpenLinkCommand { get; }
    public RelayCommand RefreshIconsCommand { get; }

    /// <summary>Raised when the user clicks 联网刷新软件图标; handled by MainViewModel (has the catalog).</summary>
    public event Action? RefreshIconsRequested;

    private string _iconNote = "";
    public string IconNote { get => _iconNote; set => Set(ref _iconNote, value); }

    private string _updateNote = "";
    public string UpdateNote { get => _updateNote; set => Set(ref _updateNote, value); }

    private async Task CheckUpdateAsync()
    {
        UpdateNote = "正在检查更新 …";
        var r = await SelfUpdate.CheckAsync(force: true);
        if (r.Error != null) { UpdateNote = r.Error; return; }
        if (r.Available)
        {
            UpdateNote = $"发现新版本 v{r.Latest}";
            AuditLog.Action($"检查更新：发现新版本 v{r.Latest}（当前 v{r.Current}）");
            if (MessageBox.Show($"发现新版本 v{r.Latest}（当前 v{r.Current}）。\n\n是否前往发布页下载？",
                    "检查更新", MessageBoxButton.YesNo, MessageBoxImage.Information) == MessageBoxResult.Yes)
                OpenUrl(r.HtmlUrl);
        }
        else UpdateNote = $"当前已是最新版本 v{r.Current}";
    }

    private static void OpenUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return;
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }); }
        catch { /* ignore */ }
    }

    private string _devRoot;
    public string DevRoot { get => _devRoot; set { if (Set(ref _devRoot, value)) Note = ""; } }

    private string _toolsDir;
    public string ToolsDir { get => _toolsDir; set { if (Set(ref _toolsDir, value)) Note = ""; } }

    private string _downloadDir;
    public string DownloadDir { get => _downloadDir; set { if (Set(ref _downloadDir, value)) Note = ""; } }

    private string _repoUrl;
    public string RepoUrl { get => _repoUrl; set { if (Set(ref _repoUrl, value)) Note = ""; } }

    private string _mirror;
    public string Mirror { get => _mirror; set { if (Set(ref _mirror, value)) Note = ""; } }

    private string _redactKeywords;
    public string RedactKeywords { get => _redactKeywords; set { if (Set(ref _redactKeywords, value)) Note = ""; } }

    // ── 开发人员模式（即时生效并持久化）─────────────────────────────────
    private bool _developerMode;
    public bool DeveloperMode
    {
        get => _developerMode;
        set
        {
            if (!Set(ref _developerMode, value)) return;
            _s.DeveloperMode = value;
            SettingsStore.Save(_s);
            AuditLog.Action($"开发人员模式：{(value ? "开启（显示完整软件列表）" : "关闭（仅基础分类）")}");
            DeveloperModeChanged?.Invoke(value);
        }
    }

    /// <summary>勾选/取消开发人员模式时触发，让软件安装中心立即重算分类可见性。</summary>
    public event Action<bool>? DeveloperModeChanged;

    private string _theme;
    public bool IsThemeSystem { get => _theme == "system"; set { if (value) SetTheme("system"); } }
    public bool IsThemeLight { get => _theme == "light"; set { if (value) SetTheme("light"); } }
    public bool IsThemeDark { get => _theme == "dark"; set { if (value) SetTheme("dark"); } }

    private void SetTheme(string t)
    {
        _theme = t;
        OnPropertyChanged(nameof(IsThemeSystem));
        OnPropertyChanged(nameof(IsThemeLight));
        OnPropertyChanged(nameof(IsThemeDark));
        ThemeManager.Apply(ThemeManager.Parse(t));
        _s.Theme = t;
        SettingsStore.Save(_s);
        AuditLog.Action($"切换主题：{t}");
    }

    public string SettingsPath { get; }

    private string _note = "";
    public string Note { get => _note; set => Set(ref _note, value); }

    public RelayCommand SaveCommand { get; }
    public RelayCommand OpenFolderCommand { get; }
    public event Action? Saved;

    private void OpenFolder()
    {
        try
        {
            System.IO.Directory.CreateDirectory(SettingsStore.Folder);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(SettingsStore.Folder) { UseShellExecute = true });
        }
        catch { /* ignore */ }
    }

    private void Save()
    {
        _s.DevRoot = DevRoot.Trim();
        _s.ToolsDir = ToolsDir.Trim();
        _s.DownloadDir = DownloadDir.Trim();
        _s.RepoUrl = RepoUrl.Trim();
        _s.Mirror = Mirror.Trim();
        _s.RedactKeywords = RedactKeywords.Trim();
        SettingsStore.Save(_s);
        AuditLog.Action($"更新设置 · DevRoot={_s.DevRoot} · ToolsDir={_s.ToolsDir} · 下载={_s.DownloadDir} · Repo={_s.RepoUrl} · " +
                        $"镜像={(string.IsNullOrEmpty(_s.Mirror) ? "(无)" : _s.Mirror)} · 脱敏关键词 {ParseKeywords(_s.RedactKeywords).Length} 项");
        Note = "已保存。脱敏关键词即时生效；路径变量在下次启动生效。";
        Saved?.Invoke();
    }

    public static string[] ParseKeywords(string? raw)
        => string.IsNullOrWhiteSpace(raw)
            ? Array.Empty<string>()
            : raw.Split(new[] { ',', ' ', '\n', '\r', '\t', ';', '，', '；' },
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
