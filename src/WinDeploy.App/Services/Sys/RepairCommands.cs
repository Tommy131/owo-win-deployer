using System.ComponentModel;
using System.Diagnostics;
using WinDeploy.Core.I18n;

namespace WinDeploy.App.Services.Sys;

/// <summary>One built-in Windows repair/maintenance command. Most require elevation; they run in their own
/// console window (cmd /k) so the user sees live output and the window stays open with the result.
/// <see cref="Title"/> / <see cref="Detail"/> resolve through the localizer at access time (keyed by
/// <see cref="Id"/>), so they follow the active UI language.</summary>
public sealed record RepairAction(string Id, string Command, bool Elevate, bool Risky = false)
{
    public string Title => Localizer.T($"maint.repair.item.{Id}.title");

    /// <summary>Localized one-line description; falls back to the raw command for entries (e.g. gpupdate)
    /// whose detail is just the command itself.</summary>
    public string Detail
    {
        get
        {
            var key = $"maint.repair.item.{Id}.detail";
            return Localizer.Has(key) ? Localizer.T(key) : Command;
        }
    }
}

/// <summary>The repair technician's toolbox: SFC / DISM / chkdsk / network reset / Windows Update cache /
/// icon cache rebuild — the commands every Windows repair starts with, one click each.</summary>
public static class RepairCommands
{
    public static readonly IReadOnlyList<RepairAction> All = new[]
    {
        new RepairAction("sfc", "sfc /scannow", true),
        new RepairAction("dism", "DISM /Online /Cleanup-Image /RestoreHealth", true),
        new RepairAction("dism-clean", "DISM /Online /Cleanup-Image /StartComponentCleanup", true),
        new RepairAction("chkdsk", "chkdsk C:", true),
        new RepairAction("net-reset", "ipconfig /flushdns & netsh winsock reset & netsh int ip reset", true),
        new RepairAction("flushdns", "ipconfig /flushdns", false),
        new RepairAction("wu-cache",
            "net stop wuauserv & net stop bits & rd /s /q %windir%\\SoftwareDistribution\\Download & net start wuauserv & net start bits", true),
        new RepairAction("icon-cache",
            "ie4uinit.exe -show & taskkill /f /im explorer.exe & del /a /q \"%localappdata%\\IconCache.db\" & del /a /q \"%localappdata%\\Microsoft\\Windows\\Explorer\\iconcache*\" & start explorer.exe", true, Risky: true),
        new RepairAction("gpupdate", "gpupdate /force", true),
    };

    /// <summary>Launch an action in its own (optionally elevated) cmd window. Returns a friendly status.</summary>
    public static (bool Ok, string Msg) Run(RepairAction a)
    {
        try
        {
            var psi = new ProcessStartInfo("cmd.exe")
            {
                // /k keeps the window open after the command finishes so the user can read the result.
                Arguments = $"/k \"{a.Command} & echo. & echo ===== {Localizer.T("maint.repair.cmdDone")} =====\"",
                UseShellExecute = true,
            };
            if (a.Elevate) psi.Verb = "runas";
            Process.Start(psi);
            AuditLog.Action($"系统维护：{a.Title}");
            return (true, Localizer.T("maint.repair.ran"));
        }
        catch (Win32Exception) { return (false, Localizer.T("maint.repair.cancelled")); }
        catch (Exception ex) { return (false, ex.Message); }
    }
}
