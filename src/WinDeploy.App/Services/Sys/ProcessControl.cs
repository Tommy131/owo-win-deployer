using System.Diagnostics;
using System.IO;
using WinDeploy.Core;
using WinDeploy.Core.Models;

namespace WinDeploy.App.Services.Sys;

/// <summary>One running process belonging to a catalog item.</summary>
public sealed record ProcItem(int Pid, string Name, long MemBytes, string? Path, TimeSpan CpuTime, int SessionId = -1);

/// <summary>Finds and controls the processes of a catalog item. Resolving the target exe (process name +
/// install dir) is split out so the live process page can resolve once and sample cheaply each tick.</summary>
public static class ProcessControl
{
    /// <summary>The interactive (foreground) Windows session this app runs in. Processes in this session are
    /// "user-level"; everything else (session 0 services / SYSTEM) is "system-level" — the Task-Manager split.</summary>
    public static readonly int CurrentSessionId = SafeCurrentSession();

    private static int SafeCurrentSession()
    {
        try { return Process.GetCurrentProcess().SessionId; } catch { return -1; }
    }

    private static int SafeSessionId(Process p)
    {
        try { return p.SessionId; } catch { return -1; }
    }

    /// <summary>The item's process name + install dir (for verification), or null if unresolved.</summary>
    public static (string Proc, string? Dir)? ResolveTarget(CatalogItem item, PathResolver pr)
        => Launcher.ProcessTarget(item, pr);

    /// <summary>Running processes named <paramref name="procName"/> (verified under <paramref name="baseDir"/> when readable).</summary>
    public static List<ProcItem> FindByName(string procName, string? baseDir)
    {
        var result = new List<ProcItem>();
        Process[] procs;
        try { procs = Process.GetProcessesByName(procName); } catch { return result; }
        foreach (var p in procs)
        {
            try
            {
                string? path = null;
                try { path = p.MainModule?.FileName; } catch { /* access denied / bitness mismatch */ }
                if (path != null && !string.IsNullOrEmpty(baseDir)
                    && !path.StartsWith(baseDir, StringComparison.OrdinalIgnoreCase))
                    continue;

                TimeSpan cpu;
                try { cpu = p.TotalProcessorTime; } catch { cpu = TimeSpan.Zero; }
                long mem;
                try { mem = p.WorkingSet64; } catch { mem = 0; }

                result.Add(new ProcItem(p.Id, p.ProcessName, mem, path, cpu, SafeSessionId(p)));
            }
            catch { /* skip */ }
            finally { try { p.Dispose(); } catch { /* ignore */ } }
        }
        return result;
    }

    public static List<ProcItem> Find(CatalogItem item, PathResolver pr)
    {
        var t = ResolveTarget(item, pr);
        if (t == null) return new List<ProcItem>();
        // Use the same dir+name matching as the live process page so detail/card agree
        // (FindByName alone misses dir-only targets like MSIX/Steam).
        var cache = new Dictionary<int, string?>();
        return ScanAll(new List<(string, string, string?)> { (item.Id, t.Value.Proc, t.Value.Dir) }, cache)
            .Select(x => x.Proc).ToList();
    }

