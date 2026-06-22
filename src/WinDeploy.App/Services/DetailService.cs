using System.Diagnostics;
using System.IO;
using System.Text;
using WinDeploy.Core.Models;
using WinDeploy.Core.Util;

namespace WinDeploy.App.Services;

/// <summary>Resolved software detail fields. Mutable so enrichment can fill gaps in place.</summary>
public sealed class DetailInfo
{
    public string Version = "—";
    public string Size = "—";
    public string InstallDate = "—";
    public string Publisher = "—";
    public string Homepage = "—";
    public bool Enriched;
    internal string? InstallLoc;
}

/// <summary>
/// Computes and caches per-software detail (version/size/date/publisher/homepage). Sources, in order:
/// ARP registry → catalog → built-in homepage map → `winget show` → install-folder size/date.
/// Cached by id so the work runs once (prefetched during lazy-load, reused on card click).
/// </summary>
public static class DetailService
{
    private static readonly Dictionary<string, DetailInfo> Cache = new();
    private static readonly object Gate = new();

    public static DetailInfo? GetCached(string id)
    {
        lock (Gate) return Cache.TryGetValue(id, out var v) ? v : null;
    }

    /// <summary>Drop cached detail/versions for an id so the next fetch recomputes (after update/uninstall).</summary>
    public static void Invalidate(string id)
    {
        lock (Gate) { Cache.Remove(id); VersionCache.Remove(id); }
    }

    private static readonly Dictionary<string, IReadOnlyList<string>> VersionCache = new();

    /// <summary>Available versions from winget (network), newest first. Cached per id.</summary>
    public static async Task<IReadOnlyList<string>> GetVersionsAsync(string id)
    {
        lock (Gate)
            if (VersionCache.TryGetValue(id, out var cached)) return cached;

        var list = new List<string>();
        try
        {
            var r = await Proc.RunAsync("winget", new[] { "show", "--versions", "--id", id, "-e", "--disable-interactivity", "--accept-source-agreements" });
            if (r.Ok)
            {
                var started = false;
                foreach (var raw in r.StdOut.Split('\n'))
                {
                    var line = raw.Trim();
                    if (line.Length == 0) continue;
                    if (!started) { if (line.Length >= 3 && line.All(c => c == '-')) started = true; continue; }
                    list.Add(line);
                    if (list.Count >= 30) break;
                }
            }
        }
        catch { /* ignore */ }

        IReadOnlyList<string> result = list;
        lock (Gate) VersionCache[id] = result;
        return result;
    }

    public static async Task<DetailInfo> FetchAsync(CatalogItem item)
    {
        lock (Gate)
            if (Cache.TryGetValue(item.Id, out var c) && c.Enriched) return c;

        var info = ComputeBasic(item);
        lock (Gate) Cache[item.Id] = info;

        await EnrichAsync(item, info);
        info.Enriched = true;
        lock (Gate) Cache[item.Id] = info;
        return info;
    }

    private static DetailInfo ComputeBasic(CatalogItem item)
    {
        var info = new DetailInfo();
        var e = Arp.Find(item.Detect?.Arp, item.Name, IdToName(item.Install.Id));
        if (e != null)
        {
            if (!string.IsNullOrWhiteSpace(e.DisplayVersion)) info.Version = e.DisplayVersion!;
            if (e.EstimatedSizeKb > 0) info.Size = FormatKb(e.EstimatedSizeKb);
            if (!string.IsNullOrWhiteSpace(e.InstallDate)) info.InstallDate = FormatDate(e.InstallDate!);
            if (!string.IsNullOrWhiteSpace(e.Publisher)) info.Publisher = e.Publisher!;
            if (!string.IsNullOrWhiteSpace(e.Homepage)) info.Homepage = e.Homepage!;
            info.InstallLoc = !string.IsNullOrWhiteSpace(e.InstallLocation) ? e.InstallLocation : DirOf(e.DisplayIcon);
        }
        if (info.Version == "—" && item.Version != null) info.Version = item.Version;
        if (info.Homepage == "—" && !string.IsNullOrWhiteSpace(item.Homepage)) info.Homepage = item.Homepage!;
        if (info.Homepage == "—" && Homepages.TryGetValue(item.Id, out var hp)) info.Homepage = hp;
        if (info.Publisher == "—" && Publishers.TryGetValue(item.Id, out var pub)) info.Publisher = pub;
        return info;
    }

