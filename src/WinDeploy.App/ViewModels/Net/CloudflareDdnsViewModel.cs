using System.Collections.ObjectModel;
using System.Windows;
using WinDeploy.App;
using WinDeploy.App.Services;
using WinDeploy.Core.I18n;

namespace WinDeploy.App.ViewModels.Net;

/// <summary>One zone (domain) row for the picker.</summary>
public sealed class CfZoneItem
{
    public CfZone Z { get; }
    public CfZoneItem(CfZone z) => Z = z;
    public string Id => Z.Id;
    public string Name => Z.Name;
    public string Display => Z.Status is "active" or "" ? Z.Name : $"{Z.Name}（{Z.Status}）";
    // The themed ComboBox template renders the closed selection box from SelectionBoxItem (no generated
    // SelectionBoxItemTemplate), so it falls back to ToString() — return the domain name, not the type name.
    public override string ToString() => Display;
}

/// <summary>One DNS record card, with its current DDNS-bound state.</summary>
public sealed class CfRecordItem : ObservableObject
{
    public CfDnsRecord R { get; private set; }
    public CfRecordItem(CfDnsRecord r, bool bound) { R = r; _bound = bound; }

    public string Type => R.Type;
    public string Name => R.Name;
    public string Content => R.Content;
    public bool Proxied => R.Proxied;
    public string TtlText => R.Ttl <= 1 ? Localizer.T("common.auto") : Localizer.Format("cloud.ttl.seconds", R.Ttl);
    public bool IsDdnsEligible => R.Type is "A" or "AAAA";

    private bool _bound;
    public bool IsBound { get => _bound; set { if (Set(ref _bound, value)) OnPropertyChanged(nameof(BindLabel)); } }
    public string BindLabel => _bound ? Localizer.T("cloud.ddns.unbind") : Localizer.T("cloud.ddns.bind");
}

/// <summary>One active DDNS binding row (with its last-applied IP + enable toggle).</summary>
public sealed class DdnsBindingItem : ObservableObject
{
    public DdnsBinding B { get; }
    public DdnsBindingItem(DdnsBinding b) { B = b; _enabled = b.Enabled; }

    public string RecordName => B.RecordName;
    public string Type => B.Type;
    public string ZoneName => B.ZoneName;
    public string LastIpText => string.IsNullOrEmpty(B.LastIp) ? Localizer.T("cloud.section.notApplied") : B.LastIp!;
    public string LastUpdateText =>
        string.IsNullOrEmpty(B.LastUpdate) ? "" :
        DateTime.TryParse(B.LastUpdate, out var d) ? d.ToString("MM-dd HH:mm:ss") : B.LastUpdate!;

    private bool _enabled;
    public bool Enabled { get => _enabled; set { if (Set(ref _enabled, value)) { B.Enabled = value; EnabledChanged?.Invoke(); } } }
    public event Action? EnabledChanged;

    public void RefreshState() { OnPropertyChanged(nameof(LastIpText)); OnPropertyChanged(nameof(LastUpdateText)); }
}

/// <summary>The 「Cloudflare DDNS」 page (开发人员模式): saves a scoped API token (DPAPI-encrypted), lists the
/// account's zones and DNS records, creates / edits records, and binds A/AAAA records to a background monitor
/// that keeps them pointed at this device's public IP. Dev-only.</summary>
public sealed class CloudflareDdnsViewModel : LocalizedObject
{
    private readonly CloudflareDdnsMonitor _monitor = new();
    private CloudflareConfig _cfg;

    public ObservableCollection<CfZoneItem> Zones { get; } = new();
    public ObservableCollection<CfRecordItem> Records { get; } = new();
    public ObservableCollection<DdnsBindingItem> Bindings { get; } = new();

    public RelayCommand VerifyTokenCommand { get; }
    public RelayCommand RefreshZonesCommand { get; }
    public RelayCommand RefreshRecordsCommand { get; }
    public RelayCommand NewRecordCommand { get; }
    public RelayCommand EditRecordCommand { get; }
    public RelayCommand UpdateToCurrentIpCommand { get; }
    public RelayCommand ToggleBindCommand { get; }
    public RelayCommand RemoveBindingCommand { get; }
    public RelayCommand StartMonitorCommand { get; }
    public RelayCommand StopMonitorCommand { get; }
    public RelayCommand RunOnceCommand { get; }
    public RelayCommand OpenTokenHelpCommand { get; }
    public RelayCommand ShowPermsCommand { get; }

