using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using WinDeploy.Core.Models;

namespace WinDeploy.App.Services.Software;

/// <summary>Resolves the best icon for an arbitrary entry (startup item / running process / catalog id):
/// the bundled icon cache (assets/icons/&lt;id&gt;.png) when the entry maps to a catalog item, otherwise the
/// app's real icon extracted live from its .exe. Initialized once with the catalog + repo root.</summary>
public static class IconResolver
{
    private static string _repoRoot = "";
    private static readonly Dictionary<string, string> ByKey = new();   // normalized name/id/proc → catalog id

    public static void Init(Catalog catalog, string repoRoot)
    {
        _repoRoot = repoRoot;
        ByKey.Clear();
        foreach (var it in catalog.Items)
        {
            Add(it.Id, it.Id);
            Add(it.Name, it.Id);
            if (it.Detect?.Proc is { } p) Add(p, it.Id);
        }
    }

    private static void Add(string? s, string id)
    {
        var k = Norm(s);
        if (k.Length >= 2 && !ByKey.ContainsKey(k)) ByKey[k] = id;
    }

    /// <summary>The bundled cache icon for a known catalog id, or null.</summary>
    public static ImageSource? FromCatalogId(string id) => LoadPng(PngPath(id));

    /// <summary>Cache icon if the name or exe maps to a catalog item; otherwise the exe's real icon.</summary>
    public static ImageSource? Resolve(string? name, string? exePath)
    {
        var id = MatchId(name) ?? MatchId(exePath != null ? Path.GetFileNameWithoutExtension(exePath) : null);
        if (id != null && LoadPng(PngPath(id)) is { } cached) return cached;
        if (!string.IsNullOrWhiteSpace(exePath))
            try { return IconExtractor.FromExe(exePath); } catch { /* letter fallback */ }
        return null;
    }

    private static string? MatchId(string? s)
        => Norm(s) is { Length: >= 2 } k && ByKey.TryGetValue(k, out var id) ? id : null;

    private static string PngPath(string id) => Path.Combine(_repoRoot, "assets", "icons", id + ".png");

    private static ImageSource? LoadPng(string path)
    {
        try
        {
            if (string.IsNullOrEmpty(_repoRoot) || !File.Exists(path)) return null;
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            bmp.UriSource = new Uri(path);
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch { return null; }
    }

    private static string Norm(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var sb = new System.Text.StringBuilder(s.Length);
        foreach (var c in s) if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
        return sb.ToString();
    }
}
