using System.IO;
using System.Text.Json;

namespace WinDeploy.App.Services.Infra;

public sealed class AppSettings
{
    public string? DevRoot { get; set; }
    public string? ToolsDir { get; set; }
    public string? DownloadDir { get; set; }
    public string? RepoUrl { get; set; }
    public string? Mirror { get; set; }
    public string? RedactKeywords { get; set; }
    public string? Theme { get; set; }   // system | light | dark

    /// <summary>界面语言：zh | en | de。null 表示首次运行未设定（按系统语言自动选择）。</summary>
    public string? Language { get; set; }

    /// <summary>关闭主窗口时的行为：ask（每次询问，默认）| tray（最小化到后台常驻）| exit（直接退出）。</summary>
    public string? CloseAction { get; set; }

    /// <summary>始终在系统托盘显示常驻图标：开启后无论窗口是否最小化都常驻一个托盘图标。默认关闭。</summary>
    public bool AlwaysShowTray { get; set; }

    /// <summary>开发人员模式：开启后软件安装中心显示全部分类；关闭时普通用户仅见
    /// 办公/通讯、游戏平台、系统依赖、媒体四类。默认关闭。</summary>
    public bool DeveloperMode { get; set; }

    /// <summary>终端「黑客风格」配色（绿色磷光 + 辉光 + 近黑底）。null 视为默认开启。</summary>
    public bool? TerminalHackerFx { get; set; }

    /// <summary>终端 CRT 特效（扫描线 + 轻微闪烁）。null 视为默认开启。</summary>
    public bool? TerminalCrtFx { get; set; }

    /// <summary>终端背景代码滚动特效。null 视为默认开启。</summary>
    public bool? TerminalCodeRain { get; set; }

    /// <summary>背景代码不透明度 0.05–1.0（越低前景越清晰）。null 视为默认 0.4。</summary>
    public double? TerminalCodeOpacity { get; set; }

    /// <summary>背景代码滚动速度倍率 0.2–4.0。null 视为默认 1.0。</summary>
    public double? TerminalCodeSpeed { get; set; }

    /// <summary>Custom install locations chosen per item (id → path), so a portable/git/winget app
    /// installed outside its default location is still found after a restart.</summary>
    public Dictionary<string, string> InstallPaths { get; set; } = new();
}

/// <summary>Persists GUI settings to %LOCALAPPDATA%/WinDeploy/settings.json.</summary>
public static class SettingsStore
{
    private static readonly string DirPath =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinDeploy");
    private static readonly string FilePathValue = Path.Combine(DirPath, "settings.json");
    private static readonly JsonSerializerOptions Opt = new() { WriteIndented = true };

    public static string FilePath => FilePathValue;
    public static string Folder => DirPath;

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePathValue))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePathValue)) ?? new();
        }
        catch { /* fall through to defaults */ }

        // Create the file on first run so the path shown in the UI always exists.
        var def = new AppSettings();
        Save(def);
        return def;
    }

    public static void Save(AppSettings s)
    {
        try { Directory.CreateDirectory(DirPath); File.WriteAllText(FilePathValue, JsonSerializer.Serialize(s, Opt)); }
        catch { /* best effort */ }
    }

    /// <summary>Persist the close-button behavior (ask | tray | exit). Load-modify-save.</summary>
    public static void SetCloseAction(string action)
    {
        try { var s = Load(); s.CloseAction = action; Save(s); } catch { /* best effort */ }
    }

    /// <summary>Persist the UI language (zh | en | de). Load-modify-save.</summary>
    public static void SetLanguage(string code)
    {
        try { var s = Load(); s.Language = code; Save(s); } catch { /* best effort */ }
    }

    /// <summary>Persist the terminal hacker-FX toggle. Load-modify-save.</summary>
    public static void SetTerminalHackerFx(bool on)
    {
        try { var s = Load(); s.TerminalHackerFx = on; Save(s); } catch { /* best effort */ }
    }

    /// <summary>Remember (or forget, when <paramref name="path"/> is null/empty) a per-item custom install
    /// path. Load-modify-save so it never clobbers other settings.</summary>
    public static void SetInstallPath(string id, string? path)
    {
        try
        {
            var s = Load();
            s.InstallPaths ??= new();
            if (string.IsNullOrWhiteSpace(path)) s.InstallPaths.Remove(id);
            else s.InstallPaths[id] = path.Trim();
            Save(s);
        }
        catch { /* best effort */ }
    }
}
