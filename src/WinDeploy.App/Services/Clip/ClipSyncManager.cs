using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Windows;

namespace WinDeploy.App.Services.Clip;

/// <summary>The clipboard-sync engine: owns discovery, the inbound pairing listener, the local clipboard
/// monitor, the set of encrypted peer links, and the shared board. It enforces the edition device cap,
/// applies the auto-mirror / persistence preferences, and keeps the board replicated across peers
/// (entries forward through a mesh, deduped by id, so 3+ devices stay consistent in a future build).
///
/// All public mutators are safe to call from the UI thread; link callbacks arrive on background threads and
/// are guarded by <see cref="_gate"/>. Change events may fire on any thread — the VM marshals them.</summary>
public sealed class ClipSyncManager : IDisposable
{
    private readonly PeerDiscovery _discovery = new();
    private readonly ClipboardMonitor _monitor = new();
    private readonly List<ClipLink> _links = new();
    private readonly List<ClipEntry> _board = new();
    private readonly HashSet<string> _seenIds = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private ClipSyncConfig _config = new();

    public bool Running { get; private set; }
    public ClipSyncConfig Config => _config;
    public string InstanceId => _config.InstanceId;
    public string SelfName => _config.DeviceName;
    public int MaxLinks => ClipEdition.MaxLinks;
    public int MaxPeers => ClipEdition.MaxPeers;

    public IReadOnlyList<ClipPeer> Peers => _discovery.Peers;
    public IReadOnlyList<ClipLink> Links { get { lock (_gate) return _links.ToList(); } }
    public IReadOnlyList<ClipEntry> Board { get { lock (_gate) return _board.ToList(); } }
    public bool AtCapacity { get { lock (_gate) return _links.Count >= MaxLinks; } }

    // ── events (may fire on a background thread — the VM marshals) ────────────────────────────────────
    public event Action? BoardChanged;
    public event Action? PeersChanged;
    public event Action? LinksChanged;
    public event Action<string>? Log;

    /// <summary>Set by the VM to prompt the user for a PIN on an inbound pairing request. Receives
    /// (initiatorDeviceName, attemptIndex); returns the entered PIN, or null to decline. Null prompt ⇒ decline.</summary>
    public Func<string, int, Task<string?>>? PinPrompt { get; set; }

    public ClipSyncManager()
    {
        _discovery.PeersChanged += () => PeersChanged?.Invoke();
        _discovery.Log += m => Log?.Invoke(m);
        _monitor.Captured += OnLocalCaptured;
        _monitor.Log += m => Log?.Invoke(m);
    }

    // ── lifecycle ──────────────────────────────────────────────────────────────────────────────────────
    /// <summary>Start discovery + the pairing listener + the clipboard monitor. MUST be called on the UI
    /// thread (the monitor hooks a message-only window). Throws if the TCP port can't be bound.</summary>
    public void Start()
    {
        if (Running) return;
        _config = ClipConfigStore.Load();
        if (string.IsNullOrWhiteSpace(_config.InstanceId))
        {
            _config.InstanceId = Guid.NewGuid().ToString("N");
            ClipConfigStore.Save(_config);
        }
        _monitor.MaxImageBytes = _config.MaxImageBytes;

        if (_config.PersistHistory)
            lock (_gate) { _board.Clear(); _seenIds.Clear(); foreach (var e in ClipConfigStore.LoadHistory()) { _board.Add(e); _seenIds.Add(e.Id); } }

        _cts = new CancellationTokenSource();
        var ct = _cts.Token;

        _listener = new TcpListener(IPAddress.Any, _config.Port);
        try { _listener.Start(); }
        catch (Exception ex)
        {
            _listener = null;
            throw new InvalidOperationException($"无法监听端口 {_config.Port}：{ex.Message}", ex);
        }
        _ = AcceptLoopAsync(_listener, ct);

        _discovery.Start(_config, _config.InstanceId, WinDeploy.App.AppInfo.Version);
        _monitor.Start();

        Running = true;
        BoardChanged?.Invoke();
        Log?.Invoke($"剪贴板共享已启动 · 设备「{_config.DeviceName}」· 端口 {_config.Port}");
    }

