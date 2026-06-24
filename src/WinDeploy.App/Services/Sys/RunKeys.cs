using System.IO;
using Microsoft.Win32;

namespace WinDeploy.App.Services.Sys;

/// <summary>Locates an app's real .exe from the Windows "Run" startup registry keys — the only reliable
/// signal for a portable app installed to a custom folder the catalog doesn't know about (e.g. cc-switch
/// at D:\Tools\CC Switch). Matching is by normalised name/id to avoid false hits on short tokens.</summary>
public static class RunKeys
{
    private static readonly (RegistryHive Hive, string Path)[] Roots =
    {
        (RegistryHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Run"),
        (RegistryHive.LocalMachine, @"Software\Microsoft\Windows\CurrentVersion\Run"),
        (RegistryHive.LocalMachine, @"Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Run"),
    };

    /// <summary>The exe registered under a Run key whose value name matches the item, or null.</summary>
    public static string? FindExe(string name, string id)
    {
        var nName = Normalize(name);
        var nId = Normalize(id);
        if (nName.Length < 3 && nId.Length < 3) return null;

        foreach (var (hive, path) in Roots)
        {
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Default);
                using var key = baseKey.OpenSubKey(path);
                if (key == null) continue;
                foreach (var valName in key.GetValueNames())
                {
                    var nk = Normalize(valName);
                    if (!Matches(nk, nName) && !Matches(nk, nId)) continue;
                    if (key.GetValue(valName) is not string cmd) continue;
                    var exe = ExePathFromCommand(cmd);
                    if (exe != null && File.Exists(exe)) return exe;
                }
            }
            catch { /* registry access denied / missing */ }
        }
        return null;
    }

    private static bool Matches(string a, string b)
        => b.Length >= 3 && (a == b || a.Contains(b) || b.Contains(a));

    /// <summary>"\"C:\App\app.exe\" --flag" or "C:\Tools\CC Switch\cc-switch.exe -m" → the exe path,
    /// handling unquoted paths that contain spaces.</summary>
    private static string? ExePathFromCommand(string command)
    {
        var c = Environment.ExpandEnvironmentVariables(command.Trim());
        if (c.Length == 0) return null;
        if (c[0] == '"')
        {
            var end = c.IndexOf('"', 1);
            if (end > 1) return c.Substring(1, end - 1);
        }
        foreach (var ext in new[] { ".exe", ".com", ".bat", ".cmd" })
        {
            var i = c.IndexOf(ext, StringComparison.OrdinalIgnoreCase);
            if (i > 0)
            {
                var cand = c.Substring(0, i + ext.Length);
                if (File.Exists(cand)) return cand;
            }
        }
        return File.Exists(c) ? c : null;
    }

    private static string Normalize(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var sb = new System.Text.StringBuilder(s.Length);
        foreach (var ch in s) if (char.IsLetterOrDigit(ch)) sb.Append(char.ToLowerInvariant(ch));
        return sb.ToString();
    }
}
