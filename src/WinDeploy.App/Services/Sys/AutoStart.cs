using Microsoft.Win32;

namespace WinDeploy.App.Services.Sys;

/// <summary>Manages the app's "launch at Windows sign-in" entry under the current user's Run key
/// (HKCU\…\CurrentVersion\Run). HKCU is always writable by the user, so this needs no elevation. The
/// registry value is the single source of truth — read it back rather than trusting a cached flag.</summary>
public static class AutoStart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "OwOWinDeployer";

    /// <summary>True when a Run entry for this app exists and still points at an executable path.</summary>
    public static bool IsEnabled()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(RunKey, false);
            return k?.GetValue(ValueName) is string s && !string.IsNullOrWhiteSpace(s);
        }
        catch { return false; }
    }

    /// <summary>Add (or remove) the auto-start Run entry, pointing at the current executable.</summary>
    public static (bool Ok, string Msg) Set(bool enabled)
    {
        try
        {
            using var k = Registry.CurrentUser.CreateSubKey(RunKey, true);
            if (k == null) return (false, "");
            if (enabled)
            {
                var exe = Environment.ProcessPath;
                if (string.IsNullOrEmpty(exe)) return (false, "");
                k.SetValue(ValueName, $"\"{exe}\"", RegistryValueKind.String);
            }
            else
            {
                k.DeleteValue(ValueName, throwOnMissingValue: false);
            }
            return (true, "");
        }
        catch (Exception ex) { return (false, ex.Message); }
    }
}
