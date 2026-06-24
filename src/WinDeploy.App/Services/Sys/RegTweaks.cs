using Microsoft.Win32;
using WinDeploy.Core.I18n;

namespace WinDeploy.App.Services.Sys;

public enum TweakKind { Dword, ClassicMenu }

/// <summary>A reversible Windows tweak. Most are a single HKCU DWORD (On vs Off value); the Win11 classic
/// right-click menu is a create/delete-key special case.</summary>
public sealed class RegTweak
{
    public string Id { get; init; } = "";
    public string Title { get; init; } = "";
    public string Detail { get; init; } = "";
    public TweakKind Kind { get; init; } = TweakKind.Dword;
    public RegistryHive Hive { get; init; } = RegistryHive.CurrentUser;
    public string Path { get; init; } = "";
    public string Value { get; init; } = "";
    public int On { get; init; }
    public int Off { get; init; }
    public bool NeedsAdmin { get; init; }
    public bool RestartExplorer { get; init; }

    /// <summary>Minimum Windows build this tweak applies to (0 = any). Tweaks above the running build are
    /// hidden — e.g. the Win11 classic menu / clock-seconds tweaks don't show on Windows 10.</summary>
    public int MinBuild { get; init; }
}

/// <summary>Curated, reversible system tweaks (Explorer / appearance / privacy) that sysadmins apply on
/// every fresh machine — each with a one-click on/off and live current-state read. HKCU tweaks need no
/// elevation; the telemetry tweak writes HKLM (run as admin).</summary>
public static class RegTweaks
{
    private const string Adv = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced";
    private const string Personalize = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
    private const string ClassicClsid = @"Software\Classes\CLSID\{86ca1aa0-34aa-4e8b-a509-50c905bae2a2}";

    public static readonly IReadOnlyList<RegTweak> All = new[]
    {
        // Title / Detail are localization keys (tweaks.item.<id>.title|detail), resolved in TweakRowViewModel.
        new RegTweak { Id = "fileext", Title = "tweaks.item.fileext.title", Detail = "tweaks.item.fileext.detail",
            Path = Adv, Value = "HideFileExt", On = 0, Off = 1, RestartExplorer = true },
        new RegTweak { Id = "hidden", Title = "tweaks.item.hidden.title", Detail = "tweaks.item.hidden.detail",
            Path = Adv, Value = "Hidden", On = 1, Off = 2, RestartExplorer = true },
        new RegTweak { Id = "darkapps", Title = "tweaks.item.darkapps.title", Detail = "tweaks.item.darkapps.detail",
            Path = Personalize, Value = "AppsUseLightTheme", On = 0, Off = 1, MinBuild = OsInfo.Win10_1809 },
        new RegTweak { Id = "darksys", Title = "tweaks.item.darksys.title", Detail = "tweaks.item.darksys.detail",
            Path = Personalize, Value = "SystemUsesLightTheme", On = 0, Off = 1, MinBuild = OsInfo.Win10_1809 },
        new RegTweak { Id = "thispc", Title = "tweaks.item.thispc.title", Detail = "tweaks.item.thispc.detail",
            Path = Adv, Value = "LaunchTo", On = 1, Off = 2 },
        new RegTweak { Id = "clockseconds", Title = "tweaks.item.clockseconds.title", Detail = "tweaks.item.clockseconds.detail",
            Path = Adv, Value = "ShowSecondsInSystemClock", On = 1, Off = 0, RestartExplorer = true, MinBuild = OsInfo.Win11_22H2 },
        new RegTweak { Id = "classicmenu", Title = "tweaks.item.classicmenu.title", Detail = "tweaks.item.classicmenu.detail",
            Kind = TweakKind.ClassicMenu, Path = ClassicClsid, RestartExplorer = true, MinBuild = OsInfo.Win11_21H2 },
        new RegTweak { Id = "telemetry", Title = "tweaks.item.telemetry.title", Detail = "tweaks.item.telemetry.detail",
            Hive = RegistryHive.LocalMachine, Path = @"SOFTWARE\Policies\Microsoft\Windows\DataCollection",
            Value = "AllowTelemetry", On = 0, Off = 1, NeedsAdmin = true },
    };

    private static RegistryKey Root(RegTweak t) => t.Hive == RegistryHive.LocalMachine ? Registry.LocalMachine : Registry.CurrentUser;

    public static bool? IsOn(RegTweak t)
    {
        try
        {
            if (t.Kind == TweakKind.ClassicMenu)
            {
                using var k = Registry.CurrentUser.OpenSubKey(t.Path + @"\InprocServer32", false);
                return k != null;
            }
            using var key = Root(t).OpenSubKey(t.Path, false);
            var v = key?.GetValue(t.Value);
            if (v is int i) return i == t.On;
            return null;   // unset → unknown (OS default in effect)
        }
        catch { return null; }
    }

    public static (bool Ok, string Msg) Set(RegTweak t, bool on)
    {
        try
        {
            if (t.Kind == TweakKind.ClassicMenu)
            {
                if (on)
                {
                    using var k = Registry.CurrentUser.CreateSubKey(t.Path + @"\InprocServer32", true);
                    k!.SetValue("", "", RegistryValueKind.String);   // empty default = enable classic menu
                }
                else
                {
                    try { Registry.CurrentUser.DeleteSubKeyTree(t.Path, throwOnMissingSubKey: false); } catch { }
                }
                return (true, "");
            }

            using var key = Root(t).CreateSubKey(t.Path, true);
            key!.SetValue(t.Value, on ? t.On : t.Off, RegistryValueKind.DWord);
            return (true, "");
        }
        catch (UnauthorizedAccessException) { return (false, Localizer.T("tweaks.err.needAdmin")); }
        catch (System.Security.SecurityException) { return (false, Localizer.T("tweaks.err.needAdmin")); }
        catch (Exception ex) { return (false, ex.Message); }
    }

    public static void RestartExplorer()
    {
        try
        {
            foreach (var p in System.Diagnostics.Process.GetProcessesByName("explorer"))
                try { p.Kill(); } catch { }
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe") { UseShellExecute = true });
        }
        catch { /* explorer auto-restarts anyway */ }
    }
}
