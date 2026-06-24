using WinDeploy.Core.Models;
using WinDeploy.Core.Util;

namespace WinDeploy.App.Services.Software;

/// <summary>Checks which winget packages have an available upgrade by parsing one `winget upgrade`
/// run, cached per process. Update availability for an item = its package id appears in that output.</summary>
public static class UpdateChecker
{
    private static string? _cache;

    public static async Task<string> WingetUpgradeOutputAsync(bool force = false)
    {
        if (_cache != null && !force) return _cache;
        try
        {
            var r = await Proc.RunAsync("winget", new[]
            {
                "upgrade", "--include-unknown", "--disable-interactivity", "--accept-source-agreements",
            });
            _cache = r.StdOut;
        }
        catch { _cache = ""; }
        return _cache!;
    }

    public static void Reset() => _cache = null;

    /// <summary>Compare two dotted version strings (e.g. "1.2.0" vs "1.10"). &gt;0 if <paramref name="a"/>
    /// is newer, &lt;0 if older, 0 if equal. Non-numeric trailing parts are ignored.</summary>
    public static int CompareSemver(string a, string b)
    {
        static int[] Parse(string s)
        {
            var core = new string(s.Trim().TakeWhile(c => char.IsDigit(c) || c == '.').ToArray());
            return core.Split('.', StringSplitOptions.RemoveEmptyEntries)
                       .Select(p => int.TryParse(p, out var n) ? n : 0).ToArray();
        }
        var pa = Parse(a); var pb = Parse(b);
        for (var i = 0; i < Math.Max(pa.Length, pb.Length); i++)
        {
            var x = i < pa.Length ? pa[i] : 0;
            var y = i < pb.Length ? pb[i] : 0;
            if (x != y) return x.CompareTo(y);
        }
        return 0;
    }

    /// <summary>True if the item (winget / winget-bundle) has an available upgrade in <paramref name="output"/>.</summary>
    public static bool HasUpgrade(CatalogItem item, string output)
    {
        if (string.IsNullOrEmpty(output)) return false;
        var ins = item.Install;
        if (ins.Method == "winget" && !string.IsNullOrEmpty(ins.Id))
            return output.Contains(ins.Id, StringComparison.OrdinalIgnoreCase);
        if (ins.Method == "winget-bundle" && ins.Ids is { Count: > 0 } ids)
            return ids.Any(id => output.Contains(id, StringComparison.OrdinalIgnoreCase));
        return false;
    }
}
