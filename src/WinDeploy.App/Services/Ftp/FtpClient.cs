using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;

namespace WinDeploy.App.Services.Ftp;

/// <summary>One remote directory entry parsed from MLSD / LIST.</summary>
public sealed record FtpRemoteEntry(string Name, bool IsDir, long Size, DateTime? Modified, string Perm = "");

/// <summary>A minimal, zero-dependency FTP / FTPS client used by the 客户端 tab to browse a remote server and
/// transfer files. Supports plain FTP, explicit FTPS (AUTH TLS) and implicit FTPS, passive data transfers,
/// and MLSD with a LIST fallback. Self-signed server certs are accepted (ad-hoc usage).</summary>
public sealed class FtpClient : IDisposable
{
    private TcpClient? _tcp;
    private Stream _ctrl = Stream.Null;
    private string _host = "";
    private bool _secure;
    private readonly byte[] _rbuf = new byte[4096];
    private int _rpos, _rlen;

    public string CurrentDir { get; private set; } = "/";
    public bool Connected => _tcp?.Connected == true;
    public event Action<string>? Log;

    public async Task ConnectAsync(string host, int port, string tlsMode, string user, string pass, CancellationToken ct)
    {
        _host = host;
        _tcp = new TcpClient { NoDelay = true };
        using (var to = LinkTimeout(ct)) await _tcp.ConnectAsync(host, port, to.Token);
        _ctrl = _tcp.GetStream();

        var implicitTls = string.Equals(tlsMode, "implicit", StringComparison.OrdinalIgnoreCase);
        var explicitTls = string.Equals(tlsMode, "explicit", StringComparison.OrdinalIgnoreCase);

        if (implicitTls) await UpgradeTlsAsync(ct);

        var greet = await ReadResponseAsync(ct);
        Expect(greet, 220);

        if (explicitTls)
        {
            var auth = await SendAsync("AUTH TLS", ct);
            if (auth.Code is 234 or 334) await UpgradeTlsAsync(ct);
            else throw new IOException("服务器不支持 AUTH TLS：" + auth.Text);
        }

        if (_secure)
        {
            await SendAsync("PBSZ 0", ct);
            await SendAsync("PROT P", ct);
        }

        var u = await SendAsync("USER " + user, ct);
        if (u.Code == 331)
        {
            var p = await SendAsync("PASS " + pass, ct, mask: true);
            Expect(p, 230);
        }
        else if (u.Code != 230) throw new IOException("登录失败：" + u.Text);

        await SendAsync("TYPE I", ct);
        var pwd = await SendAsync("PWD", ct);
        CurrentDir = ParsePwd(pwd.Text) ?? "/";
        Log?.Invoke($"已连接 {host}:{port} · {(_secure ? "TLS 加密" : "明文")} · 目录 {CurrentDir}");
    }

