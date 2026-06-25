using WinDeploy.Core.I18n;
using WinDeploy.Core.Util;

namespace WinDeploy.Core.Engine;

/// <summary>Creates a Windows System Restore point (via PowerShell's <c>Checkpoint-Computer</c>) before a
/// risky batch install, so a bad deployment can be rolled back. Requires admin and System Restore enabled on
/// the system drive; best-effort — the caller decides whether to proceed when it fails.</summary>
public static class RestorePoint
{
    /// <summary>Try to create a restore point. Returns success + a localized message. Note Windows rate-limits
    /// restore points (one per ~24h by default); a throttled call reports success with no new point.</summary>
    public static async Task<(bool Ok, string Message)> CreateAsync(string description, CancellationToken ct = default)
    {
        // -RestorePointType MODIFY_SETTINGS is the right category for "about to change installed software".
        var script = $"Checkpoint-Computer -Description \"{description.Replace("\"", "'")}\" -RestorePointType MODIFY_SETTINGS";
        ProcResult r;
        try
        {
            r = await Proc.RunAsync("powershell",
                new[] { "-NoProfile", "-NonInteractive", "-Command", script }, ct: ct);
        }
        catch (Exception ex) { return (false, ex.Message); }

        if (r.Ok) return (true, Localizer.T("engine.restore.created"));

        // Surface the most useful line of the failure (admin / System Restore disabled / frequency throttle).
        var err = (r.StdErr + r.StdOut).Trim();
        var firstLine = err.Split('\n').FirstOrDefault(l => l.Trim().Length > 0)?.Trim();
        return (false, string.IsNullOrEmpty(firstLine) ? Localizer.T("engine.restore.failed") : firstLine);
    }
}
