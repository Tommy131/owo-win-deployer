using System.IO;
using System.Text.Json;

namespace WinDeploy.App.Services;

public sealed class AppSettings
{
    public string? DevRoot { get; set; }
    public string? ToolsDir { get; set; }
    public string? DownloadDir { get; set; }
    public string? RepoUrl { get; set; }
    public string? Mirror { get; set; }
    public string? RedactKeywords { get; set; }
    public string? Theme { get; set; }   // system | light | dark

    /// <summary>开发人员模式：开启后软件安装中心显示全部分类；关闭时普通用户仅见
    /// 办公/通讯、游戏平台、系统依赖、媒体四类。默认关闭。</summary>
    public bool DeveloperMode { get; set; }

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