    private async Task UpgradeTlsAsync(CancellationToken ct)
    {
        var ssl = new SslStream(_ctrl, leaveInnerStreamOpen: false,
            (_, _, _, _) => true);   // accept self-signed — ad-hoc personal usage
        using (var to = LinkTimeout(ct))
            await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = _host,
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
            }, to.Token);
        _ctrl = ssl;
        _secure = true;
        _rpos = _rlen = 0;
    }

    // ── browsing ───────────────────────────────────────────────────────────────
    public async Task<List<FtpRemoteEntry>> ListAsync(CancellationToken ct)
    {
        // Prefer MLSD; fall back to LIST when unsupported.
        var conn = await ConnectDataAsync(ct);
        try
        {
            var r = await SendAsync("MLSD", ct);
            if (r.Code is 150 or 125)
            {
                var data = await SecureDataAsync(conn, ct);   // TLS handshake AFTER the 150 (server starts its side then)
                var text = await ReadAllAsync(data, ct);
                Close(data, conn);
                await ReadResponseAsync(ct);   // 226
                return ParseMlsd(text);
            }
            conn.Dispose();
        }
        catch { conn.Dispose(); }

        // LIST fallback
        var conn2 = await ConnectDataAsync(ct);
        var lr = await SendAsync("LIST", ct);
        if (lr.Code is not (150 or 125)) { conn2.Dispose(); throw new IOException("LIST 失败：" + lr.Text); }
        var data2 = await SecureDataAsync(conn2, ct);
        var raw = await ReadAllAsync(data2, ct);
        Close(data2, conn2);
        await ReadResponseAsync(ct);   // 226
        return ParseList(raw);
    }

    public async Task ChangeDirAsync(string path, CancellationToken ct)
    {
        var r = await SendAsync("CWD " + path, ct);
        Expect(r, 250);
        var pwd = await SendAsync("PWD", ct);
        CurrentDir = ParsePwd(pwd.Text) ?? CurrentDir;
    }

    public Task UpAsync(CancellationToken ct) => ChangeDirAsync("..", ct);

    public async Task MakeDirAsync(string name, CancellationToken ct) => Expect(await SendAsync("MKD " + name, ct), 257);
    public async Task RemoveDirAsync(string name, CancellationToken ct) => Expect(await SendAsync("RMD " + name, ct), 250);
    public async Task DeleteAsync(string name, CancellationToken ct) => Expect(await SendAsync("DELE " + name, ct), 250);

    public async Task RenameAsync(string from, string to, CancellationToken ct)
    {
        Expect(await SendAsync("RNFR " + from, ct), 350);
        Expect(await SendAsync("RNTO " + to, ct), 250);
    }

    // ── transfers ──────────────────────────────────────────────────────────────
    public async Task DownloadAsync(string remoteName, string localPath, IProgress<long>? progress, CancellationToken ct)
    {
        var conn = await ConnectDataAsync(ct);
        try
        {
            var r = await SendAsync("RETR " + remoteName, ct);
            if (r.Code is not (150 or 125)) throw new IOException("下载失败：" + r.Text);
            var data = await SecureDataAsync(conn, ct);
            await using (var fs = new FileStream(localPath, FileMode.Create, FileAccess.Write, FileShare.None))
                await PumpAsync(data, fs, progress, ct);
            Close(data, conn);
            Expect(await ReadResponseAsync(ct), 226);
        }
        catch { conn.Dispose(); throw; }
    }

    public async Task UploadAsync(string localPath, string remoteName, IProgress<long>? progress, CancellationToken ct)
    {
        var conn = await ConnectDataAsync(ct);
        try
        {
            var r = await SendAsync("STOR " + remoteName, ct);
            if (r.Code is not (150 or 125)) throw new IOException("上传失败：" + r.Text);
            var data = await SecureDataAsync(conn, ct);
            await using (var fs = new FileStream(localPath, FileMode.Open, FileAccess.Read, FileShare.Read))
                await PumpAsync(fs, data, progress, ct);
            // Signal EOF gracefully so the server reads a clean end (TLS close_notify / TCP FIN), not a reset.
            if (data is SslStream ssl) { try { await ssl.ShutdownAsync(); } catch { } }
            else { try { conn.Client.Shutdown(SocketShutdown.Send); } catch { } }
            // Read the server's 226 BEFORE tearing down the data socket, so the close never races the read.
            Expect(await ReadResponseAsync(ct), 226);
            Close(data, conn);
        }
        catch { conn.Dispose(); throw; }
    }

    // ── directory (recursive) transfers ─────────────────────────────────────────
    /// <summary>Recursively download remote directory <paramref name="remoteName"/> (under the current dir)
    /// into <paramref name="localParent"/>/&lt;remoteName&gt;. The working directory is restored afterwards.
    /// <paramref name="onFile"/> reports each file name as its transfer starts; <paramref name="bytes"/>
    /// reports the delta bytes of each chunk (for overall speed/progress).</summary>
    public async Task DownloadDirectoryAsync(string remoteName, string localParent, IProgress<string>? onFile, IProgress<long>? bytes, CancellationToken ct)
    {
        var start = CurrentDir;
        try { await DownloadDirRecAsync(remoteName, localParent, onFile, bytes, ct); }
        finally { try { await ChangeDirAsync(start, ct); } catch { } }
    }

    private async Task DownloadDirRecAsync(string remoteName, string localParent, IProgress<string>? onFile, IProgress<long>? bytes, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var localDir = Path.Combine(localParent, SafeLocal(remoteName));
        Directory.CreateDirectory(localDir);
        await ChangeDirAsync(remoteName, ct);
        var entries = await ListAsync(ct);
        foreach (var e in entries)
        {
            ct.ThrowIfCancellationRequested();
            if (e.IsDir) await DownloadDirRecAsync(e.Name, localDir, onFile, bytes, ct);
            else { onFile?.Report(e.Name); await DownloadAsync(e.Name, Path.Combine(localDir, SafeLocal(e.Name)), bytes, ct); }
        }
        await ChangeDirAsync("..", ct);
    }

    /// <summary>Recursively upload local directory <paramref name="localDir"/> into a remote directory named
    /// <paramref name="remoteName"/> (created under the current dir). The working directory is restored after.</summary>
    public async Task UploadDirectoryAsync(string localDir, string remoteName, IProgress<string>? onFile, IProgress<long>? bytes, CancellationToken ct)
    {
        var start = CurrentDir;
        try { await UploadDirRecAsync(localDir, remoteName, onFile, bytes, ct); }
        finally { try { await ChangeDirAsync(start, ct); } catch { } }
    }

    private async Task UploadDirRecAsync(string localDir, string remoteName, IProgress<string>? onFile, IProgress<long>? bytes, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        try { await MakeDirAsync(remoteName, ct); } catch { /* may already exist */ }
        await ChangeDirAsync(remoteName, ct);
        foreach (var sub in Directory.GetDirectories(localDir))
            await UploadDirRecAsync(sub, Path.GetFileName(sub), onFile, bytes, ct);
        foreach (var file in Directory.GetFiles(localDir))
        {
            ct.ThrowIfCancellationRequested();
            onFile?.Report(Path.GetFileName(file));
            await UploadAsync(file, Path.GetFileName(file), bytes, ct);
        }
        await ChangeDirAsync("..", ct);
    }

    /// <summary>Sum the byte size of a remote directory tree (recursive), restoring the working directory.
    /// Used to compute the ETA total before a folder download.</summary>
    public async Task<long> RemoteDirSizeAsync(string remoteName, CancellationToken ct)
    {
        var start = CurrentDir;
        try { return await RemoteDirSizeRecAsync(remoteName, ct); }
        finally { try { await ChangeDirAsync(start, ct); } catch { } }
    }

    private async Task<long> RemoteDirSizeRecAsync(string remoteName, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        await ChangeDirAsync(remoteName, ct);
        long sum = 0;
        foreach (var e in await ListAsync(ct))
            sum += e.IsDir ? await RemoteDirSizeRecAsync(e.Name, ct) : e.Size;
        await ChangeDirAsync("..", ct);
        return sum;
    }

    /// <summary>Recursively delete a remote directory and everything inside it (files + nested folders).
    /// FTP's RMD only removes empty directories, so contents are cleared first. Restores the working dir.</summary>
    public async Task DeleteDirectoryAsync(string remoteName, CancellationToken ct)
    {
        var start = CurrentDir;
        try { await DeleteDirRecAsync(remoteName, ct); }
        finally { try { await ChangeDirAsync(start, ct); } catch { } }
    }

    private async Task DeleteDirRecAsync(string remoteName, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        await ChangeDirAsync(remoteName, ct);
        var entries = await ListAsync(ct);
        foreach (var e in entries)
        {
            ct.ThrowIfCancellationRequested();
            if (e.IsDir) await DeleteDirRecAsync(e.Name, ct);
            else await DeleteAsync(e.Name, ct);
        }
        await ChangeDirAsync("..", ct);          // back to the parent before removing the now-empty dir
        await RemoveDirAsync(remoteName, ct);
    }

    /// <summary>Make a remote name safe to use as a local file/dir name.</summary>
    private static string SafeLocal(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        return name.Trim();
    }

    /// <summary>Copy <paramref name="from"/> → <paramref name="to"/>, reporting the DELTA bytes of each chunk
    /// (not a cumulative total) so callers can aggregate overall progress across many files.</summary>
    private static async Task PumpAsync(Stream from, Stream to, IProgress<long>? progress, CancellationToken ct)
    {
        var buf = new byte[81920];
        int n;
        while ((n = await from.ReadAsync(buf, ct)) > 0)
        {
            await to.WriteAsync(buf.AsMemory(0, n), ct);
            progress?.Report(n);
        }
        await to.FlushAsync(ct);
    }

    // ── data channel (passive) ───────────────────────────────────────────────────
    /// <summary>Open the passive data TCP connection only. The TLS handshake (when PROT P) is intentionally
    /// deferred to <see cref="SecureDataAsync"/>, run AFTER the transfer command + 150 reply — otherwise the
    /// client would block handshaking before the server has started its side, deadlocking the channel.</summary>
    private async Task<TcpClient> ConnectDataAsync(CancellationToken ct)
    {
        var (ip, port) = await EnterPassiveAsync(ct);
        var conn = new TcpClient { NoDelay = true };
        using (var to = LinkTimeout(ct)) await conn.ConnectAsync(ip, port, to.Token);
        return conn;
    }

    private async Task<Stream> SecureDataAsync(TcpClient conn, CancellationToken ct)
    {
        Stream s = conn.GetStream();
        if (!_secure) return s;
        var ssl = new SslStream(s, leaveInnerStreamOpen: false, (_, _, _, _) => true);
        using (var to = LinkTimeout(ct))
            await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = _host,
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
            }, to.Token);
        return ssl;
    }

    /// <summary>A cancellation source linked to <paramref name="ct"/> that also self-cancels after a timeout,
    /// so a misconfigured connection (e.g. wrong TLS mode, unreachable passive port) fails fast instead of
    /// hanging the UI.</summary>
    private static CancellationTokenSource LinkTimeout(CancellationToken ct, int seconds = 15)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(seconds));
        return cts;
    }

    private static void Close(Stream s, TcpClient conn)
    {
        try { s.Dispose(); } catch { }
        try { conn.Dispose(); } catch { }
    }

    private async Task<(IPAddress Ip, int Port)> EnterPassiveAsync(CancellationToken ct)
    {
        // Try EPSV first (works for IPv4/IPv6), fall back to PASV.
        var e = await SendAsync("EPSV", ct);
        if (e.Code == 229)
        {
            var l = e.Text.IndexOf('(');
            var r = e.Text.IndexOf(')');
            if (l >= 0 && r > l)
            {
                var inner = e.Text.Substring(l + 1, r - l - 1);   // |||port|
                var parts = inner.Split('|');
                if (parts.Length >= 4 && int.TryParse(parts[3], out var ep))
                    return (((IPEndPoint)_tcp!.Client.RemoteEndPoint!).Address, ep);
            }
        }

        var p = await SendAsync("PASV", ct);
        Expect(p, 227);
        var lp = p.Text.IndexOf('(');
        var rp = p.Text.IndexOf(')');
        if (lp < 0 || rp < lp) throw new IOException("PASV 响应无法解析：" + p.Text);
        var nums = p.Text.Substring(lp + 1, rp - lp - 1).Split(',');
        if (nums.Length != 6) throw new IOException("PASV 响应无法解析：" + p.Text);
        var ip = new IPAddress(new[] { byte.Parse(nums[0]), byte.Parse(nums[1]), byte.Parse(nums[2]), byte.Parse(nums[3]) });
        var port = int.Parse(nums[4]) * 256 + int.Parse(nums[5]);
        return (ip, port);
    }

    // ── parsing ────────────────────────────────────────────────────────────────
    private static List<FtpRemoteEntry> ParseMlsd(string text)
    {
        var list = new List<FtpRemoteEntry>();
        foreach (var line in text.Split('\n'))
        {
            var l = line.TrimEnd('\r');
            if (l.Length == 0) continue;
            var sp = l.IndexOf(' ');
            if (sp < 0) continue;
            var facts = l[..sp];
            var name = l[(sp + 1)..];
            string type = "file"; long size = 0; DateTime? mod = null; string perm = "";
            foreach (var f in facts.Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                var eq = f.IndexOf('=');
                if (eq < 0) continue;
                var k = f[..eq].ToLowerInvariant();
                var v = f[(eq + 1)..];
                if (k == "type") type = v.ToLowerInvariant();
                else if (k == "size") long.TryParse(v, out size);
                else if (k == "perm") perm = v.ToLowerInvariant();
                else if (k == "modify" && DateTime.TryParseExact(v, "yyyyMMddHHmmss", CultureInfo.InvariantCulture,
                             DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var dt)) mod = dt.ToLocalTime();
            }
            if (type is "cdir" or "pdir" || name is "." or "..") continue;
            list.Add(new FtpRemoteEntry(name, type == "dir", size, mod, perm));
        }
        return Sort(list);
    }

    private static List<FtpRemoteEntry> ParseList(string text)
    {
        var list = new List<FtpRemoteEntry>();
        foreach (var line in text.Split('\n'))
        {
            var l = line.TrimEnd('\r');
            if (l.Length == 0) continue;

            // Windows / IIS DOS format: "MM-dd-yy  hh:mmAM <DIR> name" or "... 1234 name"
            if (char.IsDigit(l[0]) && (l.Contains("<DIR>") || l.Contains("AM") || l.Contains("PM")))
            {
                var t = l.Split((char[]?)null, 4, StringSplitOptions.RemoveEmptyEntries);
                if (t.Length == 4)
                {
                    var isDir = t[2] == "<DIR>";
                    long.TryParse(t[2], out var sz);
                    list.Add(new FtpRemoteEntry(t[3], isDir, isDir ? 0 : sz, null));
                    continue;
                }
            }

            // Unix ls -l: perms links owner group size Mon dd time/year name
            if (l[0] is '-' or 'd' or 'l')
            {
                var t = l.Split((char[]?)null, 9, StringSplitOptions.RemoveEmptyEntries);
                if (t.Length >= 9)
                {
                    var isDir = l[0] == 'd';
                    long.TryParse(t[4], out var sz);
                    var name = t[8];
                    if (name is "." or "..") continue;
                    list.Add(new FtpRemoteEntry(name, isDir, sz, null));
                }
            }
        }
        return Sort(list);
    }

    private static List<FtpRemoteEntry> Sort(List<FtpRemoteEntry> list)
        => list.OrderByDescending(e => e.IsDir).ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ToList();

    private static string? ParsePwd(string text)
    {
        var a = text.IndexOf('"');
        var b = text.LastIndexOf('"');
        return (a >= 0 && b > a) ? text.Substring(a + 1, b - a - 1) : null;
    }

    // ── control I/O ────────────────────────────────────────────────────────────
    private async Task<(int Code, string Text)> SendAsync(string cmd, CancellationToken ct, bool mask = false)
    {
        var bytes = Encoding.UTF8.GetBytes(cmd + "\r\n");
        await _ctrl.WriteAsync(bytes, ct);
        await _ctrl.FlushAsync(ct);
        Log?.Invoke("→ " + (mask ? cmd[..Math.Min(4, cmd.Length)] + " ******" : cmd));
        var r = await ReadResponseAsync(ct);
        Log?.Invoke($"← {r.Code} {r.Text.Replace("\n", " ")}");
        return r;
    }

    private async Task<(int Code, string Text)> ReadResponseAsync(CancellationToken ct)
    {
        var first = await ReadLineAsync(ct) ?? throw new IOException("连接已关闭");
        var code = first.Length >= 3 && int.TryParse(first[..3], out var c) ? c : 0;
        var text = first.Length > 4 ? first[4..] : "";
        if (first.Length >= 4 && first[3] == '-')
        {
            while (true)
            {
                var l = await ReadLineAsync(ct) ?? throw new IOException("连接已关闭");
                if (l.Length >= 4 && int.TryParse(l[..3], out var cc) && cc == code && l[3] == ' ') { text += "\n" + l[4..]; break; }
                text += "\n" + l;
            }
        }
        return (code, text);
    }

    private async Task<string?> ReadLineAsync(CancellationToken ct)
    {
        var bytes = new List<byte>(64);
        while (true)
        {
            if (_rpos >= _rlen)
            {
                _rlen = await _ctrl.ReadAsync(_rbuf.AsMemory(), ct);
                _rpos = 0;
                if (_rlen == 0) return bytes.Count > 0 ? Encoding.UTF8.GetString(bytes.ToArray()) : null;
            }
            var b = _rbuf[_rpos++];
            if (b == (byte)'\n') break;
            if (b == (byte)'\r') continue;
            bytes.Add(b);
        }
        return Encoding.UTF8.GetString(bytes.ToArray());
    }

    private static async Task<string> ReadAllAsync(Stream s, CancellationToken ct)
    {
        using var ms = new MemoryStream();
        await s.CopyToAsync(ms, ct);
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    private static void Expect((int Code, string Text) r, int ok)
    {
        if (r.Code != ok && !(ok == 230 && r.Code == 202)) throw new IOException($"服务器返回 {r.Code}：{r.Text}");
    }

    public async Task QuitAsync(CancellationToken ct)
    {
        try { await SendAsync("QUIT", ct); } catch { }
    }

    public void Dispose()
    {
        try { _ctrl.Dispose(); } catch { }
        try { _tcp?.Dispose(); } catch { }
    }
}
