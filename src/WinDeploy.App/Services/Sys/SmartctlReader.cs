using System.IO;
using WinDeploy.Core.Util;

namespace WinDeploy.App.Services.Sys;

/// <summary>Locates the bundled <c>smartctl.exe</c> (smartmontools) and runs it for one physical drive, returning
/// its JSON output. Used as the SMART backend for drives behind a USB / FireWire / SCSI bridge (external
/// enclosures), where smartctl's per-chipset NVMe tunneling (ASMedia / JMicron / Realtek auto-detected) reads
/// SMART that Windows' generic IOCTLs can't. smartctl opens the raw disk, so it needs administrator rights.</summary>
public static class SmartctlReader
{
    private static string? _cached;
    private static bool _searched;

    /// <summary>Full path to smartctl.exe, or null if not found. Looked up next to the app (bundled
    /// <c>tools\smartctl.exe</c>), up the tree to the repo's <c>tools\</c> (dev), a smartmontools install, then PATH.</summary>
    public static string? FindExe()
    {
        if (_searched) return _cached;
        _searched = true;

        var baseDir = AppContext.BaseDirectory;
        var candidates = new List<string>
        {
            Path.Combine(baseDir, "tools", "smartctl.exe"),
            Path.Combine(baseDir, "smartctl.exe"),
        };
        var dir = new DirectoryInfo(baseDir);
        for (var i = 0; i < 8 && dir != null; i++, dir = dir.Parent)
            candidates.Add(Path.Combine(dir.FullName, "tools", "smartctl.exe"));
        try { candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "smartmontools", "bin", "smartctl.exe")); }
        catch { /* ignore */ }

        foreach (var c in candidates)
            try { if (File.Exists(c)) { _cached = c; return c; } } catch { /* skip */ }

        _cached = CommandFinder.Find("smartctl");   // last resort: PATH
        return _cached;
    }

    public static bool Available => FindExe() != null;

    /// <summary>Run smartctl for the given physical-drive index and return its JSON stdout (null if smartctl
    /// isn't available or the index is out of range). The USB-bridge type is auto-detected.</summary>
    public static async Task<string?> RunAsync(int physicalDriveIndex, CancellationToken ct = default)
    {
        if (physicalDriveIndex < 0 || physicalDriveIndex > 25) return null;
        var exe = FindExe();
        if (exe == null) return null;
        var dev = "/dev/sd" + (char)('a' + physicalDriveIndex);
        try
        {
            // Hard timeout so a quirky bridge can never hang the dialog (Proc kills the process tree on cancel).
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(15));
            // Read ONLY what we display: -H health, -i identify/model, -A SMART attributes (incl. the NVMe
            // health log), -l devstat the Device Statistics log (host read/write totals for SSDs that omit ATA
            // attrs 0xF1/0xF2). Deliberately NOT "-a"/"-x": those also read the Error Information Log, which on
            // some USB bridges (e.g. UGREEN / Realtek) fails slowly (IOCTL Error 1117) and takes ~60 s — past our
            // timeout — leaving the dialog empty. smartctl exits non-zero for benign conditions yet still prints
            // valid JSON, so we parse stdout regardless of exit code.
            var r = await Proc.RunAsync(exe, new[] { "-j", "-H", "-i", "-A", "-l", "devstat", dev }, ct: cts.Token);
            return string.IsNullOrWhiteSpace(r.StdOut) ? null : r.StdOut;
        }
        catch { return null; }
    }
}
