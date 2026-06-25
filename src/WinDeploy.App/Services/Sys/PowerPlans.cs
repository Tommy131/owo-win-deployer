using System.Diagnostics;
using System.Text.RegularExpressions;
using WinDeploy.Core.I18n;

namespace WinDeploy.App.Services.Sys;

/// <summary>One Windows power scheme (电源计划): its GUID, friendly name (as reported by powercfg, may be
/// empty / non-ASCII), and whether it is the currently active scheme.</summary>
public sealed record PowerPlan(string Guid, string Name, bool Active);

/// <summary>
/// Thin wrapper over <c>powercfg</c> to read and switch Windows power schemes — the 节能 / 平衡 / 高性能 /
/// 卓越性能 (Power saver / Balanced / High performance / Ultimate performance) scheduling policies.
///
/// All operations run in the current user's context and need <b>no elevation</b> (<c>/setactive</c>,
/// <c>/list</c>, <c>/getactivescheme</c>, <c>-duplicatescheme</c>). Only ASCII tokens (GUIDs and the active
/// <c>*</c> marker) are parsed from powercfg's output, so console code-page differences never corrupt the
/// result; built-in scheme names are localized by the view-model from the GUID instead.
/// </summary>
public static class PowerPlans
{
    // Well-known built-in scheme GUIDs (stable across Windows versions). Ultimate Performance is a hidden
    // template that must be registered with -duplicatescheme before it appears / can be activated.
    public const string PowerSaver = "a1841308-3541-4fab-bc81-f71556f20b4a";
    public const string Balanced = "381b4222-f694-41f0-9685-ff5bb260df2e";
    public const string HighPerformance = "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c";
    public const string UltimatePerformance = "e9a42b02-d5df-448d-aa00-03f14749eb61";

    private static readonly Regex GuidRe = new(
        @"[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}",
        RegexOptions.Compiled);

    /// <summary>Localization key for a built-in scheme's display name, or null for custom user schemes
    /// (whose name comes from powercfg instead).</summary>
    public static string? KnownKey(string guid) => guid.ToLowerInvariant() switch
    {
        PowerSaver => "power.preset.saver.title",
        Balanced => "power.preset.balanced.title",
        HighPerformance => "power.preset.high.title",
        UltimatePerformance => "power.preset.ultimate.title",
        _ => null,
    };

    /// <summary>All power schemes registered on the machine, with the active one flagged.</summary>
    public static List<PowerPlan> List()
    {
        var (_, output) = Run("/list");
        var plans = new List<PowerPlan>();
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.Trim();
            var m = GuidRe.Match(line);
            if (!m.Success) continue;
            var active = line.EndsWith("*", StringComparison.Ordinal);

            // Friendly name sits in parentheses after the GUID: "... GUID: <guid>  (Balanced) *".
            var name = "";
            var open = line.IndexOf('(', m.Index + m.Length);
            var close = line.LastIndexOf(')');
            if (open >= 0 && close > open) name = line.Substring(open + 1, close - open - 1).Trim();

            plans.Add(new PowerPlan(m.Value.ToLowerInvariant(), name, active));
        }
        return plans;
    }

    /// <summary>GUID of the currently active scheme, or null if it can't be determined.</summary>
    public static string? ActiveGuid()
    {
        var (_, output) = Run("/getactivescheme");
        var m = GuidRe.Match(output);
        return m.Success ? m.Value.ToLowerInvariant() : null;
    }

    /// <summary>Make the given scheme active (current user; no elevation needed).</summary>
    public static (bool Ok, string Msg) SetActive(string guid)
    {
        var (code, output) = Run($"/setactive {guid}");
        return code == 0 ? (true, "") : (false, Friendly(output));
    }

    /// <summary>Register the hidden Ultimate Performance scheme on this machine. Returns the GUID to activate
    /// (the freshly-duplicated scheme's GUID, falling back to the template GUID).</summary>
    public static (bool Ok, string GuidOrMsg) UnlockUltimate()
    {
        var (code, output) = Run($"-duplicatescheme {UltimatePerformance}");
        if (code != 0) return (false, Friendly(output));
        var m = GuidRe.Match(output);
        return (true, m.Success ? m.Value.ToLowerInvariant() : UltimatePerformance);
    }

    private static string Friendly(string output)
    {
        var t = output.Trim();
        return string.IsNullOrEmpty(t) ? Localizer.T("power.err.generic") : t;
    }

    private static (int Code, string Output) Run(string args)
    {
        try
        {
            var psi = new ProcessStartInfo("powercfg.exe", args)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var p = Process.Start(psi);
            if (p == null) return (-1, "");
            var so = p.StandardOutput.ReadToEnd();
            var se = p.StandardError.ReadToEnd();
            if (!p.WaitForExit(5000)) { try { p.Kill(); } catch { /* ignore */ } return (-1, so + se); }
            return (p.ExitCode, so + se);
        }
        catch (Exception ex) { return (-1, ex.Message); }
    }
}
