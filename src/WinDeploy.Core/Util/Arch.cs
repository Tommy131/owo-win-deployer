using System.Runtime.InteropServices;

namespace WinDeploy.Core.Util;

public enum CpuArch { X64, Arm64, X86 }

/// <summary>Single source of truth for CPU-architecture matching: the current OS arch, the MinGW token,
/// and whether a GitHub release asset (by file name) is usable on this machine — so other-arch builds
/// (e.g. arm64 on an x64 PC) are filtered out everywhere instead of each caller re-implementing it.</summary>
public static class Arch
{
    public static CpuArch Current => RuntimeInformation.OSArchitecture switch
    {
        Architecture.Arm64 => CpuArch.Arm64,
        Architecture.X86 => CpuArch.X86,
        _ => CpuArch.X64,
    };

    /// <summary>MinGW / winlibs arch token for the given bitness (x86_64 / i686).</summary>
    public static string MingwToken(bool x64) => x64 ? "x86_64" : "i686";

    /// <summary>True if a release asset is usable on this Windows machine: not a non-Windows OS build, and
    /// either matching the current CPU arch or arch-neutral. Drops irrelevant downloads from the picker.</summary>
    public static bool AssetUsable(string assetName)
    {
        var n = assetName.ToLowerInvariant();

        // Drop non-Windows OS builds (this is a Windows deployer).
        if (Has(n, "macos", "darwin", ".dmg", ".pkg", "osx", "-mac.", "_mac.", "-mac-",
                   "linux", ".deb", ".rpm", ".appimage", ".tar.gz", ".tar.xz", ".tar.bz2",
                   "android", ".apk", "ios", ".ipa"))
            return false;

        var arm = Has(n, "arm64", "aarch64");
        var x64 = Has(n, "x64", "amd64", "win64", "x86_64", "x86-64", "win-x64");
        var x86 = !x64 && Has(n, "x86", "win32", "ia32", "i686", "win-x86");

        return Current switch
        {
            CpuArch.X64 => !arm,                 // x64 runs x64 + x86 (WOW64); arm64 won't run
            CpuArch.Arm64 => true,               // Win11 arm64 runs native + emulated x64/x86
            CpuArch.X86 => !x64 && !arm,         // x86 only
            _ => true,
        };
    }

    private static bool Has(string s, params string[] tokens)
    {
        foreach (var t in tokens) if (s.Contains(t)) return true;
        return false;
    }
}
