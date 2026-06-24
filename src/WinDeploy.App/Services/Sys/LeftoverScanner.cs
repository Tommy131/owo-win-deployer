using System.IO;
using WinDeploy.Core;
using WinDeploy.Core.Engine;
using WinDeploy.Core.Models;

namespace WinDeploy.App.Services.Sys;

public sealed record JunkEntry(string Path, long Bytes)
{
    public string SizeText => Bytes >= 1024L * 1024 * 1024
        ? $"{Bytes / 1024.0 / 1024 / 1024:0.0} GB"
        : $"{Bytes / 1024.0 / 1024:0.0} MB";
}

/// <summary>Finds leftover folders for an item (install dir / config dir / portable·git target) so the
/// user can reclaim space after an uninstall. Capture candidates BEFORE uninstalling (ARP disappears),
/// then scan which still exist afterwards.</summary>
public static class LeftoverScanner
{
    /// <summary>Candidate leftover directories (resolved, de-duplicated; may not all exist yet).</summary>
    public static List<string> Candidates(CatalogItem item, PathResolver pr)
    {
        var set = new List<string>();
        void Add(string? p) { if (!string.IsNullOrWhiteSpace(p)) set.Add(p!); }

        var t = Launcher.ProcessTarget(item, pr);
        if (t?.Dir != null) Add(t.Value.Dir);

        var arp = Arp.Find(item.Detect?.Arp, item.Name, null);
        Add(arp?.InstallLocation);

        var ins = item.Install;
        if (ins.Method == "portable" && ins.ExtractTo != null) Add(pr.Resolve(item.InstallPathOverride ?? ins.ExtractTo));
        if (ins.Method == "git" && ins.Dest != null) Add(pr.Resolve(item.InstallPathOverride ?? ins.Dest));

        if (item.Config?.Target != null) Add(pr.Resolve(item.Config.Target));

        return set
            .Where(p => p.Length > 3 && !IsRiskyRoot(p))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Of the given paths, the directories that still exist, with their sizes.</summary>
    public static List<JunkEntry> Scan(IEnumerable<string> paths)
    {
        var list = new List<JunkEntry>();
        foreach (var p in paths)
        {
            try { if (Directory.Exists(p)) list.Add(new JunkEntry(p, Cleanup.DirSize(p))); }
            catch { /* skip */ }
        }
        return list;
    }

    /// <summary>Delete the given folders. Returns (count, bytesFreed).</summary>
    public static (int Count, long Freed) Delete(IEnumerable<JunkEntry> entries)
    {
        var count = 0; long freed = 0;
        foreach (var e in entries)
        {
            try { if (Directory.Exists(e.Path)) { Directory.Delete(e.Path, true); count++; freed += e.Bytes; } }
            catch { /* in use / denied */ }
        }
        return (count, freed);
    }

    /// <summary>Guard against ever proposing a drive root or a top-level system folder.</summary>
    private static bool IsRiskyRoot(string p)
    {
        var full = p.TrimEnd('\\', '/');
        if (full.Length <= 3) return true;                         // C:\
        var depth = full.Count(c => c is '\\' or '/');
        return depth < 2;                                          // require at least X:\a\b
    }
}