    private static async Task EnrichAsync(CatalogItem item, DetailInfo info)
    {
        var id = item.Install.Id;
        if (!string.IsNullOrEmpty(id) && (info.Version == "—" || info.Publisher == "—" || info.Homepage == "—"))
        {
            try
            {
                var r = await Proc.RunAsync("winget", new[] { "show", "--id", id, "-e", "--disable-interactivity", "--accept-source-agreements" });
                if (r.Ok) ParseWingetShow(r.StdOut, info);
            }
            catch { /* ignore */ }
        }

        if ((info.Size == "—" || info.InstallDate == "—") && !string.IsNullOrWhiteSpace(info.InstallLoc) && Directory.Exists(info.InstallLoc))
        {
            try
            {
                if (info.InstallDate == "—")
                    info.InstallDate = Directory.GetCreationTime(info.InstallLoc!).ToString("yyyy-MM-dd");
                if (info.Size == "—")
                {
                    var bytes = await Task.Run(() => DirSize(info.InstallLoc!));
                    if (bytes > 0) info.Size = FormatBytes(bytes);
                }
            }
            catch { /* ignore */ }
        }
    }

    private static void ParseWingetShow(string output, DetailInfo info)
    {
        foreach (var line in output.Split('\n'))
        {
            var i = line.IndexOfAny(new[] { ':', '：' });
            if (i <= 0) continue;
            var label = line[..i].Trim();
            var value = line[(i + 1)..].Trim();
            if (value.Length == 0) continue;

            if (info.Version == "—" && Is(label, "Version", "版本")) info.Version = value;
            else if (info.Publisher == "—" && Is(label, "Publisher", "发布者")) info.Publisher = value;
            else if (info.Homepage == "—" && Is(label, "Homepage", "主页") && value.StartsWith("http", StringComparison.OrdinalIgnoreCase)) info.Homepage = value;
        }
    }

    private static bool Is(string label, params string[] names)
        => names.Any(n => label.Equals(n, StringComparison.OrdinalIgnoreCase));

    /// <summary>Directory of a DisplayIcon path like "C:\App\app.exe,0".</summary>
    private static string? DirOf(string? displayIcon)
    {
        if (string.IsNullOrWhiteSpace(displayIcon)) return null;
        var p = displayIcon.Trim().Trim('"');
        var comma = p.LastIndexOf(',');
        if (comma > 1 && p.Length - comma <= 4) p = p[..comma];
        p = p.Trim().Trim('"');
        try { return Path.GetDirectoryName(p); } catch { return null; }
    }

