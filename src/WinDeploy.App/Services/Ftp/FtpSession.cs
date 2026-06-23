using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;

namespace WinDeploy.App.Services.Ftp;

/// <summary>Handles one client control connection: reads commands, enforces auth/permissions, confines the
/// user to their home directory, and runs active/passive (optionally TLS-protected) data transfers.</summary>
internal sealed class FtpSession
{
    private static readonly StringComparison OIC = StringComparison.OrdinalIgnoreCase;

    private readonly FtpServer _server;
    private readonly int _id;
    private readonly TcpClient _client;
    private readonly FtpConnectionInfo _info;
    private readonly bool _implicitTls;

    private Stream _ctrl = Stream.Null;
    private CancellationToken _ct;

    private readonly byte[] _rbuf = new byte[4096];
    private int _rpos, _rlen;

    // ── session state ────────────────────────────────────────────────────────
    private bool _ctrlSecure;
    private bool _authed;
    private string _pendingUser = "";
    private string _user = "";
    private string _home = "";          // real root directory (chroot)
    private string _cwd = "/";          // virtual current directory
    private FtpPerm _perms = FtpPerm.None;
    private bool _protectData;           // PROT P
    private long _restOffset;
    private string? _renameFrom;

    // data channel
    private TcpListener? _pasvListener;
    private IPEndPoint? _activeEp;

    public FtpSession(FtpServer server, int id, TcpClient client, FtpConnectionInfo info, bool implicitTls)
    {
        _server = server;
        _id = id;
        _client = client;
        _info = info;
        _implicitTls = implicitTls;
    }

    public async Task RunAsync(CancellationToken ct)
    {
        _ct = ct;
        try
        {
            _client.NoDelay = true;
            _ctrl = _client.GetStream();

            if (_implicitTls)
            {
                if (!await UpgradeControlTlsAsync()) return;
                _protectData = true;   // implicit FTPS defaults to protected data
            }

            await SendAsync(220, "OwO WinDeploy FTP 服务就绪");

            while (!ct.IsCancellationRequested)
            {
                var line = await ReadCommandLineAsync();
                if (line == null) break;                 // client disconnected
                if (line.Length == 0) continue;

                var (cmd, arg) = Split(line);
                _server.Log(_id, $"← {cmd}{(cmd is "PASS" ? " ******" : arg.Length > 0 ? " " + arg : "")}");
                _info.Activity = cmd;

                if (await Dispatch(cmd, arg)) break;     // QUIT
            }
        }
        catch (OperationCanceledException) { /* shutting down */ }
        catch (IOException) { /* peer reset */ }
        catch (Exception ex) { _server.Log(_id, "错误：" + ex.Message); }
        finally
        {
            CloseData();
            try { _ctrl.Dispose(); } catch { }
        }
    }

