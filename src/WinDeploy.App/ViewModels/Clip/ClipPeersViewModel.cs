using System.Collections.ObjectModel;
using System.Net;
using System.Net.Sockets;
using System.Windows;
using System.Windows.Threading;
using WinDeploy.App.Services.Clip;
using WinDeploy.Core.I18n;

namespace WinDeploy.App.ViewModels.Clip;

/// <summary>One discovered LAN device row, with an 邀请 action (disabled once it is already shared).</summary>
public sealed class ClipPeerRowVm : ObservableObject
{
    private readonly ClipPeer _peer;
    public ClipPeerRowVm(ClipPeer peer, bool linked, Action<ClipPeer> invite)
    {
        _peer = peer;
        IsLinked = linked;
        InviteCommand = new RelayCommand(_ => invite(_peer), _ => !IsLinked);
    }
    public ClipPeer Peer => _peer;
    public string DeviceName => _peer.DeviceName;
    public string Address => _peer.Address;
    public string Version => string.IsNullOrWhiteSpace(_peer.Version) ? "—" : "v" + _peer.Version;
    public bool IsLinked { get; }
    public string ActionText => IsLinked ? Localizer.T("clip.peers.linked") : Localizer.T("clip.peers.invite");
    public RelayCommand InviteCommand { get; }
}

/// <summary>One active (paired, encrypted) peer link row, with a 断开 action.</summary>
public sealed class ClipLinkRowVm : ObservableObject
{
    private readonly ClipLink _link;
    public ClipLinkRowVm(ClipLink link, Action<ClipLink> disconnect)
    {
        _link = link;
        DisconnectCommand = new RelayCommand(_ => disconnect(_link));
    }
    public string PeerName => _link.PeerName;
    public string Remote => _link.Remote;
    public string Role => _link.IsInitiator ? Localizer.T("clip.role.initiator") : Localizer.T("clip.role.joiner");
    public string SinceText
    {
        get
        {
            var d = DateTime.Now - _link.PairedAt;
            return d.TotalHours >= 1 ? $"{(int)d.TotalHours}h{d.Minutes}m" : d.TotalMinutes >= 1 ? $"{(int)d.TotalMinutes}m" : $"{(int)d.TotalSeconds}s";
        }
    }
    public void Refresh() => OnPropertyChanged(nameof(SinceText));
    public RelayCommand DisconnectCommand { get; }
}

/// <summary>One entry in the 监听网卡 picker: a specific interface IP, or "" = all NICs (auto).</summary>
public sealed class ClipNicVm
{
    public string Ip { get; }
    public string Label { get; }
    public ClipNicVm(string ip, string label) { Ip = ip; Label = label; }
}

/// <summary>设备与配对 tab: start/stop the share, see this device's identity + capacity, discover LAN peers
/// and invite them (showing a PIN), manage active links, and watch a rolling activity log.</summary>
public sealed class ClipPeersViewModel : LocalizedObject
{
    private readonly ClipSyncManager _manager;
    private readonly Queue<string> _logLines = new();
    private DispatcherTimer? _timer;
    private CancellationTokenSource? _inviteCts;

    public ClipPeersViewModel(ClipSyncManager manager)
    {
        _manager = manager;
        StartCommand = new RelayCommand(_ => Start(), _ => !Running);
        StopCommand = new RelayCommand(_ => Stop(), _ => Running);
        ClearLogCommand = new RelayCommand(_ => { _logLines.Clear(); LogText = ""; });
        CancelInviteCommand = new RelayCommand(_ => _inviteCts?.Cancel(), _ => InviteInProgress);
        ConnectManualCommand = new RelayCommand(_ => ConnectManual(), _ => Running && !InviteInProgress);
        _manualPort = _manager.Config.Port.ToString();
        LocalAddresses = string.Join("   ", LocalIPv4());
        LoadNics();
    }

    public ObservableCollection<ClipPeerRowVm> DiscoveredPeers { get; } = new();
    public ObservableCollection<ClipLinkRowVm> ActiveLinks { get; } = new();
    public ObservableCollection<ClipNicVm> Nics { get; } = new();

    public RelayCommand StartCommand { get; }
    public RelayCommand StopCommand { get; }
    public RelayCommand ClearLogCommand { get; }
    public RelayCommand CancelInviteCommand { get; }
    public RelayCommand ConnectManualCommand { get; }

