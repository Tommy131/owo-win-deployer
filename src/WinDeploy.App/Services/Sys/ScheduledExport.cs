using WinDeploy.Core.Util;

namespace WinDeploy.App.Services.Sys;

/// <summary>Registers a Windows Task Scheduler job that periodically runs this app headlessly
/// (<c>WinDeploy.exe --capture &lt;repoRoot&gt;</c>) to capture the machine's configs into the repo. Driven via
/// PowerShell's ScheduledTasks cmdlets (robust quoting for paths with spaces, and UTF-8 output so error text
/// isn't mangled by the console's OEM codepage). Runs as the current user — no elevation needed.</summary>
public static class ScheduledExport
{
    public const string TaskName = "OwOWinDeployer-Capture";

    /// <summary>Schedule frequency presets exposed in the UI.</summary>
    public enum Frequency { Daily, Weekly, OnLogon }

    // Force the child PowerShell to emit UTF-8 so Proc (which reads as UTF-8) doesn't mojibake localized errors.
    private const string Utf8 = "[Console]::OutputEncoding=[System.Text.UTF8Encoding]::new($false);";

    /// <summary>True when the capture task currently exists.</summary>
    public static async Task<bool> IsRegisteredAsync()
    {
        try
        {
            var r = await Run($"{Utf8} if (Get-ScheduledTask -TaskName '{TaskName}' -ErrorAction SilentlyContinue) {{ 'yes' }} else {{ 'no' }}");
            return r.Ok && r.StdOut.Contains("yes");
        }
        catch { return false; }
    }

    /// <summary>Create or replace the capture task. Daily/Weekly run at 12:00; OnLogon runs at sign-in.</summary>
    public static async Task<(bool Ok, string Message)> RegisterAsync(string exePath, string repoRoot, Frequency freq)
    {
        var trigger = freq switch
        {
            Frequency.Weekly => "New-ScheduledTaskTrigger -Weekly -DaysOfWeek Monday -At 12:00",
            Frequency.OnLogon => "New-ScheduledTaskTrigger -AtLogOn",
            _ => "New-ScheduledTaskTrigger -Daily -At 12:00",
        };
        var script =
            $"{Utf8} $ErrorActionPreference='Stop'; try {{ " +
            $"$a = New-ScheduledTaskAction -Execute '{Q(exePath)}' -Argument '--capture \"{repoRoot.Replace("'", "''")}\"'; " +
            $"$t = {trigger}; " +
            $"Register-ScheduledTask -TaskName '{TaskName}' -Action $a -Trigger $t -Force | Out-Null; 'OK' " +
            $"}} catch {{ Write-Error $_.Exception.Message; exit 1 }}";
        return await RunChecked(script);
    }

    /// <summary>Remove the capture task (no-op if it doesn't exist).</summary>
    public static async Task<(bool Ok, string Message)> UnregisterAsync()
        => await RunChecked($"{Utf8} Unregister-ScheduledTask -TaskName '{TaskName}' -Confirm:$false -ErrorAction SilentlyContinue");

    private static async Task<(bool, string)> RunChecked(string script)
    {
        try
        {
            var r = await Run(script);
            return r.Ok ? (true, "") : (false, (r.StdErr + r.StdOut).Trim());
        }
        catch (Exception ex) { return (false, ex.Message); }
    }

    private static Task<ProcResult> Run(string script)
        => Proc.RunAsync("powershell", new[] { "-NoProfile", "-NonInteractive", "-Command", script });

    /// <summary>Escape a value for embedding inside a single-quoted PowerShell string.</summary>
    private static string Q(string s) => s.Replace("'", "''");
}