    /// <summary>Returns true when the session should end (QUIT).</summary>
    private async Task<bool> Dispatch(string cmd, string arg)
    {
        switch (cmd)
        {
            case "QUIT": await SendAsync(221, "再见"); return true;
            case "NOOP": await SendAsync(200, "OK"); return false;
            case "SYST": await SendAsync(215, "UNIX Type: L8"); return false;
            case "FEAT": await SendFeaturesAsync(); return false;
            case "OPTS": await HandleOptsAsync(arg); return false;
            case "AUTH": await HandleAuthAsync(arg); return false;
            case "PBSZ": await SendAsync(200, "PBSZ=0"); return false;
            case "PROT": await HandleProtAsync(arg); return false;
            case "USER": await HandleUserAsync(arg); return false;
            case "PASS": await HandlePassAsync(arg); return false;
            case "TYPE": await SendAsync(arg.Trim().ToUpperInvariant() is "A" or "I" or "A N" or "I N" ? 200 : 504, "类型已设置"); return false;
            case "MODE": await SendAsync(arg.Trim().Equals("S", OIC) ? 200 : 504, "仅支持 Stream 模式"); return false;
            case "STRU": await SendAsync(arg.Trim().Equals("F", OIC) ? 200 : 504, "仅支持 File 结构"); return false;
            case "HELP": await SendAsync(214, "WinDeploy FTP — 支持常用 FTP/FTPS 命令"); return false;
        }

        // Everything below requires a logged-in user.
        if (!_authed) { await SendAsync(530, "请先登录 (USER/PASS)"); return false; }

        switch (cmd)
        {
            case "PWD":
            case "XPWD": await SendAsync(257, $"\"{_cwd}\" 当前目录"); break;
            case "CWD":
            case "XCWD": await HandleCwdAsync(arg); break;
            case "CDUP":
            case "XCUP": await HandleCwdAsync(".."); break;
            case "PASV": await HandlePasvAsync(extended: false); break;
            case "EPSV": await HandlePasvAsync(extended: true); break;
            case "PORT": await HandlePortAsync(arg, extended: false); break;
            case "EPRT": await HandlePortAsync(arg, extended: true); break;
            case "LIST": await HandleListAsync(arg, ListFormat.List); break;
            case "NLST": await HandleListAsync(arg, ListFormat.Nlst); break;
            case "MLSD": await HandleListAsync(arg, ListFormat.Mlsd); break;
            case "MLST": await HandleMlstAsync(arg); break;
            case "RETR": await HandleRetrAsync(arg); break;
            case "STOR": await HandleStorAsync(arg, append: false); break;
            case "STOU": await HandleStorAsync(arg, append: false, unique: true); break;
            case "APPE": await HandleStorAsync(arg, append: true); break;
            case "DELE": await HandleDeleAsync(arg); break;
            case "RNFR": await HandleRnfrAsync(arg); break;
            case "RNTO": await HandleRntoAsync(arg); break;
            case "MKD":
            case "XMKD": await HandleMkdAsync(arg); break;
            case "RMD":
            case "XRMD": await HandleRmdAsync(arg); break;
            case "SIZE": await HandleSizeAsync(arg); break;
            case "MDTM": await HandleMdtmAsync(arg); break;
            case "REST": await HandleRestAsync(arg); break;
            case "ABOR": await SendAsync(226, "无进行中的传输"); break;
            case "STAT": await SendAsync(211, $"已登录 {_user}，目录 {_cwd}"); break;
            default: await SendAsync(500, $"未识别的命令 {cmd}"); break;
        }
        return false;
    }

    // ── auth / TLS ─────────────────────────────────────────────────────────────
    private async Task HandleAuthAsync(string arg)
    {
        var mech = arg.Trim().ToUpperInvariant();
        if (mech is not ("TLS" or "SSL" or "TLS-C" or "TLS-P")) { await SendAsync(504, "仅支持 AUTH TLS"); return; }
        if (!_server.Config.TlsEnabled || _server.Certificate == null) { await SendAsync(431, "服务器未启用 TLS"); return; }
        if (_ctrlSecure) { await SendAsync(234, "已是 TLS"); return; }
        await SendAsync(234, "AUTH TLS 就绪，开始握手");
        if (!await UpgradeControlTlsAsync())
        {
            _server.Log(_id, "TLS 握手失败");
        }
    }

