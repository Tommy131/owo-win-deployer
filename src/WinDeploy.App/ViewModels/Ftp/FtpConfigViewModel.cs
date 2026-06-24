using System.Collections.ObjectModel;
using System.Windows;
using WinDeploy.App;
using WinDeploy.App.Services;
using WinDeploy.App.Services.Ftp;
using WinDeploy.Core.I18n;

namespace WinDeploy.App.ViewModels.Ftp;

/// <summary>Human-readable summaries for a <see cref="FtpPerm"/> flag set.</summary>
public static class FtpPermText
{
    public static string Summary(FtpPerm p)
    {
        if (p == FtpPerm.Full) return Localizer.T("ftp.perm.full");
        if (p == FtpPerm.ReadOnly) return Localizer.T("ftp.perm.readOnly");
        if (p == FtpPerm.None) return Localizer.T("ftp.perm.noPerm");
        var bits = new List<string>();
        if (p.HasFlag(FtpPerm.List)) bits.Add(Localizer.T("ftp.perm.sumList"));
        if (p.HasFlag(FtpPerm.Download)) bits.Add(Localizer.T("ftp.perm.download"));
        if (p.HasFlag(FtpPerm.Upload)) bits.Add(Localizer.T("ftp.perm.upload"));
        if (p.HasFlag(FtpPerm.Append)) bits.Add(Localizer.T("ftp.perm.sumAppend"));
        if (p.HasFlag(FtpPerm.Delete)) bits.Add(Localizer.T("ftp.perm.sumDelete"));
        if (p.HasFlag(FtpPerm.Rename)) bits.Add(Localizer.T("ftp.perm.rename"));
        if (p.HasFlag(FtpPerm.CreateDir)) bits.Add(Localizer.T("ftp.perm.sumCreateDir"));
        if (p.HasFlag(FtpPerm.DeleteDir)) bits.Add(Localizer.T("ftp.perm.sumDeleteDir"));
        return string.Join(" · ", bits);
    }
}

public sealed class FtpUserRow : ObservableObject
{
    public FtpUser Model { get; }
    public FtpUserRow(FtpUser m) { Model = m; }
    public string Name => Model.Name;
    public string Group => string.IsNullOrEmpty(Model.Group) ? "" : Localizer.Format("ftp.user.groupPrefix", Model.Group);
    public string Home => Model.Home;
    public bool Enabled => Model.Enabled;
    public string PermText => Model.UseGroupPermissions && !string.IsNullOrEmpty(Model.Group)
        ? Localizer.T("ftp.perm.inherit") : FtpPermText.Summary(Model.Permissions);
    public string StateText => Model.Enabled ? Localizer.T("ftp.user.stateEnabled") : Localizer.T("ftp.user.stateDisabled");
    public string StateBrush => Model.Enabled ? "OkFg" : "TextTertiary";
    public void Raise() { OnPropertyChanged(""); }
}

public sealed class FtpGroupRow : ObservableObject
{
    public FtpGroup Model { get; }
    public FtpGroupRow(FtpGroup m) { Model = m; }
    public string Name => Model.Name;
    public string Home => string.IsNullOrEmpty(Model.Home) ? Localizer.T("ftp.group.homeUnset") : Model.Home!;
    public string PermText => FtpPermText.Summary(Model.Permissions);
    public void Raise() { OnPropertyChanged(""); }
}

/// <summary>The 服务端配置 tab: scalar server parameters (ports, TLS, passive range, concurrency caps,
/// anonymous access) plus FileZilla-style user and group management. Holds the live config object; the
/// server-run tab starts from <see cref="Snapshot"/>, and <see cref="SaveCommand"/> persists to ftp.json.</summary>
public sealed class FtpConfigViewModel : LocalizedObject
{
    private readonly FtpServerConfig _cfg;

