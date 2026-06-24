using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using WinDeploy.Core;
using WinDeploy.Core.Engine;
using WinDeploy.Core.I18n;
using WinDeploy.Core.Models;

namespace WinDeploy.App.Services.Net;

public sealed class ConfigFile
{
    public string Name { get; init; } = "";
    public string Path { get; init; } = "";
}

public enum SvcAction { Start, Stop, Reload, Restart }

/// <summary>A detected backend server (php / nginx / apache / tomcat) with its install dir, its editable
/// config files, the process to watch, and which service actions / management features apply.</summary>
public sealed class ServerInfo
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Dir { get; init; } = "";
    public List<ConfigFile> Configs { get; init; } = new();
    public bool CanStart { get; init; }
    public bool CanStop { get; init; }
    public bool CanReload { get; init; }
    public bool CanRestart { get; init; }

    // runtime / management
    public string Exe { get; init; } = "";        // main executable (nginx.exe / bin\httpd.exe / "")
    public string ProcName { get; init; } = "";   // process base name to watch (nginx / httpd / java)
    public string MainConf { get; init; } = "";   // nginx.conf / httpd.conf / server.xml (for include injection)
    public string LogDir { get; init; } = "";     // where *.log live
    public string SslDir { get; init; } = "";     // SSL certificate dir
    public string VhostDir { get; init; } = "";   // per-site config dir
    public bool SupportsVhost { get; init; }
    public bool SupportsSsl { get; init; }

    public bool HasService => CanStart || CanStop || CanReload || CanRestart;
}

/// <summary>Locates installed php/nginx/apache/tomcat, lists their config files, and starts/stops/reloads
/// the service. Install dirs come from the catalog's InstallPathOverride / extractTo / env var.</summary>
public static class ServiceConfig
{
    public static List<ServerInfo> Detect(Catalog cat, PathResolver pr)
    {
        var list = new List<ServerInfo>();
        Add(list, BuildPhp(cat, pr));
        Add(list, BuildNginx(cat, pr));
        Add(list, BuildApache(cat, pr));
        Add(list, BuildTomcat(cat, pr));
        return list;
    }

    private static void Add(List<ServerInfo> list, ServerInfo? s) { if (s != null) list.Add(s); }

    /// <summary>Resolve a catalog item's install dir from override / extractTo / env var; null if not found.</summary>
    private static string? ResolveDir(Catalog cat, PathResolver pr, string id, params string[] markerRel)
    {
        var item = cat.Items.FirstOrDefault(i => i.Id == id);
        if (item == null) return null;
        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(item.InstallPathOverride)) candidates.Add(pr.Resolve(item.InstallPathOverride));
        if (!string.IsNullOrWhiteSpace(item.Install.ExtractTo)) candidates.Add(pr.Resolve(item.Install.ExtractTo));
        if (item.Detect?.EnvVar is { } ev && Detection.EnvVarDir(ev) is { } envDir) candidates.Add(envDir);

