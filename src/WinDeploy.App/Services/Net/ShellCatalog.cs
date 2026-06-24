using System.IO;
using System.Windows.Media;
using Microsoft.Win32;
using WinDeploy.Core.I18n;

namespace WinDeploy.App.Services.Net;

/// <summary>A command shell that can back a terminal session: a stable id, a display name, the command line
/// to launch under ConPTY, the resolved executable path (for its real icon), and a short glyph fallback.</summary>
public sealed record ShellInfo(string Id, string Name, string CommandLine, string Glyph, string Exe);

/// <summary>Discovers the command shells actually installed on this machine (Windows PowerShell, cmd,
/// PowerShell 7, Git Bash, WSL …) so the terminal page can offer each as a new-session choice. Detection is
/// best-effort and file/registry based — no shells are launched just to probe them.</summary>
public static class ShellCatalog
{
    /// <summary>The installed shells, in a sensible default order (PowerShell first).</summary>
    public static IReadOnlyList<ShellInfo> Detect()
    {
        var list = new List<ShellInfo>();
        var system = Environment.GetFolderPath(Environment.SpecialFolder.System);

        // Windows PowerShell — present on every supported Windows build.
        var winPs = Path.Combine(system, "WindowsPowerShell", "v1.0", "powershell.exe");
        if (File.Exists(winPs))
            list.Add(new("powershell", "Windows PowerShell", $"\"{winPs}\" -NoLogo", "PS", winPs));

        // PowerShell 7+ (pwsh) — optional cross-platform install.
        var pwsh = FindFile("pwsh.exe", new[]
        {
            Expand(@"%ProgramFiles%\PowerShell\7\pwsh.exe"),
            Expand(@"%ProgramFiles%\PowerShell\7-preview\pwsh.exe"),
            Expand(@"%LocalAppData%\Microsoft\PowerShell\7\pwsh.exe"),
        });
        if (pwsh != null)
            list.Add(new("pwsh", "PowerShell 7", $"\"{pwsh}\" -NoLogo", "7", pwsh));

        // cmd — always present.
        var cmd = Path.Combine(system, "cmd.exe");
        var cmdExe = File.Exists(cmd) ? cmd : "cmd.exe";
        list.Add(new("cmd", Localizer.T("svc.shell.cmd"), $"\"{cmdExe}\"", ">_", cmdExe));

        // Git Bash — the bundled bash.exe, run as a login interactive shell.
        var bash = FindFile("bash.exe", new[]
        {
            Expand(@"%ProgramFiles%\Git\bin\bash.exe"),
            Expand(@"%ProgramFiles(x86)%\Git\bin\bash.exe"),
            Expand(@"%LocalAppData%\Programs\Git\bin\bash.exe"),
            GitBashFromRegistry(),
        });
        if (bash != null)
            list.Add(new("gitbash", "Git Bash", $"\"{bash}\" --login -i", "git", GitExeFor(bash)));

        // WSL — only when at least one distro is registered.
        var wsl = Path.Combine(system, "wsl.exe");
        if (File.Exists(wsl) && WslHasDistro())
            list.Add(new("wsl", "WSL", $"\"{wsl}\"", "wsl", wsl));

        return list;
    }

    private static readonly Dictionary<string, ImageSource?> _iconCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The shell's real executable icon (cached per exe path), for the session picker.</summary>
    public static ImageSource? IconFor(ShellInfo shell)
    {
        var exe = shell.Exe;
        if (string.IsNullOrEmpty(exe)) return null;
        if (_iconCache.TryGetValue(exe, out var img)) return img;
        img = IconExtractor.FromExeAnyIcon(exe);
        _iconCache[exe] = img;
        return img;
    }

    /// <summary>Git's bin\bash.exe carries no brand icon — prefer the Git Bash launcher (git-bash.exe) at the
    /// install root for a recognizable icon, falling back to bash.exe.</summary>
    private static string GitExeFor(string bash)
    {
        try
        {
            var root = Path.GetDirectoryName(Path.GetDirectoryName(bash));   // …\Git\bin\bash.exe → …\Git
            if (root != null)
            {
                var gitBash = Path.Combine(root, "git-bash.exe");
                if (File.Exists(gitBash)) return gitBash;
            }
        }
        catch { /* fall back */ }
        return bash;
    }

    private static string Expand(string p) => Environment.ExpandEnvironmentVariables(p);

    /// <summary>First existing candidate path, else the first hit for <paramref name="exeName"/> on PATH.</summary>
    private static string? FindFile(string exeName, IEnumerable<string?> candidates)
    {
        foreach (var c in candidates)
        {
            try { if (!string.IsNullOrEmpty(c) && File.Exists(c)) return c; } catch { /* bad path */ }
        }
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in path.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try { var p = Path.Combine(Expand(dir), exeName); if (File.Exists(p)) return p; } catch { /* skip */ }
        }
        return null;
    }

    /// <summary>Git's install dir from the registry → its bin\bash.exe (covers non-default install roots).</summary>
    private static string? GitBashFromRegistry()
    {
        foreach (var (hive, view) in new[]
                 {
                     (RegistryHive.LocalMachine, RegistryView.Registry64),
                     (RegistryHive.LocalMachine, RegistryView.Registry32),
                     (RegistryHive.CurrentUser, RegistryView.Default),
                 })
        {
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                using var key = baseKey.OpenSubKey(@"SOFTWARE\GitForWindows");
                if (key?.GetValue("InstallPath") is string root && !string.IsNullOrWhiteSpace(root))
                {
                    var p = Path.Combine(root, "bin", "bash.exe");
                    if (File.Exists(p)) return p;
                }
            }
            catch { /* not installed / no access */ }
        }
        return null;
    }

    /// <summary>True when at least one WSL distro is registered (HKCU\…\Lxss has a distro subkey).</summary>
    private static bool WslHasDistro()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Lxss");
            return key != null && key.GetSubKeyNames().Length > 0;
        }
        catch { return false; }
    }
}
