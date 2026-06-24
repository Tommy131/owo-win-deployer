using System.Diagnostics;
using System.IO;

namespace WinDeploy.App.Services.Sys;

/// <summary>A one-shot snapshot of running processes' executable paths, so a startup entry whose own exe
/// path can't be resolved (or has no embedded icon) can borrow the icon of its live process. Built once
/// per startup-list refresh to avoid scanning all processes per row.</summary>
public sealed class RunningIcons
{
    private readonly Dictionary<string, string> _byPath = new(StringComparer.OrdinalIgnoreCase); // full path → full path
    private readonly Dictionary<string, string> _byName = new(StringComparer.OrdinalIgnoreCase); // process base name → full path

    public static RunningIcons Snapshot()
    {
        var r = new RunningIcons();
        Process[] all;
        try { all = Process.GetProcesses(); } catch { return r; }
        foreach (var p in all)
        {
            try
            {
                string? path = null;
                try { path = p.MainModule?.FileName; } catch { /* access denied / bitness mismatch */ }
                if (string.IsNullOrEmpty(path)) continue;
                r._byPath[path] = path;
                var name = p.ProcessName;   // no .exe
                if (!string.IsNullOrEmpty(name) && !r._byName.ContainsKey(name)) r._byName[name] = path;
            }
            catch { /* skip */ }
            finally { try { p.Dispose(); } catch { /* ignore */ } }
        }
        return r;
    }

    /// <summary>The live exe path for a startup entry: exact path match, else by the command's exe base
    /// name, else by the entry's display name (a process named like it). Null if no live match.</summary>
    public string? ResolvePath(string? exePath, string entryName)
    {
        if (!string.IsNullOrWhiteSpace(exePath))
        {
            if (_byPath.TryGetValue(exePath!, out var p)) return p;
            var baseName = Path.GetFileNameWithoutExtension(exePath);
            if (!string.IsNullOrEmpty(baseName) && _byName.TryGetValue(baseName, out var p2)) return p2;
        }
        if (!string.IsNullOrWhiteSpace(entryName) && _byName.TryGetValue(entryName.Trim(), out var p3)) return p3;
        return null;
    }
}