    public CloudflareDdnsViewModel()
    {
        _cfg = CloudflareConfigStore.Load();
        _token = CloudflareConfigStore.LoadToken();
        _email = _cfg.Email ?? "";
        _useGlobalKey = !string.IsNullOrWhiteSpace(_cfg.Email);   // a saved email means the last credential was a Global API Key
        _intervalText = _cfg.IntervalSeconds.ToString();
        _autoStart = _cfg.AutoStart;

        VerifyTokenCommand = new RelayCommand(_ => _ = VerifyAndLoadAsync(), _ => !_busy);
        RefreshZonesCommand = new RelayCommand(_ => _ = LoadZonesAsync(), _ => HasToken && !_busy);
        RefreshRecordsCommand = new RelayCommand(_ => _ = LoadRecordsAsync(), _ => SelectedZone != null && !_busy);
        NewRecordCommand = new RelayCommand(_ => _ = NewRecordAsync(), _ => SelectedZone != null && !_busy);
        EditRecordCommand = new RelayCommand(p => { if (p is CfRecordItem r) _ = EditRecordAsync(r); }, _ => !_busy);
        UpdateToCurrentIpCommand = new RelayCommand(p => { if (p is CfRecordItem r) _ = UpdateToCurrentIpAsync(r); }, _ => !_busy);
        ToggleBindCommand = new RelayCommand(p => { if (p is CfRecordItem r) ToggleBind(r); });
        RemoveBindingCommand = new RelayCommand(p => { if (p is DdnsBindingItem b) RemoveBinding(b); });
        StartMonitorCommand = new RelayCommand(_ => StartMonitor(), _ => !MonitorRunning);
        StopMonitorCommand = new RelayCommand(_ => StopMonitor(), _ => MonitorRunning);
        RunOnceCommand = new RelayCommand(_ => _ = RunOnceAsync(), _ => !_busy);
        OpenTokenHelpCommand = new RelayCommand(_ => Open("https://dash.cloudflare.com/profile/api-tokens"));
        ShowPermsCommand = new RelayCommand(_ => ShowPerms());

        _monitor.Changed += OnMonitorChanged;
        _monitor.Updated += (title, body) => ToastService.TryShow(title, body);

        LoadBindings();

        // Resident auto-start: pick up monitoring on launch if configured and ready (independent of the page
        // ever being opened — that's the "常驻" behaviour).
        if (_cfg.AutoStart && _token.Length > 0 && _cfg.Bindings.Any(b => b.Enabled))
            _monitor.Start();
    }

    private bool _loadedOnce;
    /// <summary>Page opened — lazily fetch the zone list once (avoids a network call at every app launch when
    /// the page is never visited). Called from the view's Loaded.</summary>
    public void Activate()
    {
        if (_loadedOnce || !HasToken) return;
        _loadedOnce = true;
        _ = LoadZonesAsync();
    }

    /// <summary>On language switch, refresh all bound props plus the localized text on each record / binding
    /// row VM (their <c>TtlText</c> / <c>BindLabel</c> / <c>LastIpText</c> read <see cref="Localizer"/>).</summary>
    protected override void OnCultureChanged()
    {
        base.OnCultureChanged();
        foreach (var r in Records) r.RaiseAllPropertiesChanged();
        foreach (var b in Bindings) b.RaiseAllPropertiesChanged();
    }

    // ── credential ─────────────────────────────────────────────────────────────────
    private string _token;
    /// <summary>The API token (or Global API Key). Mirrored to/from the view's PasswordBox (the box pushes here
    /// on change; the view reads this to pre-fill the decrypted value on load).</summary>
    public string Token
    {
        get => _token;
        set { if (Set(ref _token, value ?? "")) { OnPropertyChanged(nameof(HasToken)); TokenStatus = ""; } }
    }
    public bool HasToken => !string.IsNullOrWhiteSpace(_token);

