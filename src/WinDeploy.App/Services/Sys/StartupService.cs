using System.IO;
using Microsoft.Win32;
using WinDeploy.Core.I18n;

namespace WinDeploy.App.Services.Sys;

public enum StartupKind { Registry, Folder }

/// <summary>A Windows startup item (Run-key value or Startup-folder shortcut).</summary>
public sealed class StartupEntry
{
    public string Name { get; init; } = "";
    public string Command { get; init; } = "";
    public string Source { get; init; } = "";
    public bool Enabled { get; set; }
    public bool NeedsAdmin { get; init; }
    public StartupKind Kind { get; init; }

    // toggle / remove internals
    internal RegistryKey RunRoot { get; init; } = Registry.CurrentUser;
    internal string RunPath { get; init; } = "";
    internal RegistryKey ApprovedRoot { get; init; } = Registry.CurrentUser;
    internal string ApprovedPath { get; init; } = "";
    internal string ApprovedValue { get; init; } = "";
    internal string? FilePath { get; init; }

    /// <summary>The executable referenced by the command (for icon / open-location), best effort.
    /// Handles quoted paths, unquoted paths that contain spaces (e.g. "D:\Tools\CC Switch\cc-switch.exe"),
    /// and %ENV% expansion.</summary>
    public string? ExePath
    {
        get
        {
            if (Kind == StartupKind.Folder) return FilePath;
            var c = Environment.ExpandEnvironmentVariables(Command.Trim());
            if (c.Length == 0) return null;

            if (c.StartsWith("\""))
            {
                var end = c.IndexOf('"', 1);
                return end > 1 ? c[1..end] : c.Trim('"');
            }

            // Unquoted command: the path itself may contain spaces — cut at an executable extension
            // boundary and verify the file exists before trusting it.
            foreach (var ext in new[] { ".exe", ".com", ".bat", ".cmd", ".scr" })
            {
                var idx = c.IndexOf(ext, StringComparison.OrdinalIgnoreCase);
                if (idx > 0)
                {
                    var cand = c[..(idx + ext.Length)];
                    if (File.Exists(cand)) return cand;
                }
            }
            if (File.Exists(c)) return c;

            var sp = c.IndexOf(' ');
            return sp > 0 ? c[..sp] : c;
        }
    }
}

/// <summary>Reads and toggles Windows startup items the same way Task Manager does: enumerate the Run
/// keys + Startup folders, and enable/disable via the StartupApproved binary flag (non-destructive).
/// HKLM / common-folder items need admin — those operations report a friendly error instead of crashing.</summary>
public static class StartupService
{
    private const string Run = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string Run32 = @"Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Run";
    private const string AppRun = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    private const string AppRun32 = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run32";
    private const string AppFolder = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\StartupFolder";

    public static List<StartupEntry> List()
    {
        var list = new List<StartupEntry>();

        AddRegistry(Registry.CurrentUser, Run, Registry.CurrentUser, AppRun, Localizer.T("startup.source.hkcuReg"), false, list);
        AddRegistry(Registry.LocalMachine, Run, Registry.LocalMachine, AppRun, Localizer.T("startup.source.hklmReg"), true, list);
        AddRegistry(Registry.LocalMachine, Run32, Registry.LocalMachine, AppRun32, Localizer.T("startup.source.hklmReg32"), true, list);

        AddFolder(Environment.GetFolderPath(Environment.SpecialFolder.Startup),
            Registry.CurrentUser, Localizer.T("startup.source.userFolder"), false, list);
        AddFolder(Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup),
            Registry.LocalMachine, Localizer.T("startup.source.commonFolder"), true, list);

        return list;
    }

    private static void AddRegistry(RegistryKey runRoot, string runPath, RegistryKey appRoot, string appPath,
        string source, bool admin, List<StartupEntry> list)
    {
        try
        {
            using var run = runRoot.OpenSubKey(runPath, false);
            if (run == null) return;
            foreach (var name in run.GetValueNames())
            {
                if (string.IsNullOrWhiteSpace(name)) continue;
                list.Add(new StartupEntry
                {
                    Name = name,
                    Command = run.GetValue(name)?.ToString() ?? "",
                    Source = source,
                    Enabled = ReadApproved(appRoot, appPath, name),
                    NeedsAdmin = admin,
                    Kind = StartupKind.Registry,
                    RunRoot = runRoot, RunPath = runPath,
                    ApprovedRoot = appRoot, ApprovedPath = appPath, ApprovedValue = name,
                });
            }
        }
        catch { /* skip unreadable hive */ }
    }

    private static void AddFolder(string dir, RegistryKey appRoot, string source, bool admin, List<StartupEntry> list)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) return;
            foreach (var file in Directory.EnumerateFiles(dir))
            {
                var name = Path.GetFileName(file);
                if (name.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)) continue;
                list.Add(new StartupEntry
                {
                    Name = Path.GetFileNameWithoutExtension(file),
                    Command = file,
                    Source = source,
                    Enabled = ReadApproved(appRoot, AppFolder, name),
                    NeedsAdmin = admin,
                    Kind = StartupKind.Folder,
                    ApprovedRoot = appRoot, ApprovedPath = AppFolder, ApprovedValue = name,
                    FilePath = file,
                });
            }
        }
        catch { /* skip */ }
    }

    private static bool ReadApproved(RegistryKey root, string path, string valueName)
    {
        try
        {
            using var k = root.OpenSubKey(path, false);
            if (k?.GetValue(valueName) is byte[] b && b.Length > 0) return (b[0] & 1) == 0;
        }
        catch { /* default enabled */ }
        return true;
    }

    public static (bool Ok, string Msg) SetEnabled(StartupEntry e, bool enabled)
    {
        try
        {
            using var k = e.ApprovedRoot.CreateSubKey(e.ApprovedPath, true);
            var bytes = new byte[12];
            bytes[0] = (byte)(enabled ? 0x02 : 0x03);
            if (!enabled)
            {
                var ft = BitConverter.GetBytes(DateTime.Now.ToFileTimeUtc());
                Array.Copy(ft, 0, bytes, 4, 8);
            }
            k!.SetValue(e.ApprovedValue, bytes, RegistryValueKind.Binary);
            e.Enabled = enabled;
            return (true, "");
        }
        catch (UnauthorizedAccessException) { return (false, Localizer.T("startup.err.needAdmin")); }
        catch (System.Security.SecurityException) { return (false, Localizer.T("startup.err.needAdmin")); }
        catch (Exception ex) { return (false, ex.Message); }
    }

    public static (bool Ok, string Msg) Remove(StartupEntry e)
    {
        try
        {
            if (e.Kind == StartupKind.Registry)
            {
                using var run = e.RunRoot.OpenSubKey(e.RunPath, true);
                run?.DeleteValue(e.Name, false);
            }
            else if (e.FilePath != null && File.Exists(e.FilePath))
            {
                File.Delete(e.FilePath);
            }
            try { using var k = e.ApprovedRoot.OpenSubKey(e.ApprovedPath, true); k?.DeleteValue(e.ApprovedValue, false); }
            catch { /* approved cleanup best effort */ }
            return (true, "");
        }
        catch (UnauthorizedAccessException) { return (false, Localizer.T("startup.err.needAdmin")); }
        catch (System.Security.SecurityException) { return (false, Localizer.T("startup.err.needAdmin")); }
        catch (Exception ex) { return (false, ex.Message); }
    }
}
