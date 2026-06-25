using WinDeploy.Core.Util;

namespace WinDeploy.App.Services.Sys;

/// <summary>Registers a Windows Task Scheduler job that periodically runs this app headlessly
/// (<c>WinDeploy.exe --capture &lt;repoRoot&gt;</c>) to capture the machine's configs into the repo. Runs as the
/// current user (no elevation needed); the single source of truth is the scheduled task itself.</summary>
public static class ScheduledExport
{
    public const string TaskName = "OwOWinDeployer-Capture";

    /// <summary>Schedule frequency presets exposed in the UI.</summary>
    public enum Frequency { Daily, Weekly, OnLogon }

    /// <summary>True when the capture task currently exists.</summary>
    public static async Task<bool> IsRegisteredAsync()
    {
        try
        {
            var r = await Proc.RunAsync("schtasks", new[] { "/query", "/tn", TaskName });
            return r.ExitCode == 0;
        }
        catch { return false; }
    }

    /// <summary>Create or replace the capture task. Daily/Weekly run at 12:00; OnLogon runs at sign-in.</summary>
    public static async Task<(bool Ok, string Message)> RegisterAsync(string exePath, string repoRoot, Frequency freq)
    {
        var tr = $"\"{exePath}\" --capture \"{repoRoot}\"";
        var args = new List<string> { "/create", "/tn", TaskName, "/tr", tr, "/f" };
        switch (freq)
        {
            case Frequency.Weekly: args.AddRange(new[] { "/sc", "WEEKLY", "/d", "MON", "/st", "12:00" }); break;
            case Frequency.OnLogon: args.AddRange(new[] { "/sc", "ONLOGON" }); break;
            default: args.AddRange(new[] { "/sc", "DAILY", "/st", "12:00" }); break;
        }
        return await RunAsync(args);
    }

    /// <summary>Remove the capture task (no-op if it doesn't exist).</summary>
    public static async Task<(bool Ok, string Message)> UnregisterAsync()
        => await RunAsync(new[] { "/delete", "/tn", TaskName, "/f" });

    private static async Task<(bool, string)> RunAsync(IEnumerable<string> args)
    {
        try
        {
            var r = await Proc.RunAsync("schtasks", args);
            return r.ExitCode == 0 ? (true, "") : (false, (r.StdErr + r.StdOut).Trim());
        }
        catch (Exception ex) { return (false, ex.Message); }
    }
}