    // ── auth mode (explicit, so a token is never accidentally sent as a Global API Key) ──────────────
    private bool _useGlobalKey;
    /// <summary>True → 全局 API Key 模式（X-Auth-Email / X-Auth-Key，需邮箱）；false → API 令牌模式（Bearer，推荐）。</summary>
    public bool UseGlobalKey
    {
        get => _useGlobalKey;
        set { if (Set(ref _useGlobalKey, value)) { OnPropertyChanged(nameof(UseApiToken)); OnPropertyChanged(nameof(ShowEmail)); OnPropertyChanged(nameof(TokenLabel)); TokenStatus = ""; } }
    }
    public bool UseApiToken { get => !_useGlobalKey; set { if (value) UseGlobalKey = false; } }
    /// <summary>Email row is only relevant (and only sent) in Global API Key mode.</summary>
    public bool ShowEmail => _useGlobalKey;
    public string TokenLabel => _useGlobalKey ? Localizer.T("cloud.auth.globalKeyLabel") : Localizer.T("cloud.auth.tokenLabel");

    private string _email;
    /// <summary>Account email — used ONLY in Global API Key mode (X-Auth-Email).</summary>
    public string Email
    {
        get => _email;
        set { if (Set(ref _email, value ?? "")) TokenStatus = ""; }
    }

    private string _tokenStatus = "";
    public string TokenStatus { get => _tokenStatus; set => Set(ref _tokenStatus, value); }

    private bool _tokenOk;
    public bool TokenOk { get => _tokenOk; set => Set(ref _tokenOk, value); }

    /// <summary>A client for the current credential. Email (Global API Key auth) is passed ONLY in Global-Key
    /// mode; in API-Token mode it's omitted so the token always goes out as a Bearer header.</summary>
    private CloudflareClient Client() => new((Token ?? "").Trim(), _useGlobalKey ? (Email ?? "").Trim() : null);

    private async Task VerifyAndLoadAsync()
    {
        var token = (Token ?? "").Trim();
        var email = _useGlobalKey ? (Email ?? "").Trim() : "";   // token mode → clear any saved email
        CloudflareConfigStore.SaveCredential(token, email);
        _cfg = CloudflareConfigStore.Load();
        if (token.Length == 0) { TokenStatus = _useGlobalKey ? Localizer.T("cloud.token.invalidEmptyKey") : Localizer.T("cloud.token.invalidEmpty"); TokenOk = false; return; }
        if (_useGlobalKey && email.Length == 0) { TokenStatus = Localizer.T("cloud.token.needEmail"); TokenOk = false; return; }

        TokenStatus = Localizer.T("cloud.token.verifying");
        var v = await Client().VerifyAsync();

        // The real test is whether the credential can do the job — list zones. A correctly-scoped DDNS token
        // (Zone:Read + DNS:Edit, no User scope) is rejected by /user/tokens/verify (#1000) yet reads zones
        // perfectly, so treat a successful zones fetch as valid even when the verify endpoint says otherwise.
        var zonesOk = await LoadZonesAsync();

        if (v.Valid || zonesOk)
        {
            TokenOk = true;
            TokenStatus = v.Valid
                ? Localizer.T("cloud.token.statusValid")
                : Localizer.Format("cloud.token.statusUsable", Zones.Count);
            AuditLog.Action("Cloudflare 凭证：" + (v.Valid ? "验证有效" : "verify 未过但可读取域名，按可用处理"));
        }
        else
        {
            TokenOk = false;
            TokenStatus = InvalidHint(v.Error ?? v.Status);
            AuditLog.Action("Cloudflare 凭证验证：无效 — " + (v.Error ?? v.Status));
        }
    }

    /// <summary>Turn Cloudflare's terse failure into an actionable hint. #1000 = credential reached Cloudflare
    /// well-formed but was rejected (wrong value / type); #6003 = a header is malformed (usually a token sent
    /// as a Global API Key, or vice-versa).</summary>
    private string InvalidHint(string error)
    {
        var msg = Localizer.Format("cloud.token.invalidFail", error);
        if (error.Contains("6003") || error.Contains("Invalid request headers", StringComparison.OrdinalIgnoreCase)
            || error.Contains("Invalid format", StringComparison.OrdinalIgnoreCase))
            msg += Localizer.T("cloud.token.invalidHint6003");
        else if (error.Contains("1000") || error.Contains("Invalid API Token", StringComparison.OrdinalIgnoreCase))
            msg += Localizer.T("cloud.token.invalidHint1000");
        else if (error.Contains("9103") || error.Contains("Unknown X-Auth", StringComparison.OrdinalIgnoreCase))
            msg += Localizer.T("cloud.token.invalidHint9103");
        return msg;
    }