    public bool Running => _manager.Running;
    public string StatusText => Running ? Localizer.T("clip.status.running") : Localizer.T("clip.status.stopped");
    public string SelfText => Localizer.Format("clip.peers.self", _manager.SelfName);
    public string CapacityText => Localizer.Format("clip.peers.capacity", _manager.Links.Count, _manager.MaxLinks, _manager.MaxPeers);
    public string LocalAddresses { get; }

    public bool NoPeers => DiscoveredPeers.Count == 0;
    public bool NoLinks => ActiveLinks.Count == 0;

    // ── pending invite banner ────────────────────────────────────────────────────────────────────────
    private bool _inviteInProgress;
    public bool InviteInProgress { get => _inviteInProgress; private set { if (Set(ref _inviteInProgress, value)) Requery(); } }
    private string _pendingPin = "";
    public string PendingPin { get => _pendingPin; private set => Set(ref _pendingPin, value); }
    private string _pendingPeer = "";
    public string PendingPeerText { get => _pendingPeer; private set => Set(ref _pendingPeer, value); }

    private string _logText = "";
    public string LogText { get => _logText; private set => Set(ref _logText, value); }

    // ── manual connect by IP (discovery fallback) ──────────────────────────────────────────────────────
    private string _manualIp = "";
    public string ManualIp { get => _manualIp; set => Set(ref _manualIp, value); }
    private string _manualPort = "";
    public string ManualPort { get => _manualPort; set => Set(ref _manualPort, value); }

    // ── listen-NIC picker ──────────────────────────────────────────────────────────────────────────────
    private ClipNicVm? _selectedNic;
    public ClipNicVm? SelectedNic
    {
        get => _selectedNic;
        set { if (Set(ref _selectedNic, value) && value != null) _manager.SetDiscoveryInterface(value.Ip); }
    }

