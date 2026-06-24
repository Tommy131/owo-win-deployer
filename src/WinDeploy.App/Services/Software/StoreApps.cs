using System.Diagnostics;
using System.IO;
using System.Text;
using WinDeploy.Core.I18n;

namespace WinDeploy.App.Services.Software;

/// <summary>Resolves and launches apps via <c>Get-StartApps</c> (covers both MSIX apps like WhatsApp —
/// AppID "PFN!App" — and Win32 apps whose ARP entry lacks a path, like ScreenToGif — AppID is a
/// known-folder path). Used as a launch fallback and (MSIX only) for installed-detection.</summary>
public static class StoreApps
{
    private static List<(string Name, string AppId)>? _apps;

    public static IReadOnlyList<(string Name, string AppId)> All()
    {
        if (_apps != null) return _apps;
        var list = new List<(string, string)>();
        try
        {
            var psi = new ProcessStartInfo("powershell.exe")
            {
                Arguments = "-NoProfile -ExecutionPolicy Bypass -Command " +
                            "\"Get-StartApps | ForEach-Object { $_.Name + [char]9 + $_.AppID }\"",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
            };
            using var p = Process.Start(psi)!;
            var output = p.StandardOutput.ReadToEnd();
            p.WaitForExit(8000);
            foreach (var raw in output.Split('\n'))
            {
                var line = raw.Trim();
                var tab = line.IndexOf('\t');
                if (tab <= 0) continue;
                list.Add((line[..tab].Trim(), line[(tab + 1)..].Trim()));
            }
        }
        catch { /* StartApps unavailable */ }
        _apps = list;
        return _apps;
    }

    /// <summary>AppID of a Start-menu app whose name matches <paramref name="appName"/> (exact or
    /// alphanumeric-normalized equality — avoids loose mismatches like "Go" → "Google").</summary>
    public static string? FindAppId(string appName, Action<string>? log = null)
    {
        var apps = All();
        log?.Invoke(Localizer.Format("store.entries", apps.Count));
        var norm = Normalize(appName);
        if (norm.Length == 0) return null;
        var hit = apps.FirstOrDefault(a =>
            a.Name.Equals(appName, StringComparison.OrdinalIgnoreCase) || Normalize(a.Name) == norm);
        return hit.AppId;
    }

    /// <summary>True if an MSIX/Store app (AppID contains '!') matches the name — for installed-detection.</summary>
    public static bool HasMsixApp(string appName)
    {
        var norm = Normalize(appName);
        if (norm.Length == 0) return false;
        return All().Any(a => a.AppId.Contains('!') &&
            (a.Name.Equals(appName, StringComparison.OrdinalIgnoreCase) || Normalize(a.Name) == norm));
    }

    public static bool Launch(string appId, Action<string>? log = null)
    {
        try
        {
            // Win32 Start entry: AppID is "{KnownFolderGUID}\rel\path.exe" → launch the resolved exe.
            var exe = ResolveWin32(appId);
            if (exe != null)
            {
                log?.Invoke("AppsFolder(Win32) → " + exe);
                Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(exe)! });
                return true;
            }
            // MSIX / other: activate via the apps folder.
            Process.Start(new ProcessStartInfo("explorer.exe", $"shell:AppsFolder\\{appId}") { UseShellExecute = true });
            return true;
        }
        catch (Exception ex) { log?.Invoke(Localizer.Format("store.launchFail", ex.Message)); return false; }
    }

    private static readonly Dictionary<string, Environment.SpecialFolder> KnownFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        ["{6D809377-6AF0-444B-8957-A3773F02200E}"] = Environment.SpecialFolder.ProgramFiles,      // ProgramFilesX64
        ["{7C5A40EF-A0FB-4BFC-874A-C0F2E0B9FA8E}"] = Environment.SpecialFolder.ProgramFilesX86,
        ["{905E63B6-C1BF-494E-B29C-65B732D3D21A}"] = Environment.SpecialFolder.ProgramFiles,
        ["{F38BF404-1D43-42F2-9305-67DE0B28FC23}"] = Environment.SpecialFolder.Windows,
        ["{F1B32785-6FBA-4FCF-9D55-7B8E7F157091}"] = Environment.SpecialFolder.LocalApplicationData,
        ["{3EB685DB-65F9-4CF6-A03A-E3EF65729F3D}"] = Environment.SpecialFolder.ApplicationData,
    };

    private static string? ResolveWin32(string appId)
    {
        if (appId.Contains('!') || !appId.StartsWith("{")) return null;
        var brace = appId.IndexOf('}');
        if (brace <= 0 || brace + 2 >= appId.Length) return null;
        var guid = appId[..(brace + 1)];
        var rel = appId[(brace + 1)..].TrimStart('\\', '/');
        if (!KnownFolders.TryGetValue(guid, out var folder)) return null;
        var full = Path.Combine(Environment.GetFolderPath(folder), rel);
        return File.Exists(full) ? full : null;
    }

    private static string Normalize(string s)
    {
        var sb = new StringBuilder();
        foreach (var c in s) if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
        return sb.ToString();
    }
}