    // ── zones / records ────────────────────────────────────────────────────────────
    private bool _busy;
    private void SetBusy(bool b) { _busy = b; System.Windows.Input.CommandManager.InvalidateRequerySuggested(); }

    private CfZoneItem? _selectedZone;
    public CfZoneItem? SelectedZone
    {
        get => _selectedZone;
        set { if (Set(ref _selectedZone, value)) { System.Windows.Input.CommandManager.InvalidateRequerySuggested(); _ = LoadRecordsAsync(); } }
    }

    private string _note = "";
    public string Note { get => _note; set => Set(ref _note, value); }

    /// <summary>Fetch the account's zones into the picker. Returns true on success — this doubles as the real
    /// proof a credential works (a correctly-scoped DDNS token can read zones even when /user/tokens/verify
    /// denies it).</summary>
    private async Task<bool> LoadZonesAsync()
    {
        var token = (Token ?? "").Trim();
        if (token.Length == 0) { Note = Localizer.T("cloud.zones.needVerify"); return false; }
        SetBusy(true);
        Note = Localizer.T("cloud.zones.fetching");
        try
        {
            var zones = await Client().ListZonesAsync();
            var keepId = _selectedZone?.Id;
            Zones.Clear();
            foreach (var z in zones) Zones.Add(new CfZoneItem(z));
            Note = zones.Count == 0 ? Localizer.T("cloud.results.zonesEmpty") : Localizer.Format("cloud.results.zoneCount", zones.Count);
            var restore = Zones.FirstOrDefault(z => z.Id == keepId) ?? Zones.FirstOrDefault();
            if (restore != null) SelectedZone = restore;
            else { Records.Clear(); }
            return true;
        }
        catch (Exception ex) { Note = Localizer.Format("cloud.zones.fetchFailed", ex.Message); return false; }
        finally { SetBusy(false); }
    }

    private async Task LoadRecordsAsync()
    {
        var z = SelectedZone;
        var token = (Token ?? "").Trim();
        Records.Clear();
        if (z == null || token.Length == 0) return;
        SetBusy(true);
        Note = Localizer.Format("cloud.zones.recordsFetching", z.Name);
        try
        {
            var recs = await Client().ListRecordsAsync(z.Id);
            var boundIds = _cfg.Bindings.Select(b => b.RecordId).ToHashSet();
            Records.Clear();
            foreach (var r in recs) Records.Add(new CfRecordItem(r, boundIds.Contains(r.Id)));
            Note = Localizer.Format("cloud.results.zoneRecords", z.Name, recs.Count);
        }
        catch (Exception ex) { Note = Localizer.Format("cloud.zones.recordsFetchFailed", ex.Message); }
        finally { SetBusy(false); }
    }