    private static long DirSize(string path)
    {
        long total = 0;
        var sw = Stopwatch.StartNew();
        try
        {
            foreach (var f in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            {
                try { total += new FileInfo(f).Length; } catch { /* skip */ }
                if (sw.Elapsed.TotalSeconds > 5) return -1;   // too big to measure quickly
            }
        }
        catch { /* skip */ }
        return total;
    }

    private static string? IdToName(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        var last = id.Split('.').Last();
        var sb = new StringBuilder();
        for (var i = 0; i < last.Length; i++)
        {
            if (i > 0 && char.IsUpper(last[i]) && !char.IsUpper(last[i - 1])) sb.Append(' ');
            sb.Append(last[i]);
        }
        return sb.ToString();
    }

    private static string FormatKb(long kb)
    {
        if (kb <= 0) return "—";
        double mb = kb / 1024.0;
        return mb >= 1024 ? $"{mb / 1024:0.0} GB" : $"{mb:0} MB";
    }

    private static string FormatBytes(long bytes)
    {
        double mb = bytes / (1024.0 * 1024);
        return mb >= 1024 ? $"{mb / 1024:0.0} GB" : $"{mb:0} MB";
    }

    private static string FormatDate(string raw)
        => raw.Length == 8 && long.TryParse(raw, out _)
            ? $"{raw[..4]}-{raw.Substring(4, 2)}-{raw.Substring(6, 2)}"
            : raw;

    private static readonly Dictionary<string, string> Homepages = new()
    {
        ["git"] = "https://git-scm.com",
        ["gh"] = "https://cli.github.com",
        ["nodejs"] = "https://nodejs.org",
        ["python"] = "https://www.python.org",
        ["miniconda"] = "https://docs.conda.io/projects/miniconda",
        ["jdk17"] = "https://www.oracle.com/java/",
        ["go"] = "https://go.dev",
        ["dotnet-sdk"] = "https://dotnet.microsoft.com",
        ["cmake"] = "https://cmake.org",
        ["ffmpeg"] = "https://ffmpeg.org",
        ["pandoc"] = "https://pandoc.org",
        ["mingw"] = "https://winlibs.com",
        ["flutter"] = "https://flutter.dev",
        ["vcredist"] = "https://learn.microsoft.com/cpp/windows/latest-supported-vc-redist",
        ["windows-terminal"] = "https://aka.ms/terminal",
        ["huorong"] = "https://www.huorong.cn",
        ["vscode"] = "https://code.visualstudio.com",
        ["vscode-ext"] = "https://marketplace.visualstudio.com/vscode",
        ["vs2026"] = "https://visualstudio.microsoft.com",
        ["android-studio"] = "https://developer.android.com/studio",
        ["arduino"] = "https://www.arduino.cc/en/software",
        ["unity-hub"] = "https://unity.com",
        ["sublime-merge"] = "https://www.sublimemerge.com",
        ["comfyui"] = "https://www.comfy.org",
        ["lmstudio"] = "https://lmstudio.ai",
        ["claude"] = "https://claude.ai",
        ["windsurf"] = "https://windsurf.com",
        ["hermes-agent"] = "https://hermes-agent.nousresearch.com/",
        ["v2rayn"] = "https://github.com/2dust/v2rayN",
        ["snipaste"] = "https://www.snipaste.com",
        ["screentogif"] = "https://www.screentogif.com",
        ["crystaldiskinfo"] = "https://crystalmark.info/en/software/crystaldiskinfo/",
        ["cc-switch"] = "https://github.com/farion1231/cc-switch",
        ["7zip"] = "https://www.7-zip.org",
        ["wechat"] = "https://weixin.qq.com",
        ["wecom"] = "https://work.weixin.qq.com",
        ["feishu"] = "https://www.feishu.cn",
        ["tencent-meeting"] = "https://meeting.tencent.com",
        ["obs"] = "https://obsproject.com",
        ["vlc"] = "https://www.videolan.org",
        ["irfanview"] = "https://www.irfanview.com",
        ["netease-music"] = "https://music.163.com",
        ["dbgate"] = "https://dbgate.org",
        ["apifox"] = "https://apifox.com",
        ["winscp"] = "https://winscp.net",
        ["vmware"] = "https://www.vmware.com/products/workstation-pro.html",
        ["steam"] = "https://store.steampowered.com/about/",
        ["epic"] = "https://store.epicgames.com",
    };

    /// <summary>Publisher per item — app-local, so it shows even on a bare machine (overridden by ARP when installed).</summary>
    private static readonly Dictionary<string, string> Publishers = new()
    {
        ["git"] = "The Git Development Community",
        ["gh"] = "GitHub, Inc.",
        ["nodejs"] = "Node.js Foundation",
        ["python"] = "Python Software Foundation",
        ["miniconda"] = "Anaconda, Inc.",
        ["jdk17"] = "Oracle Corporation",
        ["go"] = "Google / The Go Authors",
        ["dotnet-sdk"] = "Microsoft Corporation",
        ["cmake"] = "Kitware, Inc.",
        ["ffmpeg"] = "FFmpeg",
        ["pandoc"] = "John MacFarlane",
        ["mingw"] = "WinLibs (Brecht Sanders)",
        ["flutter"] = "Google LLC",
        ["vcredist"] = "Microsoft Corporation",
        ["windows-terminal"] = "Microsoft Corporation",
        ["huorong"] = "北京火绒网络科技有限公司",
        ["vscode"] = "Microsoft Corporation",
        ["vscode-ext"] = "Microsoft Corporation",
        ["vs2026"] = "Microsoft Corporation",
        ["android-studio"] = "Google LLC",
        ["arduino"] = "Arduino SA",
        ["unity-hub"] = "Unity Technologies",
        ["sublime-merge"] = "Sublime HQ Pty Ltd",
        ["comfyui"] = "Comfy Org",
        ["lmstudio"] = "Element Labs (LM Studio)",
        ["claude"] = "Anthropic",
        ["windsurf"] = "Codeium",
        ["hermes-agent"] = "Nous Research",
        ["v2rayn"] = "2dust",
        ["snipaste"] = "Le Liu",
        ["screentogif"] = "Nicke Manarin",
        ["crystaldiskinfo"] = "Crystal Dew World",
        ["cc-switch"] = "farion1231",
        ["7zip"] = "Igor Pavlov",
        ["wechat"] = "腾讯科技(深圳)有限公司",
        ["wecom"] = "腾讯科技(深圳)有限公司",
        ["feishu"] = "Beijing Feishu Technology Co., Ltd.",
        ["tencent-meeting"] = "腾讯科技(深圳)有限公司",
        ["obs"] = "OBS Project",
        ["vlc"] = "VideoLAN",
        ["irfanview"] = "Irfan Skiljan",
        ["netease-music"] = "网易公司",
        ["dbgate"] = "Jan Prochazka",
        ["apifox"] = "Apifox Team",
        ["winscp"] = "Martin Prikryl",
        ["vmware"] = "Broadcom (VMware)",
        ["steam"] = "Valve Corporation",
        ["epic"] = "Epic Games, Inc.",
    };
}
