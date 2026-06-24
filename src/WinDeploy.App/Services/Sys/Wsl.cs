using System.ComponentModel;
using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using WinDeploy.Core.I18n;
using WinDeploy.Core.Util;

namespace WinDeploy.App.Services.Sys;

public sealed record WslDistro(string Name, string State, int Version, bool Default);
public sealed record WslOnline(string Name, string Friendly);

/// <summary>Thin wrapper over wsl.exe for the WSL management page: list installed/available distros, set
/// default, terminate, shut down, export (backup) and unregister. Capture goes through `cmd` with
/// WSL_UTF8=1 because wsl.exe otherwise emits UTF-16; long/interactive actions open their own window.</summary>
public static class Wsl
{
    public static bool IsAvailable() => CommandFinder.Find("wsl") != null;

    /// <summary>Whether the "适用于 Linux 的 Windows 子系统" optional feature is actually enabled. wsl.exe
    /// ships as a stub even when the feature is OFF, so we check for the registered WSL service instead
    /// (LxssManager, or WslService on newer Store WSL). Registry read of HKLM\…\Services needs no admin.</summary>
    public static bool IsFeatureEnabled()
    {
        foreach (var svc in new[] { "WslService", "LxssManager" })
        {
            try
            {
                using var k = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\" + svc, false);
                if (k != null) return true;
            }
            catch { /* unreadable → treat as not present */ }
        }
        return false;
    }

    /// <summary>Whether the Virtual Machine Platform feature (required by WSL2) is enabled.</summary>
    public static bool IsVmPlatformEnabled()
    {
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\vmcompute", false);
            return k != null;
        }
        catch { return false; }
    }

    /// <summary>Open the classic "启用或关闭 Windows 功能" dialog so the user can tick the WSL feature.</summary>
    public static (bool Ok, string Msg) OpenWindowsFeatures()
    {
        try
        {
            Process.Start(new ProcessStartInfo("OptionalFeatures.exe") { UseShellExecute = true });
            return (true, Localizer.T("wsl.msg.featureOpened"));
        }
        catch (Exception ex) { return (false, ex.Message); }
    }

    /// <summary>Convenience one-click enable (needs admin + reboot): runs `wsl --install --no-distribution`,
    /// which turns on the WSL + 虚拟机平台 features without installing a distro.</summary>
    public static (bool Ok, string Msg) EnableFeatureVisible()
        => LaunchVisible("--install --no-distribution", elevate: true);

    private static async Task<string> CaptureAsync(string wslArgs, CancellationToken ct)
    {
        // WSL_UTF8=1 makes wsl.exe emit UTF-8 so Proc's UTF-8 reader doesn't garble the table.
        var r = await Proc.RunAsync("cmd", new[] { "/c", $"set WSL_UTF8=1 && wsl {wslArgs}" }, ct: ct);
        return r.StdOut;
    }

    public static async Task<List<WslDistro>> ListAsync(CancellationToken ct = default)
    {
        var list = new List<WslDistro>();
        var outp = await CaptureAsync("--list --verbose", ct);
        foreach (var raw in outp.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (string.IsNullOrWhiteSpace(line)) continue;
            var isDefault = line.TrimStart().StartsWith('*');
            var body = line.Replace('*', ' ').Trim();
            if (body.StartsWith("NAME", StringComparison.OrdinalIgnoreCase)) continue;   // header
            var parts = Regex.Split(body, @"\s{2,}|\t+").Where(p => p.Length > 0).ToArray();
            if (parts.Length == 0) continue;
            var name = parts[0].Trim();
            var state = parts.Length > 1 ? parts[1].Trim() : "";
            var ver = parts.Length > 2 && int.TryParse(parts[2].Trim(), out var v) ? v : 0;
            if (name.Length > 0) list.Add(new WslDistro(name, state, ver, isDefault));
        }
        return list;
    }

    public static async Task<List<WslOnline>> ListOnlineAsync(CancellationToken ct = default)
    {
        var list = new List<WslOnline>();
        var outp = await CaptureAsync("--list --online", ct);
        var started = false;
        foreach (var raw in outp.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (string.IsNullOrWhiteSpace(line)) continue;
            if (line.TrimStart().StartsWith("NAME", StringComparison.OrdinalIgnoreCase)) { started = true; continue; }
            if (!started) continue;   // skip the preamble lines
            var parts = Regex.Split(line.Trim(), @"\s{2,}|\t+").Where(p => p.Length > 0).ToArray();
            if (parts.Length == 0) continue;
            list.Add(new WslOnline(parts[0].Trim(), parts.Length > 1 ? parts[1].Trim() : parts[0].Trim()));
        }
        return list;
    }

    public static async Task<(bool Ok, string Msg)> RunAsync(string args, CancellationToken ct = default)
    {
        try
        {
            var r = await Proc.RunAsync("cmd", new[] { "/c", $"set WSL_UTF8=1 && wsl {args}" }, ct: ct);
            return r.Ok ? (true, "完成") : (false, $"wsl 退出码 {r.ExitCode}");
        }
        catch (Exception ex) { return (false, ex.Message); }
    }

    public static Task<(bool Ok, string Msg)> SetDefaultAsync(string name) => RunAsync($"--set-default \"{name}\"");
    public static Task<(bool Ok, string Msg)> TerminateAsync(string name) => RunAsync($"--terminate \"{name}\"");
    public static Task<(bool Ok, string Msg)> ShutdownAsync() => RunAsync("--shutdown");
    public static Task<(bool Ok, string Msg)> UnregisterAsync(string name) => RunAsync($"--unregister \"{name}\"");

    public static async Task<(bool Ok, string Msg)> ExportAsync(string name, string tarPath, CancellationToken ct = default)
    {
        try
        {
            var r = await Proc.RunAsync("wsl", new[] { "--export", name, tarPath }, ct: ct);
            return r.Ok ? (true, Localizer.Format("wsl.msg.exported", tarPath)) : (false, Localizer.Format("wsl.msg.exportFailed", r.ExitCode));
        }
        catch (Exception ex) { return (false, ex.Message); }
    }

    /// <summary>Install a distro in a visible window (first install enables the WSL feature and may prompt UAC).</summary>
    public static (bool Ok, string Msg) InstallVisible(string name)
        => LaunchVisible($"--install -d {name}", elevate: true);

    /// <summary>Open a distro's shell in a new console window.</summary>
    public static (bool Ok, string Msg) LaunchVisible(string args, bool elevate = false)
    {
        try
        {
            var psi = new ProcessStartInfo("cmd.exe") { Arguments = $"/k wsl {args}", UseShellExecute = true };
            if (elevate) psi.Verb = "runas";
            Process.Start(psi);
            return (true, Localizer.T("wsl.msg.ranInNewWindow"));
        }
        catch (Win32Exception) { return (false, Localizer.T("svc.run.cancelled")); }
        catch (Exception ex) { return (false, ex.Message); }
    }
}
