using System.Text.RegularExpressions;
using WinDeploy.Core.I18n;
using WinDeploy.Core.Util;

namespace WinDeploy.App.Services.Software;

public sealed record WinLibsVariant(string Label, string Url, bool Recommended);

/// <summary>Lists MinGW-w64 toolchain variants for the current architecture from GitHub releases
/// (WinLibs and niXman mingw-builds), so the user can pick a compiler build (posix recommended).
/// Asset lists are cached via <see cref="GitHub"/>.</summary>
public static class WinLibs
{
    /// <summary>WinLibs (brechtsanders) — uses .zip assets.</summary>
    public static async Task<List<WinLibsVariant>> GetVariantsAsync(bool x64)
    {
        var arch = Arch.MingwToken(x64);
        var assets = await GitHub.LatestAssetsAsync("brechtsanders/winlibs_mingw");
        var result = new List<WinLibsVariant>();
        foreach (var (name, url) in assets)
        {
            if (!name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) continue;
            if (!name.Contains(arch, StringComparison.OrdinalIgnoreCase)) continue;
            result.Add(Variant(name, url));
        }
        return Sort(result);
    }

    /// <summary>niXman mingw-builds-binaries — .7z assets (needs 7-Zip to extract).</summary>
    public static async Task<List<WinLibsVariant>> GetMingwBuildsAsync(bool x64)
    {
        var arch = Arch.MingwToken(x64);
        var assets = await GitHub.LatestAssetsAsync("niXman/mingw-builds-binaries");
        var result = new List<WinLibsVariant>();
        foreach (var (name, url) in assets)
        {
            if (!name.EndsWith(".7z", StringComparison.OrdinalIgnoreCase) && !name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) continue;
            if (!name.StartsWith(arch + "-", StringComparison.OrdinalIgnoreCase)) continue;
            result.Add(Variant(name, url));
        }
        return Sort(result);
    }

    private static WinLibsVariant Variant(string name, string url)
    {
        var threads = name.Contains("posix") ? "posix"
                    : name.Contains("win32") ? "win32"
                    : name.Contains("mcf") ? "mcf" : "?";
        var runtime = name.Contains("ucrt", StringComparison.OrdinalIgnoreCase) ? "UCRT"
                    : name.Contains("msvcrt", StringComparison.OrdinalIgnoreCase) ? "MSVCRT" : "?";
        var exc = name.Contains("-seh-") ? "seh" : name.Contains("-dwarf-") ? "dwarf" : name.Contains("-sjlj-") ? "sjlj" : "";
        var llvm = name.Contains("llvm", StringComparison.OrdinalIgnoreCase) || name.Contains("clang", StringComparison.OrdinalIgnoreCase);
        var gcc = Regex.Match(name, @"(\d+\.\d+\.\d+)") is { Success: true } m ? m.Groups[1].Value : "?";

        var label = $"{Localizer.Format("winlibs.threads", threads)} · {runtime}{(exc.Length > 0 ? " · " + exc : "")}{(llvm ? Localizer.T("winlibs.llvm") : "")} · GCC {gcc}";
        var recommended = threads == "posix" && runtime == "UCRT" && !llvm;
        return new WinLibsVariant(label, url, recommended);
    }

    private static List<WinLibsVariant> Sort(List<WinLibsVariant> list)
        => list.OrderByDescending(v => v.Recommended).ThenBy(v => v.Label, StringComparer.OrdinalIgnoreCase).ToList();
}