    public FtpConfigViewModel()
    {
        _cfg = FtpConfigStore.Load();

        _port = _cfg.Port;
        _listenAddress = _cfg.ListenAddress;
        _tlsMode = _cfg.TlsMode;
        _requireTls = _cfg.RequireTls;
        _implicitPort = _cfg.ImplicitPort;
        _certPath = _cfg.CertPath ?? "";
        _keyPath = _cfg.KeyPath ?? "";
        _certPassword = _cfg.CertPassword ?? "";
        _passiveMin = _cfg.PassiveMin;
        _passiveMax = _cfg.PassiveMax;
        _passiveExternalIp = _cfg.PassiveExternalIp ?? "";
        _maxConnections = _cfg.MaxConnections;
        _maxPerUser = _cfg.MaxConnectionsPerUser;
        _maxPerIp = _cfg.MaxConnectionsPerIp;
        _allowAnonymous = _cfg.AllowAnonymous;
        _anonymousHome = _cfg.AnonymousHome ?? "";
        _anonAllowUpload = _cfg.AnonymousPermissions.HasFlag(FtpPerm.Upload);

        foreach (var u in _cfg.Users) Users.Add(new FtpUserRow(u));
        foreach (var g in _cfg.Groups) Groups.Add(new FtpGroupRow(g));

        SaveCommand = new RelayCommand(_ => Save());
        BrowseCertCommand = new RelayCommand(_ => BrowseCert());
        BrowseKeyCommand = new RelayCommand(_ => BrowseKey());
        ClearCertCommand = new RelayCommand(_ => { CertPath = ""; KeyPath = ""; CertPassword = ""; });

        AddUserCommand = new RelayCommand(_ => AddUser());
        EditUserCommand = new RelayCommand(p => { if (p is FtpUserRow r) EditUser(r); });
        RemoveUserCommand = new RelayCommand(p => { if (p is FtpUserRow r) RemoveUser(r); });

        AddGroupCommand = new RelayCommand(_ => AddGroup());
        EditGroupCommand = new RelayCommand(p => { if (p is FtpGroupRow r) EditGroup(r); });
        RemoveGroupCommand = new RelayCommand(p => { if (p is FtpGroupRow r) RemoveGroup(r); });
    }

    public ObservableCollection<FtpUserRow> Users { get; } = new();
    public ObservableCollection<FtpGroupRow> Groups { get; } = new();
    public bool NoUsers => Users.Count == 0;
    public bool NoGroups => Groups.Count == 0;

    // ── scalar properties ────────────────────────────────────────────────────
    private int _port; public int Port { get => _port; set { if (Set(ref _port, value)) Touch(); } }
    private string _listenAddress; public string ListenAddress { get => _listenAddress; set { if (Set(ref _listenAddress, value)) Touch(); } }

    private string _tlsMode;
    public bool IsTlsNone { get => _tlsMode == "none"; set { if (value) SetTls("none"); } }
    public bool IsTlsExplicit { get => _tlsMode == "explicit"; set { if (value) SetTls("explicit"); } }
    public bool IsTlsImplicit { get => _tlsMode == "implicit"; set { if (value) SetTls("implicit"); } }
    private void SetTls(string m)
    {
        _tlsMode = m;
        OnPropertyChanged(nameof(IsTlsNone));
        OnPropertyChanged(nameof(IsTlsExplicit));
        OnPropertyChanged(nameof(IsTlsImplicit));
        OnPropertyChanged(nameof(TlsEnabled));
        Touch();
    }
    public bool TlsEnabled => _tlsMode != "none";

    private bool _requireTls; public bool RequireTls { get => _requireTls; set { if (Set(ref _requireTls, value)) Touch(); } }
    private int _implicitPort; public int ImplicitPort { get => _implicitPort; set { if (Set(ref _implicitPort, value)) Touch(); } }
    private string _certPath; public string CertPath { get => _certPath; set { if (Set(ref _certPath, value)) Touch(); } }
    private string _keyPath; public string KeyPath { get => _keyPath; set { if (Set(ref _keyPath, value)) Touch(); } }
    private string _certPassword; public string CertPassword { get => _certPassword; set { if (Set(ref _certPassword, value)) Touch(); } }

    private int _passiveMin; public int PassiveMin { get => _passiveMin; set { if (Set(ref _passiveMin, value)) Touch(); } }
    private int _passiveMax; public int PassiveMax { get => _passiveMax; set { if (Set(ref _passiveMax, value)) Touch(); } }
    private string _passiveExternalIp; public string PassiveExternalIp { get => _passiveExternalIp; set { if (Set(ref _passiveExternalIp, value)) Touch(); } }

    private int _maxConnections; public int MaxConnections { get => _maxConnections; set { if (Set(ref _maxConnections, value)) Touch(); } }
    private int _maxPerUser; public int MaxConnectionsPerUser { get => _maxPerUser; set { if (Set(ref _maxPerUser, value)) Touch(); } }
    private int _maxPerIp; public int MaxConnectionsPerIp { get => _maxPerIp; set { if (Set(ref _maxPerIp, value)) Touch(); } }

    private bool _allowAnonymous; public bool AllowAnonymous { get => _allowAnonymous; set { if (Set(ref _allowAnonymous, value)) Touch(); } }
    private string _anonymousHome; public string AnonymousHome { get => _anonymousHome; set { if (Set(ref _anonymousHome, value)) Touch(); } }
    private bool _anonAllowUpload; public bool AnonAllowUpload { get => _anonAllowUpload; set { if (Set(ref _anonAllowUpload, value)) Touch(); } }

