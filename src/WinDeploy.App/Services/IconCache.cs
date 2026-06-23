using System.IO;
using System.Net.Http;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace WinDeploy.App.Services;

/// <summary>Fetches and caches HD brand icons for catalog items that ship no bundled assets/icons/&lt;id&gt;.png,
/// so cards show a real icon instead of a letter badge. Sources (best-effort, in order): the dashboard-icons
/// CDN (by slug), then the homepage favicon. Cached PNGs live in %LOCALAPPDATA%/WinDeploy/iconcache and are
/// fetched once; failures fall back to the letter badge (no regression).</summary>
public static class IconCache
{
    private static readonly string Dir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinDeploy", "iconcache");

    public static string PathFor(string id) => Path.Combine(Dir, id + ".png");
    public static bool Has(string id) => File.Exists(PathFor(id));

    // id → dashboard-icons slug, for ids that differ from the icon repo's slug. Unlisted ids try the id as-is.
    private static readonly Dictionary<string, string> Slug = new(StringComparer.OrdinalIgnoreCase)
    {
        ["chrome"] = "google-chrome", ["edge"] = "microsoft-edge", ["vscode"] = "visual-studio-code",
        ["vs2026"] = "visual-studio", ["visual-studio"] = "visual-studio", ["dotnet-sdk"] = "dotnet",
        ["docker-desktop"] = "docker", ["obs"] = "obs-studio", ["netease-music"] = "netease-cloud-music",
        ["wps"] = "wps-office", ["mongodb-compass"] = "mongodb", ["redis-insight"] = "redis",
        ["ea-app"] = "ea", ["unity-hub"] = "unity", ["miniconda"] = "anaconda", ["jdk17"] = "java",
        ["windows-terminal"] = "windows-terminal", ["powertoys"] = "powertoys", ["pwsh"] = "powershell",
        ["notepad-plus-plus"] = "notepad-plus-plus", ["docker-desktop"] = "docker", ["sublime-text"] = "sublime-text",
        ["qq-browser"] = "tencent-qq", ["qq"] = "tencent-qq", ["baidu-netdisk"] = "baidu",
    };

    /// <summary>For each item missing a bundled and cached icon, try to download one. Throttled; best-effort.
    /// Returns how many icons were newly cached.</summary>
    public static async Task<int> FetchMissingAsync(IReadOnlyList<(string Id, string? Homepage, string Name)> items, string repoRoot)
    {
        var todo = items.Where(i =>
            !File.Exists(Path.Combine(repoRoot, "assets", "icons", i.Id + ".png")) && !Has(i.Id)).ToList();
        if (todo.Count == 0) return 0;

        try { Directory.CreateDirectory(Dir); } catch { return 0; }

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("WinDeploy-IconCache");
        using var gate = new SemaphoreSlim(6);
        var ok = 0;

        var tasks = todo.Select(async it =>
        {
            await gate.WaitAsync();
            try { if (await FetchOneAsync(http, it)) Interlocked.Increment(ref ok); }
            catch { /* best-effort */ }
            finally { gate.Release(); }
        });
        await Task.WhenAll(tasks);
        return ok;
    }

    private static async Task<bool> FetchOneAsync(HttpClient http, (string Id, string? Homepage, string Name) it)
    {
        string? lastErr = null;
        foreach (var url in CandidateUrls(it))
        {
            try
            {
                var bytes = await http.GetByteArrayAsync(url);
                if (bytes.Length < 200 || !LooksLikeImage(bytes)) { lastErr = $"not-image({bytes.Length}) {url}"; continue; }
                await File.WriteAllBytesAsync(PathFor(it.Id), bytes);
                return true;
            }
            catch (Exception ex) { lastErr = $"{ex.GetType().Name}: {ex.Message} @ {url}"; }
        }
        if (lastErr != null) AuditLog.Warn($"图标失败 {it.Id} — {lastErr}");
        return false;
    }

    private static IEnumerable<string> CandidateUrls((string Id, string? Homepage, string Name) it)
    {
        var slug = Slug.TryGetValue(it.Id, out var s) ? s : it.Id;
        yield return $"https://cdn.jsdelivr.net/gh/homarr-labs/dashboard-icons/png/{slug}.png";

        var host = HostOf(it.Homepage);
        if (host != null)
        {
            yield return $"https://www.google.com/s2/favicons?sz=128&domain={host}";
            yield return $"https://icons.duckduckgo.com/ip3/{host}.ico";
        }
    }

    private static string? HostOf(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        try
        {
            var u = new Uri(url);
            var h = u.Host;
            return h.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? h[4..] : h;
        }
        catch { return null; }
    }

    /// <summary>Sniff common image magic bytes (PNG/JPG/GIF/BMP/ICO/WEBP) so a 404 HTML page isn't cached.</summary>
    private static bool LooksLikeImage(byte[] b)
    {
        if (b.Length < 4) return false;
        if (b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4E && b[3] == 0x47) return true;             // PNG
        if (b[0] == 0xFF && b[1] == 0xD8) return true;                                              // JPG
        if (b[0] == 0x47 && b[1] == 0x49 && b[2] == 0x46) return true;                              // GIF
        if (b[0] == 0x42 && b[1] == 0x4D) return true;                                              // BMP
        if (b[0] == 0x00 && b[1] == 0x00 && b[2] == 0x01 && b[3] == 0x00) return true;             // ICO
        if (b.Length >= 12 && b[0] == 0x52 && b[1] == 0x49 && b[2] == 0x46 && b[3] == 0x46
            && b[8] == 0x57 && b[9] == 0x45 && b[10] == 0x42 && b[11] == 0x50) return true;         // WEBP
        return false;
    }

    /// <summary>Load a cached icon (frozen), or null. Safe to call on a background thread.</summary>
    public static ImageSource? Load(string id)
    {
        var path = PathFor(id);
        if (!File.Exists(path)) return null;
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;          // don't lock the file
            bmp.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            bmp.UriSource = new Uri(path);
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch { return null; }
    }
}
