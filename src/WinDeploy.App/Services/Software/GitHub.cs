using System.Net.Http;
using System.Text.Json;

namespace WinDeploy.App.Services.Software;

public sealed record GhAsset(string Name, string Url, long Size);

public sealed record GhRelease(string Tag, string Name, string HtmlUrl, bool Prerelease, List<GhAsset> Assets);

/// <summary>Fetches a repo's releases from the GitHub API, cached per repo for 30 minutes to avoid
/// hammering the (unauthenticated, 60/hr) API on repeated installs / cancels / update checks.</summary>
public static class GitHub
{
    private static readonly Dictionary<string, (DateTime At, List<GhRelease> Releases)> ReleasesCache = new();
    private static readonly Dictionary<string, (DateTime At, GhRelease? Release)> LatestCache = new();
    private static readonly object Gate = new();
    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(30);

    /// <summary>Latest-release assets (kept for the MinGW toolchain pickers).</summary>
    public static async Task<List<(string Name, string Url)>> LatestAssetsAsync(string repo)
    {
        var rel = await LatestReleaseAsync(repo);
        return rel?.Assets.Select(a => (a.Name, a.Url)).ToList() ?? new();
    }

    /// <summary>The repo's latest (non-prerelease) release, or null. Cached 30 min unless <paramref name="force"/>.</summary>
    public static async Task<GhRelease?> LatestReleaseAsync(string repo, bool force = false)
    {
        if (!force)
            lock (Gate)
                if (LatestCache.TryGetValue(repo, out var c) && DateTime.Now - c.At < Ttl)
                    return c.Release;

        GhRelease? rel = null;
        try
        {
            var json = await GetAsync($"https://api.github.com/repos/{repo}/releases/latest");
            using var doc = JsonDocument.Parse(json);
            rel = Parse(doc.RootElement);
        }
        catch { rel = null; }

        lock (Gate) LatestCache[repo] = (DateTime.Now, rel);
        return rel;
    }

    /// <summary>All releases (newest first), for picking a specific tag + asset. Cached 30 min.</summary>
    public static async Task<List<GhRelease>> ReleasesAsync(string repo)
    {
        lock (Gate)
            if (ReleasesCache.TryGetValue(repo, out var c) && DateTime.Now - c.At < Ttl)
                return c.Releases;

        var list = new List<GhRelease>();
        var json = await GetAsync($"https://api.github.com/repos/{repo}/releases?per_page=100");
        using var doc = JsonDocument.Parse(json);
        foreach (var e in doc.RootElement.EnumerateArray())
            if (Parse(e) is { } r) list.Add(r);

        lock (Gate) ReleasesCache[repo] = (DateTime.Now, list);
        return list;
    }

    private static GhRelease Parse(JsonElement e)
    {
        var assets = new List<GhAsset>();
        if (e.TryGetProperty("assets", out var arr) && arr.ValueKind == JsonValueKind.Array)
            foreach (var a in arr.EnumerateArray())
                assets.Add(new GhAsset(
                    a.GetProperty("name").GetString() ?? "",
                    a.GetProperty("browser_download_url").GetString() ?? "",
                    a.TryGetProperty("size", out var s) ? s.GetInt64() : 0));

        return new GhRelease(
            e.TryGetProperty("tag_name", out var t) ? t.GetString() ?? "" : "",
            e.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "",
            e.TryGetProperty("html_url", out var h) ? h.GetString() ?? "" : "",
            e.TryGetProperty("prerelease", out var p) && p.GetBoolean(),
            assets);
    }

    private static async Task<string> GetAsync(string url)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("OwO-Win-Deployer");
        return await http.GetStringAsync(url);
    }
}