    private async Task NewRecordAsync()
    {
        var z = SelectedZone;
        if (z == null) { Note = Localizer.T("cloud.zones.needSelect"); return; }
        var dlg = new CloudflareRecordDialog(z.Name, prefillIp: _monitor.CurrentIpv4) { Owner = Application.Current.MainWindow };
        if (dlg.ShowDialog() != true) return;

        SetBusy(true);
        try
        {
            var (ok, msg, _) = await Client()
                .CreateRecordAsync(z.Id, dlg.RecordType, dlg.FullName(), dlg.RecordContent, dlg.Proxied, dlg.Ttl);
            Note = msg;
            if (ok) { AuditLog.Action($"Cloudflare 新建解析：{dlg.FullName()} {dlg.RecordType} → {dlg.RecordContent}"); await LoadRecordsAsync(); }
            else Dialogs.Show(msg, Localizer.T("cloud.record.createFailed"), MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally { SetBusy(false); }
    }

    private async Task EditRecordAsync(CfRecordItem item)
    {
        var z = SelectedZone;
        if (z == null) return;
        var dlg = new CloudflareRecordDialog(z.Name, item.R) { Owner = Application.Current.MainWindow };
        if (dlg.ShowDialog() != true) return;

        SetBusy(true);
        try
        {
            var (ok, msg) = await Client()
                .UpdateRecordAsync(z.Id, item.R.Id, dlg.RecordType, dlg.FullName(), dlg.RecordContent, dlg.Proxied, dlg.Ttl);
            Note = msg;
            if (ok) { AuditLog.Action($"Cloudflare 修改解析：{dlg.FullName()} → {dlg.RecordContent}"); await LoadRecordsAsync(); }
            else Dialogs.Show(msg, Localizer.T("cloud.record.updateFailed"), MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally { SetBusy(false); }
    }

    private async Task UpdateToCurrentIpAsync(CfRecordItem item)
    {
        var z = SelectedZone;
        if (z == null) return;
        if (!item.IsDdnsEligible) { Note = Localizer.T("cloud.ip.eligibleOnly"); return; }

        SetBusy(true);
        try
        {
            var v6 = item.R.Type == "AAAA";
            Note = Localizer.Format("cloud.ip.fetching", v6 ? "IPv6" : "IPv4");
            var ip = await PublicIp.GetAsync(v6);
            if (ip == null) { Note = Localizer.Format("cloud.ip.failed", v6 ? "IPv6" : "IPv4"); return; }

            var (ok, msg) = await Client()
                .UpdateRecordAsync(z.Id, item.R.Id, item.R.Type, item.R.Name, ip, item.R.Proxied, item.R.Ttl);
            Note = ok ? Localizer.Format("cloud.ip.pointed", item.R.Name, ip) : Localizer.Format("cloud.ip.updateFailed", msg);
            if (ok)
            {
                AuditLog.Action($"Cloudflare 解析指向本机IP：{item.R.Name} → {ip}");
                // If this record is DDNS-bound, remember the applied IP so the monitor won't redo it.
                CloudflareConfigStore.ApplyResults(new[] { (item.R.Id, ip, DateTime.Now.ToString("s")) });
                _cfg = CloudflareConfigStore.Load();
                RefreshBindingState();
                await LoadRecordsAsync();
            }
        }
        finally { SetBusy(false); }
    }

    // ── DDNS bindings ────────────────────────────────────────────────────────────
    private void ToggleBind(CfRecordItem item)
    {
        if (!item.IsDdnsEligible) { Note = Localizer.T("cloud.ddns.eligibleOnly"); return; }
        var z = SelectedZone;
        if (z == null) return;

        if (_cfg.Bindings.Any(b => b.RecordId == item.R.Id))
        {
            CloudflareConfigStore.RemoveBinding(item.R.Id);
            item.IsBound = false;
            Note = Localizer.Format("cloud.ddns.boundRemoved", item.R.Name);
        }
        else
        {
            CloudflareConfigStore.AddBinding(new DdnsBinding
            {
                ZoneId = z.Id, ZoneName = z.Name, RecordId = item.R.Id, RecordName = item.R.Name,
                Type = item.R.Type, Proxied = item.R.Proxied, Ttl = item.R.Ttl, Enabled = true, LastIp = item.R.Content,
            });
            item.IsBound = true;
            Note = Localizer.Format("cloud.ddns.bound", item.R.Name, item.R.Type);
        }
        _cfg = CloudflareConfigStore.Load();
        LoadBindings();
    }

    private void RemoveBinding(DdnsBindingItem item)
    {
        CloudflareConfigStore.RemoveBinding(item.B.RecordId);
        _cfg = CloudflareConfigStore.Load();
        LoadBindings();
        // Reflect on any matching record card currently shown.
        foreach (var r in Records.Where(r => r.R.Id == item.B.RecordId)) r.IsBound = false;
        Note = Localizer.Format("cloud.ddns.removed", item.B.RecordName);
    }

    private void LoadBindings()
    {
        Bindings.Clear();
        foreach (var b in _cfg.Bindings)
        {
            var item = new DdnsBindingItem(b);
            var recordId = b.RecordId;
            item.EnabledChanged += () => CloudflareConfigStore.SetBindingEnabled(recordId, item.Enabled);
            Bindings.Add(item);
        }
        OnPropertyChanged(nameof(HasBindings));
        OnPropertyChanged(nameof(NoBindings));
    }

    private void RefreshBindingState()
    {
        var byId = _cfg.Bindings.ToDictionary(b => b.RecordId);
        foreach (var item in Bindings)
            if (byId.TryGetValue(item.B.RecordId, out var fresh))
            {
                item.B.LastIp = fresh.LastIp;
                item.B.LastUpdate = fresh.LastUpdate;
                item.RefreshState();
            }
    }

    public bool HasBindings => Bindings.Count > 0;
    public bool NoBindings => Bindings.Count == 0;

    // ── monitor control (also driven by the system-tray menu) ────────────────────
    private string _intervalText;
    public string IntervalText
    {
        get => _intervalText;
        set
        {
            if (!Set(ref _intervalText, value)) return;
            if (int.TryParse(value, out var n) && n >= 30) CloudflareConfigStore.SetInterval(n);
        }
    }

    private bool _autoStart;
    public bool AutoStart
    {
        get => _autoStart;
        set { if (Set(ref _autoStart, value)) CloudflareConfigStore.SetAutoStart(value); }
    }

    public bool MonitorRunning => _monitor.Running;
    public string StatusText => _monitor.Running ? Localizer.T("cloud.monitor.running") : Localizer.T("cloud.monitor.stopped");
    public string LastResultText => _monitor.LastResult;
    public string CurrentIpText
    {
        get
        {
            var v4 = _monitor.CurrentIpv4;
            var v6 = _monitor.CurrentIpv6;
            // The last-check time rides right after the IP, e.g. "IPv4 1.2.3.4（最近检查: 21:41:31）".
            var when = _monitor.LastCheck is DateTime t ? Localizer.Format("cloud.monitor.lastCheck", t.ToString("HH:mm:ss")) : "";
            if (v4 == null && v6 == null)
                return Localizer.T("cloud.ip.unknown") + when;
            var bits = new List<string>();
            if (v4 != null) bits.Add("IPv4 " + v4);
            if (v6 != null) bits.Add("IPv6 " + v6);
            return Localizer.T("cloud.ip.publicLabel") + string.Join("   ", bits) + when;
        }
    }

    /// <summary>One-line status for the tray submenu.</summary>
    public string TrayStatusLine => _monitor.Running
        ? Localizer.Format("cloud.monitor.trayRunning", _monitor.CurrentIpv4 ?? _monitor.CurrentIpv6 ?? Localizer.T("cloud.ip.unknownShort"))
        : Localizer.T("cloud.monitor.stopped");

    public void StartMonitor()
    {
        if (CloudflareConfigStore.LoadToken().Length == 0) { Note = Localizer.T("cloud.monitor.needToken"); return; }
        if (!CloudflareConfigStore.Load().Bindings.Any(b => b.Enabled))
        { Note = Localizer.T("cloud.monitor.needBindings"); return; }
        _monitor.Start();
        AuditLog.Action("Cloudflare DDNS 监听已启动");
    }

    public void StopMonitor()
    {
        _monitor.Stop();
        AuditLog.Action("Cloudflare DDNS 监听已停止");
    }

    private async Task RunOnceAsync()
    {
        SetBusy(true);
        Note = Localizer.T("cloud.monitor.checking");
        try { Note = await _monitor.RunOnceAsync(); }
        finally { SetBusy(false); }
    }

    /// <summary>Fire-and-forget immediate check from the tray.</summary>
    public void RunOnceFromTray() => _ = _monitor.RunOnceAsync();

    private void OnMonitorChanged()
    {
        var disp = Application.Current?.Dispatcher;
        if (disp == null) return;
        disp.BeginInvoke(() =>
        {
            OnPropertyChanged(nameof(MonitorRunning));
            OnPropertyChanged(nameof(StatusText));
            OnPropertyChanged(nameof(LastResultText));
            OnPropertyChanged(nameof(CurrentIpText));
            OnPropertyChanged(nameof(TrayStatusLine));
            _cfg = CloudflareConfigStore.Load();
            RefreshBindingState();
            System.Windows.Input.CommandManager.InvalidateRequerySuggested();
        });
    }

    /// <summary>App is closing — stop the resident monitor.</summary>
    public void Shutdown() => _monitor.Stop();

    /// <summary>Open the themed "required permissions" help dialog.</summary>
    private static void ShowPerms()
        => new CloudflarePermsDialog { Owner = Application.Current.MainWindow }.ShowDialog();

    private static void Open(string url)
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }); }
        catch { /* ignore */ }
    }
}