    private string _note = Localizer.T("ftp.config.defaultNote");
    public string Note { get => _note; set => Set(ref _note, value); }

    private void Touch() { if (Note.Length > 0 && !Note.StartsWith("●")) Note = Localizer.T("ftp.config.dirtyNote"); }

    public RelayCommand SaveCommand { get; }
    public RelayCommand BrowseCertCommand { get; }
    public RelayCommand BrowseKeyCommand { get; }
    public RelayCommand ClearCertCommand { get; }
    public RelayCommand AddUserCommand { get; }
    public RelayCommand EditUserCommand { get; }
    public RelayCommand RemoveUserCommand { get; }
    public RelayCommand AddGroupCommand { get; }
    public RelayCommand EditGroupCommand { get; }
    public RelayCommand RemoveGroupCommand { get; }

    /// <summary>Flush the scalar UI fields into the backing config and return it (used by the run tab).</summary>
    public FtpServerConfig Snapshot()
    {
        _cfg.Port = Clamp(_port, 1, 65535, 21);
        _cfg.ListenAddress = string.IsNullOrWhiteSpace(_listenAddress) ? "0.0.0.0" : _listenAddress.Trim();
        _cfg.TlsMode = _tlsMode;
        _cfg.RequireTls = _requireTls;
        _cfg.ImplicitPort = Clamp(_implicitPort, 1, 65535, 990);
        _cfg.CertPath = string.IsNullOrWhiteSpace(_certPath) ? null : _certPath.Trim();
        _cfg.KeyPath = string.IsNullOrWhiteSpace(_keyPath) ? null : _keyPath.Trim();
        _cfg.CertPassword = string.IsNullOrEmpty(_certPassword) ? null : _certPassword;
        _cfg.PassiveMin = Clamp(_passiveMin, 1024, 65535, 50000);
        _cfg.PassiveMax = Clamp(_passiveMax, _cfg.PassiveMin, 65535, Math.Max(_cfg.PassiveMin, 50100));
        _cfg.PassiveExternalIp = string.IsNullOrWhiteSpace(_passiveExternalIp) ? null : _passiveExternalIp.Trim();
        _cfg.MaxConnections = Math.Max(0, _maxConnections);
        _cfg.MaxConnectionsPerUser = Math.Max(0, _maxPerUser);
        _cfg.MaxConnectionsPerIp = Math.Max(0, _maxPerIp);
        _cfg.AllowAnonymous = _allowAnonymous;
        _cfg.AnonymousHome = string.IsNullOrWhiteSpace(_anonymousHome) ? null : _anonymousHome.Trim();
        _cfg.AnonymousPermissions = _anonAllowUpload
            ? FtpPerm.List | FtpPerm.Download | FtpPerm.Upload | FtpPerm.Append | FtpPerm.CreateDir
            : FtpPerm.ReadOnly;
        // Users / Groups lists are mutated in place via the dialogs, already on _cfg.
        return _cfg;
    }

    private static int Clamp(int v, int min, int max, int fallback)
        => v < min || v > max ? Math.Clamp(fallback, min, max) : v;

    private void Save()
    {
        Snapshot();
        FtpConfigStore.Save(_cfg);
        AuditLog.Action($"FTP 配置已保存 · 端口 {_cfg.Port} · TLS {_cfg.TlsMode} · 用户 {_cfg.Users.Count} · 分组 {_cfg.Groups.Count}");
        Note = Localizer.Format("ftp.config.saved", FtpConfigStore.FilePath);
    }

    private void BrowseCert()
    {
        var d = new Microsoft.Win32.OpenFileDialog
        {
            Title = Localizer.T("ftp.config.browseCertTitle"),
            Filter = Localizer.T("ftp.config.browseCertFilter"),
        };
        if (d.ShowDialog() == true) CertPath = d.FileName;
    }

    private void BrowseKey()
    {
        var d = new Microsoft.Win32.OpenFileDialog
        {
            Title = Localizer.T("ftp.config.browseKeyTitle"),
            Filter = Localizer.T("ftp.config.browseKeyFilter"),
        };
        if (d.ShowDialog() == true) KeyPath = d.FileName;
    }