        foreach (var baseDir in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            // Accept the base dir, or a one-level subdir (e.g. Apache24) that holds a marker file.
            foreach (var root in new[] { baseDir }.Concat(SafeSubdirs(baseDir)))
                if (markerRel.Any(m => File.Exists(Path.Combine(root, m))))
                    return root;
        }
        return null;
    }

    private static IEnumerable<string> SafeSubdirs(string dir)
    {
        try { return Directory.Exists(dir) ? Directory.GetDirectories(dir) : Enumerable.Empty<string>(); }
        catch { return Enumerable.Empty<string>(); }
    }

    private static ServerInfo? BuildPhp(Catalog cat, PathResolver pr)
    {
        var dir = ResolveDir(cat, pr, "php", "php.exe");
        if (dir == null) return null;
        var cfgs = new List<ConfigFile>();
        foreach (var f in new[] { "php.ini", "php.ini-development", "php.ini-production" })
            if (File.Exists(Path.Combine(dir, f))) cfgs.Add(new ConfigFile { Name = f, Path = Path.Combine(dir, f) });
        return new ServerInfo { Id = "php", Name = "PHP", Dir = dir, Configs = cfgs };
    }

    private static ServerInfo? BuildNginx(Catalog cat, PathResolver pr)
    {
        var dir = ResolveDir(cat, pr, "nginx", "nginx.exe");
        if (dir == null) return null;
        var conf = Path.Combine(dir, "conf");
        return new ServerInfo
        {
            Id = "nginx", Name = "nginx", Dir = dir,
            Configs = ListConfigs(conf, "*.conf"),
            CanStart = true, CanStop = true, CanReload = true, CanRestart = true,
            Exe = Path.Combine(dir, "nginx.exe"), ProcName = "nginx",
            MainConf = Path.Combine(conf, "nginx.conf"),
            LogDir = Path.Combine(dir, "logs"),
            SslDir = Path.Combine(conf, "ssl"),
            VhostDir = Path.Combine(conf, "vhosts"),
            SupportsVhost = true, SupportsSsl = true,
        };
    }

    private static ServerInfo? BuildApache(Catalog cat, PathResolver pr)
    {
        var dir = ResolveDir(cat, pr, "apache", @"bin\httpd.exe");
        if (dir == null) return null;
        var cfgs = ListConfigs(Path.Combine(dir, "conf"), "*.conf");
        cfgs.AddRange(ListConfigs(Path.Combine(dir, "conf", "extra"), "*.conf"));
        return new ServerInfo
        {
            Id = "apache", Name = "Apache HTTP Server", Dir = dir, Configs = cfgs,
            CanStart = true, CanStop = true, CanRestart = true,
            Exe = Path.Combine(dir, "bin", "httpd.exe"), ProcName = "httpd",
            MainConf = Path.Combine(dir, "conf", "httpd.conf"),
            LogDir = Path.Combine(dir, "logs"),
            SslDir = Path.Combine(dir, "conf", "ssl"),
            VhostDir = Path.Combine(dir, "conf", "extra", "vhosts"),
            SupportsVhost = true, SupportsSsl = true,
        };
    }

    private static ServerInfo? BuildTomcat(Catalog cat, PathResolver pr)
    {
        var dir = ResolveDir(cat, pr, "tomcat", @"bin\catalina.bat");
        if (dir == null) return null;
        var cfgs = new List<ConfigFile>();
        foreach (var f in new[] { "server.xml", "web.xml", "context.xml" })
            if (File.Exists(Path.Combine(dir, "conf", f))) cfgs.Add(new ConfigFile { Name = f, Path = Path.Combine(dir, "conf", f) });
        return new ServerInfo
        {
            Id = "tomcat", Name = "Apache Tomcat", Dir = dir, Configs = cfgs,
            CanStart = true, CanStop = true,
            Exe = Path.Combine(dir, "bin", "catalina.bat"), ProcName = "java",
            MainConf = Path.Combine(dir, "conf", "server.xml"),
            LogDir = Path.Combine(dir, "logs"),
        };
    }

    private static List<ConfigFile> ListConfigs(string dir, string pattern)
    {
        var list = new List<ConfigFile>();
        try
        {
            if (Directory.Exists(dir))
                foreach (var f in Directory.GetFiles(dir, pattern).OrderBy(f => f))
                    list.Add(new ConfigFile { Name = Path.GetFileName(f), Path = f });
        }
        catch { /* skip */ }
        return list;
    }

    /// <summary>Run a service action. Returns (ok, message). Long/console actions open their own window.</summary>
    public static (bool Ok, string Msg) Run(ServerInfo s, SvcAction action)
    {
        try
        {
            switch (s.Id)
            {
                case "nginx":
                    return action switch
                    {
                        SvcAction.Start => Launch(Path.Combine(s.Dir, "nginx.exe"), "", s.Dir, false),
                        SvcAction.Stop => RunWait(Path.Combine(s.Dir, "nginx.exe"), "-s stop", s.Dir),
                        SvcAction.Reload => RunWait(Path.Combine(s.Dir, "nginx.exe"), "-s reload", s.Dir),
                        SvcAction.Restart => Restart(() => RunWait(Path.Combine(s.Dir, "nginx.exe"), "-s stop", s.Dir),
                                                     () => Launch(Path.Combine(s.Dir, "nginx.exe"), "", s.Dir, false)),
                        _ => (false, Localizer.T("svc.run.notSupported")),
                    };
                case "apache":
                    var httpd = Path.Combine(s.Dir, "bin", "httpd.exe");
                    return action switch
                    {
                        SvcAction.Start => Launch("cmd.exe", $"/k \"\"{httpd}\"\"", s.Dir, true),
                        SvcAction.Stop => Kill("httpd"),
                        SvcAction.Restart => Restart(() => Kill("httpd"), () => Launch("cmd.exe", $"/k \"\"{httpd}\"\"", s.Dir, true)),
                        _ => (false, Localizer.T("svc.run.notSupported")),
                    };
                case "tomcat":
                    return action switch
                    {
                        SvcAction.Start => Launch(Path.Combine(s.Dir, "bin", "startup.bat"), "", s.Dir, true),
                        SvcAction.Stop => Launch(Path.Combine(s.Dir, "bin", "shutdown.bat"), "", s.Dir, true),
                        _ => (false, Localizer.T("svc.run.notSupported")),
                    };
                default:
                    return (false, Localizer.T("svc.run.noActions"));
            }
        }
        catch (Win32Exception) { return (false, Localizer.T("svc.run.cancelled")); }
        catch (Exception ex) { return (false, ex.Message); }
    }

    private static (bool, string) Restart(Func<(bool, string)> stop, Func<(bool, string)> start)
    {
        stop();
        System.Threading.Thread.Sleep(800);
        return start();
    }

    private static (bool, string) Launch(string exe, string args, string wd, bool visible)
    {
        if (!File.Exists(exe) && !exe.Equals("cmd.exe", StringComparison.OrdinalIgnoreCase))
            return (false, Localizer.Format("svc.run.notFound", exe));
        var psi = new ProcessStartInfo(exe) { Arguments = args, WorkingDirectory = wd, UseShellExecute = visible, CreateNoWindow = !visible };
        Process.Start(psi);
        return (true, Localizer.T("svc.run.started"));
    }

    private static (bool, string) RunWait(string exe, string args, string wd)
    {
        if (!File.Exists(exe)) return (false, Localizer.Format("svc.run.notFound", exe));
        var psi = new ProcessStartInfo(exe) { Arguments = args, WorkingDirectory = wd, UseShellExecute = false, CreateNoWindow = true };
        using var p = Process.Start(psi);
        p?.WaitForExit(8000);
        return (p is { ExitCode: 0 }) ? (true, Localizer.T("svc.run.done")) : (true, Localizer.T("svc.run.sent"));
    }

    private static (bool, string) Kill(string procName)
    {
        var n = 0;
        foreach (var p in Process.GetProcessesByName(procName))
            try { p.Kill(entireProcessTree: true); n++; } catch { } finally { p.Dispose(); }
        return (true, n > 0 ? Localizer.Format("svc.run.killed", n) : Localizer.T("svc.run.noProcess"));
    }
}