    // ── start / stop ───────────────────────────────────────────────────────────────────────────────────
    private void Start()
    {
        try
        {
            _manager.Start();
            AuditLog.Action($"剪贴板共享启动 · 设备「{_manager.SelfName}」· 端口 {_manager.Config.Port}");
            RefreshAll();
        }
        catch (Exception ex)
        {
            AuditLog.Action("剪贴板共享启动失败：" + ex.Message);
            Dialogs.Show(Localizer.Format("clip.peers.startFailed", ex.Message), Localizer.T("clip.page.title"), MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void Stop()
    {
        _inviteCts?.Cancel();
        _manager.Stop();
        AuditLog.Action("剪贴板共享停止");
        RefreshAll();
    }

    // ── manual connect ─────────────────────────────────────────────────────────────────────────────────
    /// <summary>Discovery fallback: pair directly with a peer's LAN IP (read off its 本机 line). The PIN
    /// handshake is identical to an invite; the real peer id/name are learned during it.</summary>
    private void ConnectManual()
    {
        if (!Running) { Dialogs.Show(Localizer.T("clip.peers.startFirst"), Localizer.T("clip.page.title"), MessageBoxButton.OK, MessageBoxImage.Information); return; }
        var ip = ManualIp.Trim();
        if (!IPAddress.TryParse(ip, out _)) { Dialogs.Show(Localizer.T("clip.peers.manualBadIp"), Localizer.T("clip.peers.manualConnect"), MessageBoxButton.OK, MessageBoxImage.Warning); return; }
        if (!int.TryParse(ManualPort.Trim(), out var port) || port is < 1 or > 65535) port = _manager.Config.Port;
        Invite(new ClipPeer { InstanceId = "", DeviceName = ip, Address = ip, Port = port });
    }

    // ── invite ───────────────────────────────────────────────────────────────────────────────────────
    private async void Invite(ClipPeer peer)
    {
        if (!Running) { Dialogs.Show(Localizer.T("clip.peers.startFirst"), Localizer.T("clip.page.title"), MessageBoxButton.OK, MessageBoxImage.Information); return; }
        if (_manager.AtCapacity) { Dialogs.Show(Localizer.T("clip.peers.atCapacity"), Localizer.T("clip.page.title"), MessageBoxButton.OK, MessageBoxImage.Information); return; }
        if (InviteInProgress) return;

        var pin = ClipCrypto.NewPin();
        PendingPin = pin;
        PendingPeerText = Localizer.Format("clip.peers.inviteWaiting", peer.DeviceName);
        InviteInProgress = true;
        _inviteCts = new CancellationTokenSource();
        _inviteCts.CancelAfter(TimeSpan.FromMinutes(3));
        try
        {
            await _manager.InviteAsync(peer, pin, _inviteCts.Token);
            ToastService.TryShow(Localizer.T("clip.page.title"), Localizer.Format("clip.peers.inviteOk", peer.DeviceName));
        }
        catch (OperationCanceledException) { AuditLog.Action($"剪贴板配对取消/超时：{peer.DeviceName}"); }
        catch (Exception ex) { Dialogs.Show(ex.Message, Localizer.T("clip.peers.invite"), MessageBoxButton.OK, MessageBoxImage.Warning); }
        finally
        {
            InviteInProgress = false;
            PendingPin = "";
            PendingPeerText = "";
            _inviteCts?.Dispose();
            _inviteCts = null;
            RefreshPeers();
            RefreshLinks();
        }
    }

    private void Disconnect(ClipLink link)
    {
        _manager.Disconnect(link);
        AuditLog.Action($"剪贴板共享断开：{link.PeerName}");
    }

    // ── refresh ───────────────────────────────────────────────────────────────────────────────────────
    public void RefreshAll() { RefreshPeers(); RefreshLinks(); Requery(); OnPropertyChanged(nameof(Running)); OnPropertyChanged(nameof(StatusText)); OnPropertyChanged(nameof(SelfText)); }

    public void RefreshPeers()
    {
        var linkedIds = _manager.Links.Select(l => l.PeerId).ToHashSet(StringComparer.Ordinal);
        DiscoveredPeers.Clear();
        foreach (var p in _manager.Peers) DiscoveredPeers.Add(new ClipPeerRowVm(p, linkedIds.Contains(p.InstanceId), Invite));
        OnPropertyChanged(nameof(NoPeers));
    }

    public void RefreshLinks()
    {
        ActiveLinks.Clear();
        foreach (var l in _manager.Links) ActiveLinks.Add(new ClipLinkRowVm(l, Disconnect));
        OnPropertyChanged(nameof(NoLinks));
        OnPropertyChanged(nameof(CapacityText));
        RefreshPeers();   // an invite changed who is 共享中
    }

    public void AppendLog(string line)
    {
        _logLines.Enqueue($"{DateTime.Now:HH:mm:ss}  {line}");
        while (_logLines.Count > 400) _logLines.Dequeue();
        LogText = string.Join("\n", _logLines);
    }

    // ── live timer ───────────────────────────────────────────────────────────────────────────────────
    public void StartLive()
    {
        _timer ??= new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick -= OnTick;
        _timer.Tick += OnTick;
        _timer.Start();
        RefreshAll();
    }

    public void StopLive() => _timer?.Stop();

    private void OnTick(object? s, EventArgs e)
    {
        foreach (var l in ActiveLinks) l.Refresh();
    }

    protected override void OnCultureChanged()
    {
        base.OnCultureChanged();
        RefreshPeers();
        RefreshLinks();
        LoadNics();
    }

    /// <summary>Populate the NIC picker (全部 + each up IPv4 interface) and select the persisted choice.</summary>
    public void LoadNics()
    {
        Nics.Clear();
        Nics.Add(new ClipNicVm("", Localizer.T("clip.peers.nicAll")));
        foreach (var (ip, name) in PeerDiscovery.ListInterfaces())
            Nics.Add(new ClipNicVm(ip, $"{ip}  ·  {name}"));
        var sel = ClipConfigStore.Load().DiscoveryInterface ?? "";
        _selectedNic = Nics.FirstOrDefault(n => n.Ip == sel) ?? Nics[0];
        OnPropertyChanged(nameof(SelectedNic));
    }

    private static void Requery() => System.Windows.Input.CommandManager.InvalidateRequerySuggested();

    private static IEnumerable<string> LocalIPv4()
    {
        var list = new List<string>();
        try
        {
            foreach (var ip in Dns.GetHostAddresses(Dns.GetHostName()))
                if (ip.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(ip))
                    list.Add(ip.ToString());
        }
        catch { /* best effort */ }
        if (list.Count == 0) list.Add("127.0.0.1");
        return list.Distinct();
    }
}