    private async Task<bool> UpgradeControlTlsAsync()
    {
        try
        {
            var ssl = new SslStream(_ctrl, leaveInnerStreamOpen: false);
            await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
            {
                ServerCertificate = _server.Certificate,
                ClientCertificateRequired = false,
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
            }, _ct);
            _ctrl = ssl;
            _ctrlSecure = true;
            _rpos = _rlen = 0;
            return true;
        }
        catch (Exception ex) { _server.Log(_id, "控制通道 TLS 失败：" + ex.Message); return false; }
    }

    private async Task HandleProtAsync(string arg)
    {
        var lvl = arg.Trim().ToUpperInvariant();
        switch (lvl)
        {
            case "P": _protectData = true; await SendAsync(200, "数据通道：加密 (P)"); break;
            case "C": _protectData = false; await SendAsync(200, "数据通道：明文 (C)"); break;
            default: await SendAsync(504, "仅支持 PROT C / P"); break;
        }
    }

    private async Task HandleUserAsync(string arg)
    {
        _authed = false;
        _pendingUser = arg.Trim();
        if (_server.Config.RequireTls && !_ctrlSecure)
        {
            await SendAsync(534, "策略要求加密：请先执行 AUTH TLS");
            return;
        }
        if (string.IsNullOrEmpty(_pendingUser)) { await SendAsync(501, "需要用户名"); return; }
        await SendAsync(331, $"用户 {_pendingUser}，请输入密码");
    }

    private async Task HandlePassAsync(string arg)
    {
        if (string.IsNullOrEmpty(_pendingUser)) { await SendAsync(503, "请先发送 USER"); return; }
        if (_server.Config.RequireTls && !_ctrlSecure) { await SendAsync(534, "策略要求加密：请先 AUTH TLS"); return; }

        if (!TryAuthenticate(_pendingUser, arg, out var home, out var perms, out var maxConn))
        {
            await SendAsync(530, "登录失败：用户名或密码错误");
            _server.Log(_id, $"登录失败：{_pendingUser}");
            return;
        }

        // Confine to (and ensure) the home directory.
        try { Directory.CreateDirectory(home); }
        catch (Exception ex) { await SendAsync(530, "无法访问主目录：" + ex.Message); return; }

        _user = _pendingUser;
        _info.User = _user;
        _home = Path.GetFullPath(home).TrimEnd('\\', '/');
        _perms = perms;
        _cwd = "/";
        _authed = true;

        // Per-user concurrency cap (this session is already counted in the registry via _info.User).
        if (maxConn > 0 && _server.CountForUser(_user) > maxConn)
        {
            _authed = false;
            await SendAsync(530, $"该用户的并发连接已达上限（{maxConn}）");
            _server.Log(_id, $"拒绝 {_user}：超过单用户上限 {maxConn}");
            // signal end by throwing a benign IO to close
            throw new IOException("per-user limit");
        }

        await SendAsync(230, $"登录成功，欢迎 {_user}");
        _server.Log(_id, $"登录成功：{_user} → {_home}（权限 {_perms}）");
    }

    private bool TryAuthenticate(string user, string pass, out string home, out FtpPerm perms, out int maxConn)
    {
        home = ""; perms = FtpPerm.None; maxConn = 0;
        var cfg = _server.Config;

        if (cfg.AllowAnonymous && (user.Equals("anonymous", OIC) || user.Equals("ftp", OIC)))
        {
            if (string.IsNullOrWhiteSpace(cfg.AnonymousHome)) return false;
            home = cfg.AnonymousHome!;
            perms = cfg.AnonymousPermissions;
            return true;
        }

        var u = cfg.Users.FirstOrDefault(x => x.Name.Equals(user, OIC));
        if (u == null || !u.Enabled) return false;
        if (!FtpPassword.Verify(pass, u.PasswordHash, u.PasswordSalt)) return false;
        (home, perms, maxConn) = cfg.Resolve(u);
        return !string.IsNullOrWhiteSpace(home);
    }

    // ── navigation ─────────────────────────────────────────────────────────────
    private async Task HandleCwdAsync(string arg)
    {
        var v = ResolveVirtual(arg);
        if (v == null || !Directory.Exists(ToReal(v))) { await SendAsync(550, "目录不存在"); return; }
        _cwd = v;
        await SendAsync(250, $"目录已切换到 {_cwd}");
    }

    // ── passive / active ───────────────────────────────────────────────────────
    private async Task HandlePasvAsync(bool extended)
    {
        CloseData();
        try
        {
            var local = ((IPEndPoint)_client.Client.LocalEndPoint!).Address;
            _pasvListener = _server.CreatePassiveListener(local, out var port);

            if (extended)
            {
                await SendAsync(229, $"Entering Extended Passive Mode (|||{port}|)");
            }
            else
            {
                var adv = AdvertisedIp(local);
                var b = adv.GetAddressBytes();
                await SendRawAsync($"227 Entering Passive Mode ({b[0]},{b[1]},{b[2]},{b[3]},{port / 256},{port % 256})\r\n");
            }
        }
        catch (Exception ex) { await SendAsync(425, "无法进入被动模式：" + ex.Message); }
    }

    private IPAddress AdvertisedIp(IPAddress local)
    {
        var ext = _server.Config.PassiveExternalIp;
        if (!string.IsNullOrWhiteSpace(ext) && IPAddress.TryParse(ext.Trim(), out var ip) &&
            ip.AddressFamily == AddressFamily.InterNetwork) return ip;
        if (local.AddressFamily == AddressFamily.InterNetwork && !local.Equals(IPAddress.Any)) return local;
        return IPAddress.Loopback;
    }

    private async Task HandlePortAsync(string arg, bool extended)
    {
        CloseData();
        try
        {
            if (extended)
            {
                // EPRT |proto|addr|port|
                var parts = arg.Split('|');
                if (parts.Length < 4) { await SendAsync(501, "EPRT 参数无效"); return; }
                _activeEp = new IPEndPoint(IPAddress.Parse(parts[2]), int.Parse(parts[3]));
            }
            else
            {
                var n = arg.Split(',');
                if (n.Length != 6) { await SendAsync(501, "PORT 参数无效"); return; }
                var ip = new IPAddress(new[] { byte.Parse(n[0]), byte.Parse(n[1]), byte.Parse(n[2]), byte.Parse(n[3]) });
                var port = int.Parse(n[4]) * 256 + int.Parse(n[5]);
                _activeEp = new IPEndPoint(ip, port);
            }
            await SendAsync(200, "PORT 命令成功");
        }
        catch { await SendAsync(501, "地址解析失败"); }
    }

    /// <summary>Open the data connection (accept the passive client or dial the active endpoint), wrapping it
    /// in TLS when PROT P is in effect. The caller disposes the returned stream.</summary>
    private async Task<Stream?> OpenDataAsync()
    {
        Stream? raw = null;
        Socket? socket = null;
        try
        {
            if (_pasvListener != null)
            {
                using var to = CancellationTokenSource.CreateLinkedTokenSource(_ct);
                to.CancelAfter(TimeSpan.FromSeconds(30));
                var c = await _pasvListener.AcceptTcpClientAsync(to.Token);
                socket = c.Client;
                raw = c.GetStream();
            }
            else if (_activeEp != null)
            {
                var c = new TcpClient();
                using var to = CancellationTokenSource.CreateLinkedTokenSource(_ct);
                to.CancelAfter(TimeSpan.FromSeconds(30));
                await c.ConnectAsync(_activeEp.Address, _activeEp.Port, to.Token);
                socket = c.Client;
                raw = c.GetStream();
            }
            else return null;

            if (_protectData && _server.Certificate != null)
            {
                // The server is always the TLS server on the data channel, regardless of who opened the TCP.
                var ssl = new SslStream(raw, leaveInnerStreamOpen: false);
                await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                {
                    ServerCertificate = _server.Certificate,
                    ClientCertificateRequired = false,
                    EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                }, _ct);
                return ssl;
            }
            return raw;
        }
        catch
        {
            try { raw?.Dispose(); } catch { }
            try { socket?.Dispose(); } catch { }
            return null;
        }
        finally { CloseData(); }   // a listener is single-use; active endpoint consumed
    }

    private void CloseData()
    {
        try { _pasvListener?.Stop(); } catch { }
        _pasvListener = null;
        _activeEp = null;
    }

    // ── listings ───────────────────────────────────────────────────────────────
    private enum ListFormat { List, Nlst, Mlsd }

    private async Task HandleListAsync(string arg, ListFormat fmt)
    {
        if (!Has(FtpPerm.List)) { await SendAsync(550, "无列目录权限"); return; }

        // LIST may carry options like "-la"; ignore leading dash-args.
        var path = StripListFlags(arg);
        var v = string.IsNullOrEmpty(path) ? _cwd : ResolveVirtual(path);
        if (v == null) { await SendAsync(550, "路径无效"); return; }
        var real = ToReal(v);
        if (!Directory.Exists(real)) { await SendAsync(550, "目录不存在"); return; }

        await SendAsync(150, "打开数据连接，发送目录列表");
        var data = await OpenDataAsync();
        if (data == null) { await SendAsync(425, "无法建立数据连接"); return; }
        try
        {
            var sb = new StringBuilder();
            var dirs = Directory.GetDirectories(real).OrderBy(p => p, StringComparer.OrdinalIgnoreCase);
            var files = Directory.GetFiles(real).OrderBy(p => p, StringComparer.OrdinalIgnoreCase);
            foreach (var d in dirs) sb.Append(FormatEntry(new DirectoryInfo(d), true, fmt));
            foreach (var f in files) sb.Append(FormatEntry(new FileInfo(f), false, fmt));

            var bytes = Encoding.UTF8.GetBytes(sb.ToString());
            await data.WriteAsync(bytes, _ct);
            await data.FlushAsync(_ct);
            await SendAsync(226, "目录发送完成");
        }
        catch (Exception ex) { await SendAsync(426, "传输中断：" + ex.Message); }
        finally { data.Dispose(); }
    }

    private string FormatEntry(FileSystemInfo fi, bool isDir, ListFormat fmt)
    {
        long size = 0;
        try { size = isDir ? 0 : ((FileInfo)fi).Length; } catch { }
        var mtime = SafeWriteTime(fi);

        switch (fmt)
        {
            case ListFormat.Nlst:
                return fi.Name + "\r\n";
            case ListFormat.Mlsd:
                var type = isDir ? "dir" : "file";
                var sz = isDir ? "" : $"size={size};";
                return $"type={type};{sz}perm={PermFact(isDir)};modify={mtime.ToUniversalTime():yyyyMMddHHmmss}; {fi.Name}\r\n";
            default: // Unix ls -l
                var perm = isDir ? "drwxr-xr-x" : "-rw-r--r--";
                var date = FormatLsDate(mtime);
                return $"{perm} 1 owner group {size,12} {date} {fi.Name}\r\n";
        }
    }

    /// <summary>The MLSD <c>perm</c> fact (RFC 3659) for an entry, derived from the user's effective
    /// permissions, so clients can pre-disable actions they aren't allowed to perform.</summary>
    private string PermFact(bool isDir)
    {
        var sb = new StringBuilder();
        if (isDir)
        {
            if (Has(FtpPerm.List)) { sb.Append('e'); sb.Append('l'); }   // enter + list
            if (Has(FtpPerm.Upload)) sb.Append('c');                      // create files within
            if (Has(FtpPerm.CreateDir)) sb.Append('m');                   // make subdir
            if (Has(FtpPerm.Delete)) sb.Append('p');                      // purge files within
            if (Has(FtpPerm.DeleteDir)) sb.Append('d');                   // delete this dir
            if (Has(FtpPerm.Rename)) sb.Append('f');                      // rename this dir
        }
        else
        {
            if (Has(FtpPerm.Download)) sb.Append('r');                    // retrieve
            if (Has(FtpPerm.Upload)) sb.Append('w');                      // (over)write
            if (Has(FtpPerm.Append)) sb.Append('a');                      // append
            if (Has(FtpPerm.Delete)) sb.Append('d');                      // delete
            if (Has(FtpPerm.Rename)) sb.Append('f');                      // rename
        }
        return sb.ToString();
    }

    private async Task HandleMlstAsync(string arg)
    {
        var v = string.IsNullOrEmpty(arg.Trim()) ? _cwd : ResolveVirtual(arg);
        if (v == null) { await SendAsync(550, "路径无效"); return; }
        var real = ToReal(v);
        var isDir = Directory.Exists(real);
        if (!isDir && !File.Exists(real)) { await SendAsync(550, "不存在"); return; }
        FileSystemInfo fi = isDir ? new DirectoryInfo(real) : new FileInfo(real);
        await SendRawAsync($"250-MLST {v}\r\n {FormatEntry(fi, isDir, ListFormat.Mlsd).TrimEnd()}\r\n250 完成\r\n");
    }

    // ── transfers ──────────────────────────────────────────────────────────────
    private async Task HandleRetrAsync(string arg)
    {
        if (!Has(FtpPerm.Download)) { await SendAsync(550, "无下载权限"); _restOffset = 0; return; }
        var v = ResolveVirtual(arg);
        if (v == null || !File.Exists(ToReal(v))) { await SendAsync(550, "文件不存在"); _restOffset = 0; return; }
        var real = ToReal(v);

        await SendAsync(150, "打开数据连接，开始下载");
        var data = await OpenDataAsync();
        if (data == null) { await SendAsync(425, "无法建立数据连接"); _restOffset = 0; return; }
        try
        {
            await using var fs = new FileStream(real, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (_restOffset > 0 && _restOffset <= fs.Length) fs.Seek(_restOffset, SeekOrigin.Begin);
            _restOffset = 0;
            var sent = await PumpAsync(fs, data, down: true);
            _info.Activity = $"下载 {Path.GetFileName(real)}";
            await SendAsync(226, $"下载完成（{sent} 字节）");
        }
        catch (Exception ex) { await SendAsync(426, "下载中断：" + ex.Message); }
        finally { data.Dispose(); _restOffset = 0; }
    }

    private async Task HandleStorAsync(string arg, bool append, bool unique = false)
    {
        var need = append ? FtpPerm.Append : FtpPerm.Upload;
        if (!Has(need)) { await SendAsync(550, append ? "无追加权限" : "无上传权限"); _restOffset = 0; return; }

        string? v;
        if (unique)
        {
            var baseV = ResolveVirtual(arg.Length == 0 ? "upload" : arg) ?? _cwd;
            var unique2 = $"stou_{DateTime.Now:yyyyMMddHHmmss}_{_id}.dat";
            v = CombineVirtual(ParentOf(baseV), unique2);
        }
        else v = ResolveVirtual(arg);

        if (v == null) { await SendAsync(550, "路径无效"); _restOffset = 0; return; }
        var real = ToReal(v);
        try { Directory.CreateDirectory(Path.GetDirectoryName(real)!); } catch { }

        if (unique) await SendRawAsync($"150 FILE: {v}\r\n");
        else await SendAsync(150, "打开数据连接，开始接收");

        var data = await OpenDataAsync();
        if (data == null) { await SendAsync(425, "无法建立数据连接"); _restOffset = 0; return; }
        try
        {
            FileMode mode = append ? FileMode.Append : (_restOffset > 0 ? FileMode.Open : FileMode.Create);
            await using var fs = new FileStream(real, mode, FileAccess.Write, FileShare.None);
            if (!append && _restOffset > 0 && _restOffset <= fs.Length) fs.Seek(_restOffset, SeekOrigin.Begin);
            _restOffset = 0;
            var got = await PumpAsync(data, fs, down: false);
            _info.Activity = $"上传 {Path.GetFileName(real)}";
            await SendAsync(226, $"上传完成（{got} 字节）");
        }
        catch (Exception ex) { await SendAsync(426, "上传中断：" + ex.Message); }
        finally { data.Dispose(); _restOffset = 0; }
    }

    /// <summary>Copy between streams, counting bytes into the connection's up/down totals.</summary>
    private async Task<long> PumpAsync(Stream from, Stream to, bool down)
    {
        var buf = new byte[81920];
        long total = 0;
        int n;
        while ((n = await from.ReadAsync(buf, _ct)) > 0)
        {
            await to.WriteAsync(buf.AsMemory(0, n), _ct);
            total += n;
            if (down) Interlocked.Add(ref _info.BytesDown, n);
            else Interlocked.Add(ref _info.BytesUp, n);
        }
        await to.FlushAsync(_ct);
        return total;
    }

    // ── file ops ─────────────────────────────────────────────────────────────
    private async Task HandleDeleAsync(string arg)
    {
        if (!Has(FtpPerm.Delete)) { await SendAsync(550, "无删除权限"); return; }
        var v = ResolveVirtual(arg);
        if (v == null || !File.Exists(ToReal(v))) { await SendAsync(550, "文件不存在"); return; }
        try { File.Delete(ToReal(v)); await SendAsync(250, "已删除"); }
        catch (Exception ex) { await SendAsync(550, "删除失败：" + ex.Message); }
    }

    private async Task HandleRnfrAsync(string arg)
    {
        if (!Has(FtpPerm.Rename)) { await SendAsync(550, "无重命名权限"); return; }
        var v = ResolveVirtual(arg);
        if (v == null) { await SendAsync(550, "路径无效"); return; }
        var real = ToReal(v);
        if (!File.Exists(real) && !Directory.Exists(real)) { await SendAsync(550, "源不存在"); return; }
        _renameFrom = real;
        await SendAsync(350, "已记录源名，请发送 RNTO");
    }

    private async Task HandleRntoAsync(string arg)
    {
        if (_renameFrom == null) { await SendAsync(503, "请先发送 RNFR"); return; }
        var v = ResolveVirtual(arg);
        if (v == null) { await SendAsync(550, "目标路径无效"); _renameFrom = null; return; }
        var dest = ToReal(v);
        try
        {
            if (Directory.Exists(_renameFrom)) Directory.Move(_renameFrom, dest);
            else File.Move(_renameFrom, dest, overwrite: false);
            await SendAsync(250, "重命名成功");
        }
        catch (Exception ex) { await SendAsync(550, "重命名失败：" + ex.Message); }
        finally { _renameFrom = null; }
    }

    private async Task HandleMkdAsync(string arg)
    {
        if (!Has(FtpPerm.CreateDir)) { await SendAsync(550, "无创建目录权限"); return; }
        var v = ResolveVirtual(arg);
        if (v == null) { await SendAsync(550, "路径无效"); return; }
        try { Directory.CreateDirectory(ToReal(v)); await SendAsync(257, $"\"{v}\" 已创建"); }
        catch (Exception ex) { await SendAsync(550, "创建失败：" + ex.Message); }
    }

    private async Task HandleRmdAsync(string arg)
    {
        if (!Has(FtpPerm.DeleteDir)) { await SendAsync(550, "无删除目录权限"); return; }
        var v = ResolveVirtual(arg);
        if (v == null || !Directory.Exists(ToReal(v))) { await SendAsync(550, "目录不存在"); return; }
        try { Directory.Delete(ToReal(v), recursive: false); await SendAsync(250, "目录已删除"); }
        catch (Exception ex) { await SendAsync(550, "删除失败（目录非空或被占用）：" + ex.Message); }
    }

    private async Task HandleSizeAsync(string arg)
    {
        var v = ResolveVirtual(arg);
        if (v == null || !File.Exists(ToReal(v))) { await SendAsync(550, "文件不存在"); return; }
        try { await SendAsync(213, new FileInfo(ToReal(v)).Length.ToString()); }
        catch (Exception ex) { await SendAsync(550, ex.Message); }
    }

    private async Task HandleMdtmAsync(string arg)
    {
        var v = ResolveVirtual(arg);
        if (v == null) { await SendAsync(550, "路径无效"); return; }
        var real = ToReal(v);
        if (!File.Exists(real) && !Directory.Exists(real)) { await SendAsync(550, "不存在"); return; }
        var t = SafeWriteTime(File.Exists(real) ? new FileInfo(real) : new DirectoryInfo(real));
        await SendAsync(213, t.ToUniversalTime().ToString("yyyyMMddHHmmss"));
    }

    private async Task HandleRestAsync(string arg)
    {
        if (long.TryParse(arg.Trim(), out var n) && n >= 0) { _restOffset = n; await SendAsync(350, $"已设置续传位置 {n}"); }
        else await SendAsync(501, "REST 参数无效");
    }

    private async Task HandleOptsAsync(string arg)
    {
        if (arg.Trim().ToUpperInvariant().StartsWith("UTF8")) await SendAsync(200, "UTF8 已启用");
        else await SendAsync(200, "OK");
    }

    private async Task SendFeaturesAsync()
    {
        var sb = new StringBuilder();
        sb.Append("211-Features:\r\n");
        sb.Append(" UTF8\r\n");
        sb.Append(" SIZE\r\n");
        sb.Append(" MDTM\r\n");
        sb.Append(" REST STREAM\r\n");
        sb.Append(" MLST type*;size*;modify*;\r\n");
        sb.Append(" MLSD\r\n");
        sb.Append(" PASV\r\n");
        sb.Append(" EPSV\r\n");
        sb.Append(" EPRT\r\n");
        sb.Append(" TVFS\r\n");
        if (_server.Config.TlsEnabled && _server.Certificate != null)
        {
            sb.Append(" AUTH TLS\r\n");
            sb.Append(" PBSZ\r\n");
            sb.Append(" PROT\r\n");
        }
        sb.Append("211 End\r\n");
        await SendRawAsync(sb.ToString());
    }

    // ── virtual filesystem (chroot) ────────────────────────────────────────────
    private bool Has(FtpPerm p) => (_perms & p) == p;

    /// <summary>Normalize an FTP path argument to a canonical virtual path ("/a/b"), rejecting any attempt to
    /// escape above the home root. Returns null when the path is invalid.</summary>
    private string? ResolveVirtual(string arg)
    {
        arg = arg.Trim();
        if (arg.Length == 0) return _cwd;
        arg = arg.Replace('\\', '/');
        var baseParts = arg.StartsWith('/')
            ? new List<string>()
            : _cwd.Split('/', StringSplitOptions.RemoveEmptyEntries).ToList();

        foreach (var seg in arg.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (seg == ".") continue;
            if (seg == "..") { if (baseParts.Count > 0) baseParts.RemoveAt(baseParts.Count - 1); continue; }
            if (seg.Contains(':') || seg.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return null;
            baseParts.Add(seg);
        }
        return "/" + string.Join('/', baseParts);
    }

    private static string ParentOf(string virtualPath)
    {
        var parts = virtualPath.Split('/', StringSplitOptions.RemoveEmptyEntries).ToList();
        if (parts.Count > 0) parts.RemoveAt(parts.Count - 1);
        return "/" + string.Join('/', parts);
    }

    private static string CombineVirtual(string dir, string name)
        => (dir == "/" ? "/" : dir + "/") + name;

    /// <summary>Map a (already-normalized) virtual path to a real path, with a final containment guard.</summary>
    private string ToReal(string virtualPath)
    {
        var rel = virtualPath.TrimStart('/').Replace('/', Path.DirectorySeparatorChar);
        var real = Path.GetFullPath(Path.Combine(_home, rel));
        var root = _home.TrimEnd(Path.DirectorySeparatorChar);
        if (!real.Equals(root, OIC) &&
            !real.StartsWith(root + Path.DirectorySeparatorChar, OIC))
            return root;   // clamp escape attempts back to the root
        return real;
    }

    // ── control I/O ────────────────────────────────────────────────────────────
    private async Task<string?> ReadCommandLineAsync()
    {
        var bytes = new List<byte>(64);
        while (true)
        {
            if (_rpos >= _rlen)
            {
                _rlen = await _ctrl.ReadAsync(_rbuf.AsMemory(), _ct);
                _rpos = 0;
                if (_rlen == 0) return bytes.Count > 0 ? Encoding.UTF8.GetString(bytes.ToArray()) : null;
            }
            var b = _rbuf[_rpos++];
            if (b == (byte)'\n') break;
            if (b == (byte)'\r') continue;
            bytes.Add(b);
            if (bytes.Count > 8192) break;   // guard against runaway lines
        }
        return Encoding.UTF8.GetString(bytes.ToArray());
    }

    private async Task SendAsync(int code, string text)
    {
        await SendRawAsync($"{code} {text}\r\n");
        _server.Log(_id, $"→ {code} {text}");
    }

    private async Task SendRawAsync(string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        await _ctrl.WriteAsync(bytes, _ct);
        await _ctrl.FlushAsync(_ct);
    }

    // ── small helpers ──────────────────────────────────────────────────────────
    private static (string Cmd, string Arg) Split(string line)
    {
        var sp = line.IndexOf(' ');
        return sp < 0 ? (line.ToUpperInvariant(), "") : (line[..sp].ToUpperInvariant(), line[(sp + 1)..]);
    }

    private static string StripListFlags(string arg)
    {
        arg = arg.Trim();
        while (arg.StartsWith('-'))
        {
            var sp = arg.IndexOf(' ');
            arg = sp < 0 ? "" : arg[(sp + 1)..].Trim();
        }
        return arg;
    }

    private static DateTime SafeWriteTime(FileSystemInfo fi)
    {
        try { return fi.LastWriteTime; } catch { return DateTime.Now; }
    }

    private static string FormatLsDate(DateTime t)
    {
        var now = DateTime.Now;
        var recent = t <= now && (now - t).TotalDays < 180;
        return recent
            ? t.ToString("MMM dd HH:mm", CultureInfo.InvariantCulture)
            : t.ToString("MMM dd  yyyy", CultureInfo.InvariantCulture);
    }
}