    // ── users ─────────────────────────────────────────────────────────────────
    private void AddUser()
    {
        var dlg = new FtpUserDialog(null, GroupNames()) { Owner = Application.Current.MainWindow };
        if (dlg.ShowDialog() != true || dlg.Result == null) return;
        if (_cfg.Users.Any(u => u.Name.Equals(dlg.Result.Name, StringComparison.OrdinalIgnoreCase)))
        {
            Dialogs.Show(Localizer.T("ftp.user.addDuplicate"), Localizer.T("ftp.user.addTitle"), MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        _cfg.Users.Add(dlg.Result);
        Users.Add(new FtpUserRow(dlg.Result));
        OnPropertyChanged(nameof(NoUsers));
        Persist(Localizer.Format("ftp.config.userAdded", dlg.Result.Name), $"新增用户 {dlg.Result.Name}");
    }

    private void EditUser(FtpUserRow row)
    {
        var dlg = new FtpUserDialog(row.Model, GroupNames()) { Owner = Application.Current.MainWindow };
        if (dlg.ShowDialog() != true || dlg.Result == null) return;
        var idx = _cfg.Users.IndexOf(row.Model);
        if (idx >= 0) _cfg.Users[idx] = dlg.Result;
        var rIdx = Users.IndexOf(row);
        if (rIdx >= 0) Users[rIdx] = new FtpUserRow(dlg.Result);
        Persist(Localizer.Format("ftp.config.userEdited", dlg.Result.Name), $"编辑用户 {dlg.Result.Name}");
    }

    private void RemoveUser(FtpUserRow row)
    {
        if (Dialogs.Show(Localizer.Format("ftp.user.removeConfirm", row.Name), Localizer.T("ftp.user.removeTitle"), MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        _cfg.Users.Remove(row.Model);
        Users.Remove(row);
        OnPropertyChanged(nameof(NoUsers));
        Persist(Localizer.Format("ftp.config.userRemoved", row.Name), $"删除用户 {row.Name}");
    }

    // ── groups ────────────────────────────────────────────────────────────────
    private void AddGroup()
    {
        var dlg = new FtpGroupDialog(null) { Owner = Application.Current.MainWindow };
        if (dlg.ShowDialog() != true || dlg.Result == null) return;
        if (_cfg.Groups.Any(g => g.Name.Equals(dlg.Result.Name, StringComparison.OrdinalIgnoreCase)))
        {
            Dialogs.Show(Localizer.T("ftp.group.addDuplicate"), Localizer.T("ftp.group.addTitle"), MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        _cfg.Groups.Add(dlg.Result);
        Groups.Add(new FtpGroupRow(dlg.Result));
        OnPropertyChanged(nameof(NoGroups));
        Persist(Localizer.Format("ftp.config.groupAdded", dlg.Result.Name), $"新增分组 {dlg.Result.Name}");
    }

    private void EditGroup(FtpGroupRow row)
    {
        var old = row.Model.Name;
        var dlg = new FtpGroupDialog(row.Model) { Owner = Application.Current.MainWindow };
        if (dlg.ShowDialog() != true || dlg.Result == null) return;
        var idx = _cfg.Groups.IndexOf(row.Model);
        if (idx >= 0) _cfg.Groups[idx] = dlg.Result;
        var rIdx = Groups.IndexOf(row);
        if (rIdx >= 0) Groups[rIdx] = new FtpGroupRow(dlg.Result);
        // keep member references consistent if the group was renamed
        if (!old.Equals(dlg.Result.Name, StringComparison.Ordinal))
            foreach (var u in _cfg.Users.Where(u => u.Group == old)) u.Group = dlg.Result.Name;
        Persist(Localizer.Format("ftp.config.groupEdited", dlg.Result.Name), $"编辑分组 {dlg.Result.Name}");
    }

    private void RemoveGroup(FtpGroupRow row)
    {
        if (Dialogs.Show(Localizer.Format("ftp.group.removeConfirm", row.Name), Localizer.T("ftp.group.removeTitle"),
                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        _cfg.Groups.Remove(row.Model);
        Groups.Remove(row);
        foreach (var u in _cfg.Users.Where(u => u.Group == row.Name)) u.Group = null;
        for (var i = 0; i < Users.Count; i++) Users[i] = new FtpUserRow(Users[i].Model);   // refresh group column
        OnPropertyChanged(nameof(NoGroups));
        Persist(Localizer.Format("ftp.config.groupRemoved", row.Name), $"删除分组 {row.Name}");
    }

    private List<string> GroupNames() => _cfg.Groups.Select(g => g.Name).ToList();

    /// <summary>User/group edits are discrete actions — persist them immediately so they survive a restart
    /// even without pressing 保存配置 (which is for the scalar fields).</summary>
    private void Persist(string note, string auditAction)
    {
        Snapshot();
        FtpConfigStore.Save(_cfg);
        AuditLog.Action("FTP：" + auditAction);
        Note = Localizer.Format("ftp.config.savedNote", note);
    }

    protected override void OnCultureChanged()
    {
        base.OnCultureChanged();
        foreach (var u in Users) u.RaiseAllPropertiesChanged();
        foreach (var g in Groups) g.RaiseAllPropertiesChanged();
    }
}
