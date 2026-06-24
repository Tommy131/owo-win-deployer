using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using WinDeploy.Core.Util;

namespace WinDeploy.App.Services.Net;

public sealed record ServerProc(int Pid, DateTime? Start);

/// <summary>Live runtime state of one server: its processes and earliest start time (for uptime).</summary>
public sealed class ServerRuntime
{
    public List<ServerProc> Procs { get; init; } = new();
    public bool Running => Procs.Count > 0;
    public string PidText => Procs.Count == 0 ? "—" : string.Join(", ", Procs.Select(p => p.Pid));
    public DateTime? Started
    {
        get
        {
            var starts = Procs.Where(p => p.Start != null).Select(p => p.Start!.Value).ToList();
            return starts.Count > 0 ? starts.Min() : null;
        }
    }
}

public sealed record LogFile(string Name, string Path, long SizeBytes)
{
    public string SizeText => SizeBytes >= 1024 * 1024 ? $"{SizeBytes / 1024.0 / 1024:0.0} MB"
        : SizeBytes >= 1024 ? $"{SizeBytes / 1024.0:0.0} KB" : $"{SizeBytes} B";
}

public sealed record CertFile(string Name, string Path, string Kind, long SizeBytes);

public sealed record VhostSpec(string ServerName, IReadOnlyList<int> Ports, string Root, bool Ssl, string? CertPath, string? KeyPath);

/// <summary>Runtime &amp; lifecycle management for a detected server: process status / uptime, log
/// collection (tail / clear), SSL certificate management (self-signed / import / delete), and per-site
/// vhost create / delete with auto-include into the main config. nginx and Apache get the full feature
/// set; Tomcat gets status + logs (its virtual hosts live in server.xml).</summary>
public static class ServerManager
{
    // ── runtime / process status ─────────────────────────────────────────────
    public static async Task<ServerRuntime> GetRuntimeAsync(ServerInfo s)
    {
        if (string.IsNullOrEmpty(s.ProcName)) return new ServerRuntime();
        // Tomcat runs as java.exe (under JAVA_HOME) — match on command line containing the catalina dir.
        if (s.Id == "tomcat") return await GetJavaRuntimeAsync(s.Dir);
        return await Task.Run(() => GetByName(s.ProcName, s.Dir));
    }

    private static ServerRuntime GetByName(string procName, string? baseDir)
    {
        var procs = new List<ServerProc>();
        Process[] all;
        try { all = Process.GetProcessesByName(procName); } catch { return new ServerRuntime(); }
        foreach (var p in all)
        {
            try
            {
                string? path = null;
                try { path = p.MainModule?.FileName; } catch { /* denied / bitness */ }
                if (path != null && !string.IsNullOrEmpty(baseDir)
                    && !path.StartsWith(baseDir, StringComparison.OrdinalIgnoreCase))
                    continue;
                DateTime? start = null;
                try { start = p.StartTime; } catch { /* denied */ }
                procs.Add(new ServerProc(p.Id, start));
            }
            catch { /* skip */ }
            finally { try { p.Dispose(); } catch { } }
        }
        return new ServerRuntime { Procs = procs };
    }