    public void Stop()
    {
        if (!Running) return;
        Running = false;
        try { _cts?.Cancel(); } catch { }
        try { _listener?.Stop(); } catch { }
        _listener = null;
        _monitor.Stop();
        _discovery.Stop();
        lock (_gate)
        {
            foreach (var l in _links) l.Dispose();
            _links.Clear();
            if (!_config.PersistHistory) { _board.Clear(); _seenIds.Clear(); }
        }
        LinksChanged?.Invoke();
        BoardChanged?.Invoke();
        Log?.Invoke("剪贴板共享已停止");
    }

    // ── pairing ──────────────────────────────────────────────────────────────────────────────────────
    /// <summary>Initiator side: dial <paramref name="peer"/> and pair with the shown <paramref name="pin"/>.
    /// Throws on failure (capacity / wrong PIN / unreachable); the VM surfaces the message.</summary>
    public async Task InviteAsync(ClipPeer peer, string pin, CancellationToken ct)
    {
        if (AtCapacity) throw new InvalidOperationException(CapacityMessage());
        if (IsLinkedTo(peer.InstanceId)) throw new InvalidOperationException("该设备已在共享中");
        var link = await ClipLink.ConnectAsync(peer, pin, _config.InstanceId, _config.DeviceName, ct);
        AdoptLink(link);
    }

