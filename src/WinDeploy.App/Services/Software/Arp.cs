using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace WinDeploy.App.Services.Software;

/// <summary>One Add/Remove-Programs (ARP) entry from the Windows uninstall registry.</summary>
public sealed class ArpEntry
{
    public string DisplayName { get; init; } = "";
    public string? DisplayVersion { get; init; }
    public string? Publisher { get; init; }
    public string? InstallDate { get; init; }
    public string? Homepage { get; init; }
    public string? InstallLocation { get; init; }
    public string? DisplayIcon { get; init; }
    public long EstimatedSizeKb { get; init; }
}

/// <summary>Reads installed-program metadata (version, size, date, publisher, homepage) from the
/// registry — richer and faster than winget for already-installed apps. Read-only, no admin needed.</summary>
public static class Arp
{
    private static List<ArpEntry>? _cache;

    /// <summary>Drop the cached registry snapshot so the next read reflects installs/updates/uninstalls.</summary>
    public static void Refresh() => _cache = null;

    public static IReadOnlyList<ArpEntry> All()
    {
        if (_cache != null) return _cache;
        var list = new List<ArpEntry>();
        Scan(Registry.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall", list);
        Scan(Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall", list);
        Scan(Registry.CurrentUser, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall", list);
        _cache = list;
        return list;
    }

    private static void Scan(RegistryKey root, string path, List<ArpEntry> list)
    {
        try
        {
            using var key = root.OpenSubKey(path);
            if (key == null) return;
            foreach (var sub in key.GetSubKeyNames())
            {
                using var e = key.OpenSubKey(sub);
                var name = e?.GetValue("DisplayName") as string;
                if (string.IsNullOrWhiteSpace(name)) continue;
                list.Add(new ArpEntry
                {
                    DisplayName = name,
                    DisplayVersion = e!.GetValue("DisplayVersion") as string,
                    Publisher = e.GetValue("Publisher") as string,
                    InstallDate = e.GetValue("InstallDate") as string,
                    Homepage = (e.GetValue("URLInfoAbout") as string) ?? (e.GetValue("HelpLink") as string),
                    InstallLocation = e.GetValue("InstallLocation") as string,
                    DisplayIcon = e.GetValue("DisplayIcon") as string,
                    EstimatedSizeKb = e.GetValue("EstimatedSize") is int sz ? sz : 0,
                });
            }
        }
        catch { /* skip unreadable hives */ }
    }

    /// <summary>Best-effort match by display-name. CJK hints match as a substring; ASCII hints match
    /// on a word boundary (so "Go" hits "Go Programming…" but not "Google Chrome").</summary>
    public static ArpEntry? Find(params string?[] hints)
    {
        var all = All();
        foreach (var h in hints)
        {
            if (string.IsNullOrWhiteSpace(h)) continue;

            var exact = all.FirstOrDefault(e => e.DisplayName.Equals(h, StringComparison.OrdinalIgnoreCase));
            if (exact != null) return exact;

            ArpEntry? hit;
            if (h.Any(c => c >= 0x4E00 && c <= 0x9FFF))
            {
                hit = all.FirstOrDefault(e => e.DisplayName.Contains(h, StringComparison.OrdinalIgnoreCase));
            }
            else
            {
                var re = new Regex(@"\b" + Regex.Escape(h) + @"\b", RegexOptions.IgnoreCase);
                hit = all.FirstOrDefault(e => re.IsMatch(e.DisplayName));
            }
            if (hit != null) return hit;
        }
        return null;
    }
}