    /// <summary>One full process enumeration matched against many targets at once — by install-dir
    /// prefix (catches multi-process apps like Claude / 火绒) and by process name. <paramref name="pathCache"/>
    /// (pid → module path) is reused across ticks so MainModule is queried at most once per process.</summary>
    public static List<(string Id, ProcItem Proc)> ScanAll(
        IReadOnlyList<(string Id, string Proc, string? Dir)> targets,
        Dictionary<int, string?> pathCache)
    {
        var result = new List<(string, ProcItem)>();

        var dirs = targets
            .Where(t => !string.IsNullOrEmpty(t.Dir))
            .Select(t => (t.Id, Dir: t.Dir!.TrimEnd('\\', '/').ToLowerInvariant() + "\\"))
            .ToList();
        var nameTargets = targets
            .Where(t => !string.IsNullOrEmpty(t.Proc))
            .Select(t => (Proc: t.Proc, t.Id))
            .ToList();

        Process[] all;
        try { all = Process.GetProcesses(); } catch { return result; }
        var alive = new HashSet<int>();

        foreach (var p in all)
        {
            try
            {
                alive.Add(p.Id);
                if (!pathCache.TryGetValue(p.Id, out var path))
                {
                    try { path = p.MainModule?.FileName; } catch { path = null; }
                    pathCache[p.Id] = path;
                }

                string? matchId = null;
                if (path != null)
                {
                    var lp = path.ToLowerInvariant();
                    var bestLen = -1;
                    foreach (var d in dirs)
                        if (lp.StartsWith(d.Dir, StringComparison.Ordinal) && d.Dir.Length > bestLen)
                        { matchId = d.Id; bestLen = d.Dir.Length; }
                }
                if (matchId == null)
                {
                    var pn = p.ProcessName;
                    foreach (var nt in nameTargets)
                        // exact, or a sub-process like "WhatsApp.Root" / "Claude.Helper"
                        if (pn.Equals(nt.Proc, StringComparison.OrdinalIgnoreCase) ||
                            pn.StartsWith(nt.Proc + ".", StringComparison.OrdinalIgnoreCase))
                        { matchId = nt.Id; break; }
                }
                if (matchId == null) continue;

                TimeSpan cpu;
                try { cpu = p.TotalProcessorTime; } catch { cpu = TimeSpan.Zero; }
                long mem;
                try { mem = p.WorkingSet64; } catch { mem = 0; }

                result.Add((matchId, new ProcItem(p.Id, p.ProcessName, mem, path, cpu, SafeSessionId(p))));
            }
            catch { /* skip */ }
            finally { try { p.Dispose(); } catch { /* ignore */ } }
        }

        foreach (var pid in pathCache.Keys.ToList())
            if (!alive.Contains(pid)) pathCache.Remove(pid);

        return result;
    }

    /// <summary>Enumerate every running process (pid, name, working set, module path, cpu time) — for the
    /// "all processes" view. <paramref name="pathCache"/> (pid → module path) is reused across ticks so
    /// MainModule is queried at most once per process.</summary>
    public static List<ProcItem> AllProcesses(Dictionary<int, string?> pathCache)
    {
        var result = new List<ProcItem>();
        Process[] all;
        try { all = Process.GetProcesses(); } catch { return result; }
        var alive = new HashSet<int>();
        foreach (var p in all)
        {
            try
            {
                alive.Add(p.Id);
                if (!pathCache.TryGetValue(p.Id, out var path))
                {
                    try { path = p.MainModule?.FileName; } catch { path = null; }
                    pathCache[p.Id] = path;
                }
                TimeSpan cpu;
                try { cpu = p.TotalProcessorTime; } catch { cpu = TimeSpan.Zero; }
                long mem;
                try { mem = p.WorkingSet64; } catch { mem = 0; }
                result.Add(new ProcItem(p.Id, p.ProcessName, mem, path, cpu, SafeSessionId(p)));
            }
            catch { /* skip */ }
            finally { try { p.Dispose(); } catch { /* ignore */ } }
        }
        foreach (var pid in pathCache.Keys.ToList())
            if (!alive.Contains(pid)) pathCache.Remove(pid);
        return result;
    }

    public static bool Kill(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            p.Kill(entireProcessTree: true);
            p.WaitForExit(3000);
            return true;
        }
        catch { return false; }
    }

    /// <summary>Kill every process of the item. Returns how many were killed.</summary>
    public static int KillAll(CatalogItem item, PathResolver pr)
    {
        var n = 0;
        foreach (var p in Find(item, pr))
            if (Kill(p.Pid)) n++;
        return n;
    }
}