    private async Task AcceptLoopAsync(TcpListener listener, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await listener.AcceptTcpClientAsync(ct); }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (SocketException) { break; }
            _ = HandleInboundAsync(client, ct);
        }
    }

    private async Task HandleInboundAsync(TcpClient client, CancellationToken ct)
    {
        var remote = client.Client.RemoteEndPoint?.ToString() ?? "?";
        if (AtCapacity) { Log?.Invoke($"拒绝 {remote} 的配对：{CapacityMessage()}"); try { client.Dispose(); } catch { } return; }
        var prompt = PinPrompt;
        if (prompt == null) { Log?.Invoke($"拒绝 {remote} 的配对：未就绪"); try { client.Dispose(); } catch { } return; }

        try
        {
            var link = await ClipLink.AcceptAsync(client, prompt, _config.InstanceId, _config.DeviceName, ct);
            if (link == null) { Log?.Invoke($"{remote} 的配对未完成（取消 / PIN 错误）"); return; }
            if (AtCapacity || IsLinkedTo(link.PeerId)) { Log?.Invoke($"放弃与「{link.PeerName}」的重复/超额连接"); link.Dispose(); return; }
            AdoptLink(link);
        }
        catch (Exception ex) { Log?.Invoke($"{remote} 配对失败：{ex.Message}"); try { client.Dispose(); } catch { } }
    }

    /// <summary>Wire up a freshly paired link, start its pump, announce it, and exchange the current board.</summary>
    private void AdoptLink(ClipLink link)
    {
        lock (_gate) _links.Add(link);
        link.MessageReceived += OnLinkMessage;
        link.Closed += OnLinkClosed;
        link.Log += m => Log?.Invoke(m);
        link.StartPump();
        LinksChanged?.Invoke();
        Log?.Invoke($"已与「{link.PeerName}」建立加密剪贴板共享");
        // Send our board so the new peer catches up; it sends us theirs (merged, deduped by id).
        _ = SafeSend(link, new ClipWire { Kind = ClipWireKind.Board, Entries = Board.ToList() });
    }

    private void OnLinkClosed(ClipLink link)
    {
        bool removed;
        lock (_gate) removed = _links.Remove(link);
        link.Dispose();
        if (removed) { LinksChanged?.Invoke(); Log?.Invoke($"与「{link.PeerName}」的共享已断开"); }
    }

    /// <summary>Disconnect one peer link (user-initiated from the UI).</summary>
    public void Disconnect(ClipLink link)
    {
        lock (_gate) _links.Remove(link);
        link.Close();
        link.Dispose();
        LinksChanged?.Invoke();
    }

    private bool IsLinkedTo(string peerId)
    {
        if (string.IsNullOrEmpty(peerId)) return false;
        lock (_gate) return _links.Any(l => l.PeerId == peerId);
    }

    // ── inbound messages ───────────────────────────────────────────────────────────────────────────────
    private void OnLinkMessage(ClipLink from, ClipWire wire)
    {
        switch (wire.Kind)
        {
            case ClipWireKind.Board:
                if (wire.Entries != null) foreach (var e in wire.Entries) IngestRemoteEntry(e, from);
                break;
            case ClipWireKind.Entry:
                if (wire.Entry != null) IngestRemoteEntry(wire.Entry, from);
                break;
            case ClipWireKind.Delete:
                if (!string.IsNullOrEmpty(wire.EntryId)) RemoveEntry(wire.EntryId!, broadcast: true, except: from);
                break;
        }
    }

    /// <summary>Accept a remote entry: dedupe by id, add to the board, optionally mirror to the local
    /// clipboard, then forward to other peers (mesh) so 3+ devices converge.</summary>
    private void IngestRemoteEntry(ClipEntry entry, ClipLink from)
    {
        lock (_gate)
        {
            if (!_seenIds.Add(entry.Id)) return;   // already have it
            InsertEntry(entry);
        }
        if (_config.AutoApplyToLocal) ApplyToLocalClipboard(entry);
        BoardChanged?.Invoke();
        PersistIfEnabled();
        BroadcastEntry(entry, except: from);
    }

    // ── local clipboard → board → peers ─────────────────────────────────────────────────────────────────
    private void OnLocalCaptured(ClipEntry entry)
    {
        entry.OriginId = _config.InstanceId;
        entry.OriginName = _config.DeviceName;
        lock (_gate)
        {
            if (!_seenIds.Add(entry.Id)) return;
            InsertEntry(entry);
        }
        BoardChanged?.Invoke();
        PersistIfEnabled();
        BroadcastEntry(entry, except: null);
    }

    // ── central management (called from the board VM) ──────────────────────────────────────────────────
    /// <summary>Manually add a text entry from this device and sync it.</summary>
    public void AddText(string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        var entry = new ClipEntry
        {
            Kind = ClipKind.Text, Text = text, CreatedAtUnix = ClipEntry.NowUnix(),
            OriginId = _config.InstanceId, OriginName = _config.DeviceName,
        };
        lock (_gate) { _seenIds.Add(entry.Id); InsertEntry(entry); }
        BoardChanged?.Invoke();
        PersistIfEnabled();
        BroadcastEntry(entry, except: null);
    }

    /// <summary>Delete an entry everywhere (removes locally and tells every peer).</summary>
    public void Delete(string entryId) => RemoveEntry(entryId, broadcast: true, except: null);

    /// <summary>Clear this device's board view only (does not delete on peers).</summary>
    public void ClearLocal()
    {
        lock (_gate) { _board.Clear(); _seenIds.Clear(); }
        BoardChanged?.Invoke();
        if (_config.PersistHistory) ClipConfigStore.ClearHistory();
    }

    /// <summary>Copy an entry onto this machine's clipboard (manual "use this"), suppressing the echo.</summary>
    public void CopyToLocal(ClipEntry entry) => ApplyToLocalClipboard(entry);

    private void RemoveEntry(string entryId, bool broadcast, ClipLink? except)
    {
        bool removed;
        lock (_gate)
        {
            var idx = _board.FindIndex(e => e.Id == entryId);
            removed = idx >= 0;
            if (removed) _board.RemoveAt(idx);
            // keep it in _seenIds so a late echo of the same id doesn't resurrect it
        }
        // A peer's delete for something we don't have: stop here. Not relaying also breaks any mesh cycle
        // (in a ring, a delete would otherwise loop forever once every node has already removed the entry).
        if (!removed && except != null) return;
        if (removed) { BoardChanged?.Invoke(); PersistIfEnabled(); }
        if (broadcast)
            foreach (var l in Links) if (!ReferenceEquals(l, except)) _ = SafeSend(l, new ClipWire { Kind = ClipWireKind.Delete, EntryId = entryId });
    }

    // ── helpers ────────────────────────────────────────────────────────────────────────────────────────
    /// <summary>Insert newest-first and prune beyond the history limit. Caller holds <see cref="_gate"/>.</summary>
    private void InsertEntry(ClipEntry entry)
    {
        _board.Insert(0, entry);
        while (_board.Count > Math.Max(1, _config.HistoryLimit))
        {
            var last = _board[^1];
            _board.RemoveAt(_board.Count - 1);
            _seenIds.Remove(last.Id);
        }
    }

    private void BroadcastEntry(ClipEntry entry, ClipLink? except)
    {
        foreach (var l in Links)
            if (!ReferenceEquals(l, except))
                _ = SafeSend(l, new ClipWire { Kind = ClipWireKind.Entry, Entry = entry });
    }

    private async Task SafeSend(ClipLink link, ClipWire wire)
    {
        try { await link.SendAsync(wire); }
        catch (Exception ex) { Log?.Invoke($"向「{link.PeerName}」发送失败：{ex.Message}"); }
    }

    private void ApplyToLocalClipboard(ClipEntry entry)
    {
        var app = Application.Current;
        if (app == null) return;
        app.Dispatcher.Invoke(() =>
        {
            try
            {
                _monitor.Suppress(entry.ContentHash());   // don't re-capture & re-broadcast what we just set
                if (entry.Kind == ClipKind.Text && entry.Text != null) System.Windows.Clipboard.SetText(entry.Text);
                else if (entry.Kind == ClipKind.Image && entry.Image != null) System.Windows.Clipboard.SetImage(DecodeImage(entry.Image));
            }
            catch (Exception ex) { Log?.Invoke($"写入本机剪贴板失败：{ex.Message}"); }
        });
    }

    private static System.Windows.Media.Imaging.BitmapImage DecodeImage(byte[] png)
    {
        var bmp = new System.Windows.Media.Imaging.BitmapImage();
        bmp.BeginInit();
        bmp.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
        bmp.StreamSource = new System.IO.MemoryStream(png);
        bmp.EndInit();
        bmp.Freeze();
        return bmp;
    }

    private void PersistIfEnabled()
    {
        if (_config.PersistHistory) ClipConfigStore.SaveHistory(Board);
    }

    private string CapacityMessage()
        => $"开源版最多 {MaxPeers} 台设备共享（已连接 {Links.Count}/{MaxLinks}）。付费版可经服务器中转解除上限。";

    // ── settings ──────────────────────────────────────────────────────────────────────────────────────
    /// <summary>Pin discovery to a single interface IP (or "" = all) and restart ONLY discovery so it applies
    /// immediately, keeping existing links + the clipboard monitor running. Persisted for next launch.</summary>
    public void SetDiscoveryInterface(string? ip)
    {
        var cfg = Running ? _config : ClipConfigStore.Load();
        cfg.DiscoveryInterface = ip ?? "";
        ClipConfigStore.Save(cfg);
        if (!Running) return;
        _config = cfg;
        _discovery.Stop();
        _discovery.Start(_config, _config.InstanceId, WinDeploy.App.AppInfo.Version);
    }

    /// <summary>Apply edited settings live where possible (name, auto-apply, persistence, image cap) and
    /// persist them. Port / discovery-port changes take effect on the next start (returns true if a restart
    /// is needed to fully apply).</summary>
    public bool UpdateConfig(ClipSyncConfig edited)
    {
        var restartNeeded = Running && (edited.Port != _config.Port || edited.DiscoveryPort != _config.DiscoveryPort);
        edited.InstanceId = string.IsNullOrWhiteSpace(_config.InstanceId) ? Guid.NewGuid().ToString("N") : _config.InstanceId;
        var wasPersisting = _config.PersistHistory;
        _config = edited;
        ClipConfigStore.Save(_config);

        _monitor.MaxImageBytes = _config.MaxImageBytes;
        _discovery.UpdateName(_config.DeviceName);
        if (wasPersisting && !_config.PersistHistory) ClipConfigStore.ClearHistory();
        else if (_config.PersistHistory) PersistIfEnabled();
        return restartNeeded;
    }

    public void Dispose() => Stop();
}
