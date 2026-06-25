using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using WinDeploy.App.Services;
using WinDeploy.Core.I18n;
using WinDeploy.Core.Net;

namespace WinDeploy.App.ViewModels.Shell;

public sealed class SettingsViewModel : ObservableObject
{
    private readonly AppSettings _s;

    public SettingsViewModel()
    {
        _s = SettingsStore.Load();
        _lang = _s.Language ?? Localizer.Current;
        _devRoot = _s.DevRoot ?? "%USERPROFILE%/dev";
        _toolsDir = _s.ToolsDir ?? "%LOCALAPPDATA%/tools";
        _downloadDir = _s.DownloadDir ?? "%USERPROFILE%/Downloads/WinDeploy";
        _repoUrl = _s.RepoUrl ?? "https://github.com/Tommy131/owo-win-deployer.git";
        _mirror = _s.Mirror ?? "";
        _redactKeywords = _s.RedactKeywords ?? "";
        _theme = _s.Theme ?? "system";
        _closeAction = _s.CloseAction ?? "ask";
        _developerMode = _s.DeveloperMode;
        _runAtStartup = Services.Sys.AutoStart.IsEnabled();
        _alwaysShowTray = _s.AlwaysShowTray;
        _tempMonitorEnabled = _s.TempMonitorEnabled;
        _tempTts = _s.TempTtsEnabled;
        _tempCpu = _s.TempCpuEnabled; _tempGpu = _s.TempGpuEnabled; _tempDisk = _s.TempDiskEnabled;
        _cpuTempThreshold = _s.CpuTempThreshold; _gpuTempThreshold = _s.GpuTempThreshold; _diskTempThreshold = _s.DiskTempThreshold;
        _reminderSeconds = _s.TempReminderSeconds;
        _proxyEnabled = _s.ProxyEnabled;
        _proxyUrl = _s.ProxyUrl ?? "";
        SettingsPath = SettingsStore.FilePath;
        SaveCommand = new RelayCommand(_ => Save());
        OpenFolderCommand = new RelayCommand(_ => OpenFolder());
        CheckUpdateCommand = new RelayCommand(_ => _ = CheckUpdateAsync());
        OpenLinkCommand = new RelayCommand(p => OpenUrl(p as string));
        RefreshIconsCommand = new RelayCommand(_ => RefreshIconsRequested?.Invoke());
        TestTtsCommand = new RelayCommand(_ => Tts.Speak(Localizer.T("tempmon.tts.test")));
        SaveProxyCommand = new RelayCommand(_ => _ = SaveProxyAsync(), _ => !_proxyTesting);
        ResetCommand = new RelayCommand(_ => ResetAll());
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
        UpdateNote = Localizer.T("settings.update.checking");
        var r = await SelfUpdate.CheckAsync(force: true);
        if (r.Error != null) { UpdateNote = r.Error; return; }
        if (r.Available)
        {
            UpdateNote = Localizer.Format("settings.update.found", r.Latest);
            AuditLog.Action($"检查更新：发现新版本 v{r.Latest}（当前 v{r.Current}）");
            if (Dialogs.Show(Localizer.Format("settings.update.foundBody", r.Latest, r.Current),
                    Localizer.T("settings.update.dialogTitle"), MessageBoxButton.YesNo, MessageBoxImage.Information) == MessageBoxResult.Yes)
                OpenUrl(r.HtmlUrl);
        }
        else UpdateNote = Localizer.Format("settings.update.upToDate", r.Current);
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

    // ── 下载代理（保存前必须通过连通性测试）─────────────────────────────────
    public RelayCommand SaveProxyCommand { get; }

    private bool _proxyEnabled;
    public bool ProxyEnabled { get => _proxyEnabled; set { if (Set(ref _proxyEnabled, value)) ProxyNote = ""; } }

    private string _proxyUrl;
    public string ProxyUrl { get => _proxyUrl; set { if (Set(ref _proxyUrl, value)) ProxyNote = ""; } }

    private bool _proxyTesting;
    public bool ProxyTesting { get => _proxyTesting; set { if (Set(ref _proxyTesting, value)) CommandManager.InvalidateRequerySuggested(); } }

    private string _proxyNote = "";
    public string ProxyNote { get => _proxyNote; set => Set(ref _proxyNote, value); }

    /// <summary>Persist + apply the download proxy. Disabling saves immediately; enabling first validates the
    /// format (strict regex) and verifies live connectivity through the proxy — saving only if reachable.</summary>
    private async Task SaveProxyAsync()
    {
        if (!_proxyEnabled)
        {
            _s.ProxyEnabled = false;
            _s.ProxyUrl = (_proxyUrl ?? "").Trim();
            SettingsStore.Save(_s);
            HttpProxy.Apply(false, _s.ProxyUrl);
            ProxyNote = Localizer.T("settings.proxy.disabled");
            AuditLog.Action("下载代理：关闭");
            return;
        }

        var url = (_proxyUrl ?? "").Trim();
        if (!HttpProxy.IsValid(url)) { ProxyNote = Localizer.T("settings.proxy.badFormat"); return; }

        ProxyTesting = true;
        ProxyNote = Localizer.T("settings.proxy.testing");
        var (ok, detail) = await HttpProxy.TestAsync(url);
        ProxyTesting = false;
        if (!ok)
        {
            ProxyNote = Localizer.Format("settings.proxy.unreachable", detail);   // not saved
            AuditLog.Action($"下载代理：连通性测试失败（{detail}），未保存");
            return;
        }

        _s.ProxyEnabled = true;
        _s.ProxyUrl = url;
        SettingsStore.Save(_s);
        HttpProxy.Apply(true, url);
        ProxyNote = Localizer.T("settings.proxy.ok");
        AuditLog.Action($"下载代理：已启用 {url}");
    }

    // ── 开发人员模式（即时生效并持久化）─────────────────────────────────
    private bool _developerMode;
    public bool DeveloperMode
    {
        get => _developerMode;
        set
        {
            if (value && !_developerMode)
            {
                // Turning on — require explicit confirmation; revert UI if user cancels.
                if (ConfirmEnableDeveloperMode?.Invoke() == false)
                {
                    OnPropertyChanged(nameof(DeveloperMode));   // revert checkbox
                    return;
                }
            }
            if (!Set(ref _developerMode, value)) return;
            _s.DeveloperMode = value;
            SettingsStore.Save(_s);
            AuditLog.Action($"开发人员模式：{(value ? "开启（显示完整软件列表）" : "关闭（仅基础分类）")}");
            DeveloperModeChanged?.Invoke(value);
        }
    }

    /// <summary>勾选/取消开发人员模式时触发，让软件安装中心立即重算分类可见性。</summary>
    public event Action<bool>? DeveloperModeChanged;

    /// <summary>开启开发人员模式前触发，供外部显示二次确认弹窗。返回 false 则取消启用。</summary>
    public event Func<bool>? ConfirmEnableDeveloperMode;

    // ── 开机自启动 / 托盘常驻（即时生效并持久化）──────────────────────────
    private bool _runAtStartup;
    /// <summary>开机时自动启动：写入/删除当前用户的 Run 启动项（注册表为权威来源，无需管理员）。</summary>
    public bool RunAtStartup
    {
        get => _runAtStartup;
        set
        {
            if (!Set(ref _runAtStartup, value)) return;
            var (ok, msg) = Services.Sys.AutoStart.Set(value);
            if (!ok)
            {
                _runAtStartup = !value;   // revert UI on failure
                OnPropertyChanged(nameof(RunAtStartup));
                Dialogs.Show(Localizer.Format("settings.autostart.fail", msg), Localizer.T("settings.title"),
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            AuditLog.Action($"开机自启动：{(value ? "开启" : "关闭")}");
        }
    }

    private bool _alwaysShowTray;
    /// <summary>始终在系统托盘显示常驻图标：无论窗口是否最小化都常驻一个托盘图标。即时生效。</summary>
    public bool AlwaysShowTray
    {
        get => _alwaysShowTray;
        set
        {
            if (!Set(ref _alwaysShowTray, value)) return;
            _s.AlwaysShowTray = value;
            SettingsStore.Save(_s);
            AuditLog.Action($"托盘常驻图标：{(value ? "开启" : "关闭")}");
            AlwaysShowTrayChanged?.Invoke(value);
        }
    }

    /// <summary>切换「托盘常驻图标」时触发，让主窗口立即显示 / 隐藏常驻托盘图标。</summary>
    public event Action<bool>? AlwaysShowTrayChanged;

    // ── 硬件温度监控（即时生效并持久化）────────────────────────────────────
    public RelayCommand TestTtsCommand { get; }

    private bool _tempMonitorEnabled;
    public bool TempMonitorEnabled
    {
        get => _tempMonitorEnabled;
        set { if (Set(ref _tempMonitorEnabled, value)) { ApplyTempMonitor(); AuditLog.Action($"硬件温度监控：{(value ? "开启" : "关闭")}"); } }
    }

    private bool _tempTts;
    public bool TempTts { get => _tempTts; set { if (Set(ref _tempTts, value)) ApplyTempMonitor(); } }

    private bool _tempCpu;
    public bool TempCpu { get => _tempCpu; set { if (Set(ref _tempCpu, value)) ApplyTempMonitor(); } }
    private bool _tempGpu;
    public bool TempGpu { get => _tempGpu; set { if (Set(ref _tempGpu, value)) ApplyTempMonitor(); } }
    private bool _tempDisk;
    public bool TempDisk { get => _tempDisk; set { if (Set(ref _tempDisk, value)) ApplyTempMonitor(); } }

    private int _cpuTempThreshold;
    public int CpuTempThreshold { get => _cpuTempThreshold; set { if (Set(ref _cpuTempThreshold, ClampTemp(value))) ApplyTempMonitor(); } }
    private int _gpuTempThreshold;
    public int GpuTempThreshold { get => _gpuTempThreshold; set { if (Set(ref _gpuTempThreshold, ClampTemp(value))) ApplyTempMonitor(); } }
    private int _diskTempThreshold;
    public int DiskTempThreshold { get => _diskTempThreshold; set { if (Set(ref _diskTempThreshold, ClampTemp(value))) ApplyTempMonitor(); } }

    /// <summary>Reasonable warn-threshold bounds; keeps a typo from disabling (0) or never firing (999) alerts.</summary>
    private static int ClampTemp(int v) => Math.Clamp(v, 40, 110);

    /// <summary>Central reminder-frequency choices (seconds) — repeat-alert interval while a device stays hot.
    /// The order here matches the ComboBox items in SettingsView.</summary>
    private static readonly int[] ReminderSecondsOptions = { 30, 60, 120, 300, 600 };

    private int _reminderSeconds;
    /// <summary>Selected reminder-frequency index, bound to the settings ComboBox (centralized, not per-device).</summary>
    public int ReminderIndex
    {
        get { var i = Array.IndexOf(ReminderSecondsOptions, _reminderSeconds); return i >= 0 ? i : 1; }
        set
        {
            var sec = ReminderSecondsOptions[Math.Clamp(value, 0, ReminderSecondsOptions.Length - 1)];
            if (_reminderSeconds == sec) return;
            _reminderSeconds = sec;
            OnPropertyChanged();
            ApplyTempMonitor();
        }
    }

    /// <summary>Persist all temperature-monitor settings and reconfigure the background watchdog live.</summary>
    private void ApplyTempMonitor()
    {
        _s.TempMonitorEnabled = _tempMonitorEnabled;
        _s.TempTtsEnabled = _tempTts;
        _s.TempCpuEnabled = _tempCpu;
        _s.TempGpuEnabled = _tempGpu;
        _s.TempDiskEnabled = _tempDisk;
        _s.CpuTempThreshold = _cpuTempThreshold;
        _s.GpuTempThreshold = _gpuTempThreshold;
        _s.DiskTempThreshold = _diskTempThreshold;
        _s.TempReminderSeconds = _reminderSeconds;
        SettingsStore.Save(_s);
        TempMonitor.Configure(TempMonitorConfig.From(_s));
    }

    // ── 关闭主窗口行为（即时持久化）────────────────────────────────────
    private string _closeAction;
    public bool IsCloseAsk { get => _closeAction == "ask"; set { if (value) SetCloseAction("ask"); } }
    public bool IsCloseTray { get => _closeAction == "tray"; set { if (value) SetCloseAction("tray"); } }
    public bool IsCloseExit { get => _closeAction == "exit"; set { if (value) SetCloseAction("exit"); } }

    private void SetCloseAction(string a)
    {
        _closeAction = a;
        OnPropertyChanged(nameof(IsCloseAsk));
        OnPropertyChanged(nameof(IsCloseTray));
        OnPropertyChanged(nameof(IsCloseExit));
        _s.CloseAction = a;
        SettingsStore.Save(_s);
        AuditLog.Action($"关闭行为：{(a switch { "tray" => "最小化到后台常驻", "exit" => "直接退出", _ => "每次询问" })}");
    }

    // ── 终端特效（即时持久化并广播给终端页）──────────────────────────────
    public bool TermHacker
    {
        get => TerminalFx.Hacker;
        set { if (value != TerminalFx.Hacker) { TerminalFx.SetHacker(value); OnPropertyChanged(); } }
    }
    public bool TermCrt
    {
        get => TerminalFx.Crt;
        set { if (value != TerminalFx.Crt) { TerminalFx.SetCrt(value); OnPropertyChanged(); } }
    }
    public bool TermCodeRain
    {
        get => TerminalFx.CodeRain;
        set { if (value != TerminalFx.CodeRain) { TerminalFx.SetCodeRain(value); OnPropertyChanged(); OnPropertyChanged(nameof(CodeRainEnabled)); } }
    }
    public bool CodeRainEnabled => TerminalFx.CodeRain;

    public double TermCodeOpacity
    {
        get => TerminalFx.CodeOpacity;
        set { if (Math.Abs(value - TerminalFx.CodeOpacity) > 0.001) { TerminalFx.SetCodeOpacity(value); OnPropertyChanged(); OnPropertyChanged(nameof(TermCodeOpacityText)); } }
    }
    public string TermCodeOpacityText => $"{TerminalFx.CodeOpacity * 100:0}%";

    public double TermCodeSpeed
    {
        get => TerminalFx.Speed;
        set { if (Math.Abs(value - TerminalFx.Speed) > 0.001) { TerminalFx.SetSpeed(value); OnPropertyChanged(); OnPropertyChanged(nameof(TermCodeSpeedText)); } }
    }
    public string TermCodeSpeedText => $"{TerminalFx.Speed:0.0}×";

    // ── 界面语言（即时切换并持久化）────────────────────────────────────
    private string _lang;
    public bool IsLangZh { get => _lang == Lang.Zh; set { if (value) SetLang(Lang.Zh); } }
    public bool IsLangEn { get => _lang == Lang.En; set { if (value) SetLang(Lang.En); } }
    public bool IsLangDe { get => _lang == Lang.De; set { if (value) SetLang(Lang.De); } }

    private void SetLang(string code)
    {
        if (_lang == code) return;
        _lang = code;
        OnPropertyChanged(nameof(IsLangZh));
        OnPropertyChanged(nameof(IsLangEn));
        OnPropertyChanged(nameof(IsLangDe));
        LocalizationManager.SetLanguage(code);   // live: swaps S.* resources + raises CultureChanged
        SettingsStore.SetLanguage(code);
        AuditLog.Action($"切换语言：{code}");
    }

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
    public RelayCommand ResetCommand { get; }
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
        Note = Localizer.T("settings.saved");
        Saved?.Invoke();
    }

    public static string[] ParseKeywords(string? raw)
        => string.IsNullOrWhiteSpace(raw)
            ? Array.Empty<string>()
            : raw.Split(new[] { ',', ' ', '\n', '\r', '\t', ';', '，', '；' },
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    // ── 重置所有设置与应用数据（三重确认）──────────────────────────────────
    /// <summary>Wipe every setting and all app data back to a clean first-run state — behind three confirmations:
    /// two warning prompts, then a type-the-phrase gate. On success the data folder is deleted and the app restarts.</summary>
    private void ResetAll()
    {
        if (Dialogs.Show(Localizer.T("settings.reset.warn1.body"), Localizer.T("settings.reset.warn1.title"),
                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;

        if (Dialogs.Show(Localizer.T("settings.reset.warn2.body"), Localizer.T("settings.reset.warn2.title"),
                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;

        var dlg = new ConfirmPhraseDialog(
            Localizer.T("settings.reset.confirm.title"),
            Localizer.T("settings.reset.confirm.body"),
            Localizer.T("settings.reset.confirm.phrase")) { Owner = Application.Current.MainWindow };
        if (dlg.ShowDialog() != true) return;

        PerformResetAndRestart();
    }

    /// <summary>Clear app-controlled state (autostart), then delete the entire data folder and relaunch via a
    /// detached shell that waits for this process to exit first (so even locked files like the log are removed).</summary>
    private static void PerformResetAndRestart()
    {
        AuditLog.Action("重置：清除全部设置与应用数据并重启");
        try { Services.Sys.AutoStart.Set(false); } catch { /* best effort */ }
        try
        {
            var exe = Environment.ProcessPath;
            var folder = SettingsStore.Folder;
            // ping = ~2s grace for our process to fully exit; then remove the data folder and relaunch.
            var args = $"/c ping 127.0.0.1 -n 3 >nul & rmdir /s /q \"{folder}\""
                       + (string.IsNullOrEmpty(exe) ? "" : $" & start \"\" \"{exe}\"");
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe", args)
            { CreateNoWindow = true, UseShellExecute = false });
        }
        catch { /* if the shell can't be spawned, still shut down so a manual cleanup is possible */ }
        Application.Current.Shutdown();
    }
}