    private static async Task<ServerRuntime> GetJavaRuntimeAsync(string dir)
    {
        // One CIM query: java processes whose command line references the Tomcat dir.
        // The dir is embedded into a single-quoted PowerShell string, where the ONLY escaping needed
        // is doubling '. Backslashes are literal there — do NOT double them, or the match never hits.
        // Match with a case-insensitive substring (IndexOf), not -like, to avoid wildcard ([ ] * ?) and
        // backslash surprises in the path.
        var esc = dir.Replace("'", "''");
        var ps =
            "[Console]::OutputEncoding=[System.Text.UTF8Encoding]::new($false);" +
            "$d='" + esc + "';" +
            "Get-CimInstance Win32_Process -Filter \"Name='java.exe' OR Name='javaw.exe'\" " +
            "| Where-Object { $_.CommandLine -and $_.CommandLine.IndexOf($d,[System.StringComparison]::OrdinalIgnoreCase) -ge 0 } " +
            "| ForEach-Object { [pscustomobject]@{ Pid=$_.ProcessId; Start=$_.CreationDate.ToString('o') } } | ConvertTo-Json -Compress";
        try
        {
            var r = await Proc.RunAsync("powershell.exe",
                new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-Command", ps });
            var procs = ParseJavaProcs(r.StdOut);
            return new ServerRuntime { Procs = procs };
        }
        catch { return new ServerRuntime(); }
    }

    private static List<ServerProc> ParseJavaProcs(string json)
    {
        var list = new List<ServerProc>();
        json = json.Trim();
        if (string.IsNullOrEmpty(json)) return list;
        try
        {
            using var doc = JsonDocument.Parse(json[0] == '[' ? json : "[" + json + "]");
            foreach (var e in doc.RootElement.EnumerateArray())
            {
                if (!e.TryGetProperty("Pid", out var pidEl)) continue;
                var pid = pidEl.GetInt32();
                DateTime? start = null;
                if (e.TryGetProperty("Start", out var st) && st.ValueKind == JsonValueKind.String
                    && DateTime.TryParse(st.GetString(), out var dt)) start = dt;
                list.Add(new ServerProc(pid, start));
            }
        }
        catch { /* ignore parse */ }
        return list;
    }

    // ── logs ─────────────────────────────────────────────────────────────────
    public static List<LogFile> Logs(ServerInfo s)
    {
        var list = new List<LogFile>();
        try
        {
            if (Directory.Exists(s.LogDir))
                foreach (var f in Directory.GetFiles(s.LogDir, "*.log").Concat(Directory.GetFiles(s.LogDir, "*.txt")).Distinct().OrderBy(f => f))
                {
                    var fi = new FileInfo(f);
                    list.Add(new LogFile(fi.Name, f, fi.Length));
                }
        }
        catch { /* skip */ }
        return list;
    }

    /// <summary>Read up to the last <paramref name="maxLines"/> lines (shared-read; works while the server writes).</summary>
    public static string ReadTail(string path, int maxLines = 600)
    {
        try
        {
            if (!File.Exists(path)) return "（日志文件不存在）";
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var sr = new StreamReader(fs);
            var q = new Queue<string>(maxLines);
            string? line;
            while ((line = sr.ReadLine()) != null)
            {
                if (q.Count == maxLines) q.Dequeue();
                q.Enqueue(line);
            }
            return q.Count == 0 ? "（日志为空）" : string.Join(Environment.NewLine, q);
        }
        catch (Exception ex) { return "读取失败：" + ex.Message; }
    }

    public static (bool Ok, string Msg) ClearLog(string path)
    {
        try
        {
            if (!File.Exists(path)) return (false, "文件不存在");
            File.WriteAllText(path, "");
            AuditLog.Action($"服务配置：清空日志 {path}");
            return (true, "已清空");
        }
        catch (Exception ex) { return (false, ex.Message); }
    }

    // ── SSL certificates ───────────────────────────────────────────────────────
    public static List<CertFile> ListCerts(ServerInfo s)
    {
        var list = new List<CertFile>();
        try
        {
            if (!Directory.Exists(s.SslDir)) return list;
            foreach (var f in Directory.GetFiles(s.SslDir).OrderBy(f => f))
            {
                var ext = Path.GetExtension(f).ToLowerInvariant();
                var kind = ext switch
                {
                    ".crt" or ".cer" or ".pem" => "证书",
                    ".key" => "私钥",
                    ".pfx" or ".p12" => "PFX",
                    _ => "其他",
                };
                list.Add(new CertFile(Path.GetFileName(f), f, kind, new FileInfo(f).Length));
            }
        }
        catch { /* skip */ }
        return list;
    }

    /// <summary>Generate a self-signed cert + key (PEM) for <paramref name="host"/> into the SSL dir.</summary>
    public static (bool Ok, string Msg) CreateSelfSigned(ServerInfo s, string host)
    {
        host = host.Trim();
        if (string.IsNullOrWhiteSpace(host)) return (false, "请填写域名 / 主机名");
        try
        {
            Directory.CreateDirectory(s.SslDir);
            using var rsa = RSA.Create(2048);
            var req = new CertificateRequest($"CN={host}", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            var san = new SubjectAlternativeNameBuilder();
            san.AddDnsName(host);
            if (host != "localhost") san.AddDnsName("localhost");
            req.CertificateExtensions.Add(san.Build());
            req.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
            req.CertificateExtensions.Add(new X509KeyUsageExtension(
                X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, false));
            req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
                new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") }, false)); // server auth

            var now = DateTimeOffset.UtcNow;
            using var cert = req.CreateSelfSigned(now.AddDays(-1), now.AddYears(5));

            var safe = SafeName(host);
            var crtPath = Path.Combine(s.SslDir, safe + ".crt");
            var keyPath = Path.Combine(s.SslDir, safe + ".key");
            File.WriteAllText(crtPath, cert.ExportCertificatePem(), new UTF8Encoding(false));
            File.WriteAllText(keyPath, rsa.ExportPkcs8PrivateKeyPem(), new UTF8Encoding(false));
            AuditLog.Action($"服务配置：生成自签名证书 {host} → {crtPath}");
            return (true, $"已生成自签名证书：{safe}.crt / {safe}.key（有效期 5 年）");
        }
        catch (Exception ex) { return (false, "生成失败：" + ex.Message); }
    }

    public static (bool Ok, string Msg) ImportCert(ServerInfo s, string srcPath)
    {
        try
        {
            if (!File.Exists(srcPath)) return (false, "源文件不存在");
            Directory.CreateDirectory(s.SslDir);
            var dest = Path.Combine(s.SslDir, Path.GetFileName(srcPath));
            File.Copy(srcPath, dest, overwrite: true);
            AuditLog.Action($"服务配置：导入证书 {dest}");
            return (true, "已导入：" + Path.GetFileName(dest));
        }
        catch (Exception ex) { return (false, "导入失败：" + ex.Message); }
    }

    public static (bool Ok, string Msg) DeleteCert(string path)
    {
        try { File.Delete(path); AuditLog.Action($"服务配置：删除证书 {path}"); return (true, "已删除"); }
        catch (Exception ex) { return (false, ex.Message); }
    }

    // ── virtual hosts ─────────────────────────────────────────────────────────
    public static List<ConfigFile> ListVhosts(ServerInfo s)
    {
        var list = new List<ConfigFile>();
        try
        {
            if (Directory.Exists(s.VhostDir))
                foreach (var f in Directory.GetFiles(s.VhostDir, "*.conf").OrderBy(f => f))
                    list.Add(new ConfigFile { Name = Path.GetFileName(f), Path = f });
        }
        catch { /* skip */ }
        return list;
    }

    public static (bool Ok, string Msg) CreateVhost(ServerInfo s, VhostSpec spec)
    {
        if (string.IsNullOrWhiteSpace(spec.ServerName)) return (false, "请填写域名 / server_name");
        if (string.IsNullOrWhiteSpace(spec.Root)) return (false, "请填写站点根目录");
        try
        {
            Directory.CreateDirectory(s.VhostDir);
            var file = Path.Combine(s.VhostDir, SafeName(spec.ServerName) + ".conf");
            var body = s.Id == "apache" ? BuildApacheVhost(spec) : BuildNginxVhost(spec);
            File.WriteAllText(file, body, new UTF8Encoding(false));
            var inc = EnsureInclude(s);
            AuditLog.Action($"服务配置：创建 vhost {spec.ServerName} → {file}");
            return (true, $"已创建 {Path.GetFileName(file)}{inc}。" + (s.CanReload ? "请「重载」生效。" : "请「重启」生效。"));
        }
        catch (Exception ex) { return (false, "创建失败：" + ex.Message); }
    }

    public static (bool Ok, string Msg) DeleteVhost(string path)
    {
        try { File.Delete(path); AuditLog.Action($"服务配置：删除 vhost {path}"); return (true, "已删除，请重载 / 重启生效"); }
        catch (Exception ex) { return (false, ex.Message); }
    }

    /// <summary>Make sure the main config pulls in the vhost dir; returns a note if it had to inject the include.</summary>
    private static string EnsureInclude(ServerInfo s)
    {
        try
        {
            if (!File.Exists(s.MainConf)) return "";
            var text = File.ReadAllText(s.MainConf);
            var fwd = s.VhostDir.Replace("\\", "/");
            if (s.Id == "apache")
            {
                var incLine = $"Include \"{fwd}/*.conf\"";
                if (text.Contains(fwd, StringComparison.OrdinalIgnoreCase)) return "";
                Backup(s.MainConf);
                File.AppendAllText(s.MainConf, Environment.NewLine + "# WinDeploy vhosts" + Environment.NewLine + incLine + Environment.NewLine);
                return "（已写入 httpd.conf 的 Include）";
            }
            else // nginx
            {
                if (text.Contains(fwd, StringComparison.OrdinalIgnoreCase)) return "";
                var idx = text.IndexOf("http", StringComparison.Ordinal);
                var brace = idx >= 0 ? text.IndexOf('{', idx) : -1;
                if (brace < 0) return "（请手动在 http 块内加入 include vhosts）";
                Backup(s.MainConf);
                var insert = $"{Environment.NewLine}    include \"{fwd}/*.conf\";{Environment.NewLine}";
                text = text.Insert(brace + 1, insert);
                File.WriteAllText(s.MainConf, text, new UTF8Encoding(false));
                return "（已写入 nginx.conf 的 include）";
            }
        }
        catch { return "（include 自动写入失败，请手动添加）"; }
    }

    private static string BuildNginxVhost(VhostSpec v)
    {
        var root = v.Root.Replace("\\", "/").TrimEnd('/');
        var ssl = v.Ssl && !string.IsNullOrEmpty(v.CertPath) && !string.IsNullOrEmpty(v.KeyPath);
        var sb = new StringBuilder();
        sb.AppendLine($"# {v.ServerName} — generated by WinDeploy");
        sb.AppendLine("server {");
        foreach (var port in v.Ports)                                 // one listen line per port
            sb.AppendLine(ssl ? $"    listen       {port} ssl;" : $"    listen       {port};");
        sb.AppendLine($"    server_name  {v.ServerName};");
        if (ssl)
        {
            sb.AppendLine($"    ssl_certificate      \"{v.CertPath!.Replace("\\", "/")}\";");
            sb.AppendLine($"    ssl_certificate_key  \"{v.KeyPath!.Replace("\\", "/")}\";");
        }
        sb.AppendLine($"    root   \"{root}\";");
        sb.AppendLine("    index  index.html index.htm;");
        sb.AppendLine();
        sb.AppendLine("    location / {");
        sb.AppendLine("        try_files $uri $uri/ =404;");
        sb.AppendLine("    }");
        sb.AppendLine("}");
        return sb.ToString();
    }

    private static string BuildApacheVhost(VhostSpec v)
    {
        var root = v.Root.Replace("\\", "/").TrimEnd('/');
        var addrs = string.Join(" ", v.Ports.Select(p => $"*:{p}"));  // one VirtualHost bound to all ports
        var sb = new StringBuilder();
        sb.AppendLine($"# {v.ServerName} — generated by WinDeploy");
        sb.AppendLine("# 注意：非默认端口需在 httpd.conf 中确保存在对应的 Listen 指令（此处不自动写入，避免与主配置的 Listen 重复绑定导致启动失败）。");
        sb.AppendLine($"<VirtualHost {addrs}>");
        sb.AppendLine($"    ServerName {v.ServerName}");
        sb.AppendLine($"    DocumentRoot \"{root}\"");
        if (v.Ssl && !string.IsNullOrEmpty(v.CertPath) && !string.IsNullOrEmpty(v.KeyPath))
        {
            sb.AppendLine("    SSLEngine on");
            sb.AppendLine($"    SSLCertificateFile \"{v.CertPath.Replace("\\", "/")}\"");
            sb.AppendLine($"    SSLCertificateKeyFile \"{v.KeyPath.Replace("\\", "/")}\"");
        }
        sb.AppendLine($"    <Directory \"{root}\">");
        sb.AppendLine("        Options Indexes FollowSymLinks");
        sb.AppendLine("        AllowOverride All");
        sb.AppendLine("        Require all granted");
        sb.AppendLine("    </Directory>");
        sb.AppendLine("</VirtualHost>");
        return sb.ToString();
    }

    private static void Backup(string path)
    {
        try { if (File.Exists(path)) File.Copy(path, $"{path}.bak.{DateTime.Now:yyyyMMddHHmmss}", true); } catch { }
    }

    private static string SafeName(string s)
    {
        foreach (var c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
        return s.Replace(':', '_').Replace('*', '_');
    }
}
