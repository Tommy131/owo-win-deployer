using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using WinDeploy.App.Behaviors;
using WinDeploy.App.Services.Ftp;

namespace WinDeploy.App.Views;

/// <summary>Add / edit an FTP user: name, password (PBKDF2 at OK time), group, home directory, per-command
/// permissions (or inherit from the group), enabled flag and a per-user connection cap.</summary>
public sealed class FtpUserDialog : Window
{
    private readonly FtpUser? _existing;
    private readonly TextBox _name;
    private readonly PasswordBox _pw = new() { FontSize = 13.5, Padding = new Thickness(8, 6, 8, 6) };
    private readonly ComboBox _group = new();
    private readonly TextBox _home;
    private readonly CheckBox _useGroup = new() { Content = "权限继承所属分组", Margin = new Thickness(0, 8, 0, 0) };
    private readonly CheckBox _enabled = new() { Content = "启用该用户", Margin = new Thickness(0, 8, 0, 0), IsChecked = true };
    private readonly TextBox _maxConn;
    private readonly FtpPermPanel _perms = new();

    public FtpUser? Result { get; private set; }

    public FtpUserDialog(FtpUser? existing, IReadOnlyList<string> groups)
    {
        _existing = existing;
        Title = existing == null ? "添加用户" : $"编辑用户 · {existing.Name}";
        Width = 520;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;
        Background = Brush("PageBg");

        _name = Tb(existing?.Name ?? "");
        _home = Tb(existing?.Home ?? "");
        _maxConn = Tb((existing?.MaxConnections ?? 0).ToString());
        InputFilter.SetMode(_maxConn, "digits");   // per-user connection cap: digits only

        _group.Items.Add("（无）");
        foreach (var g in groups) _group.Items.Add(g);
        _group.SelectedItem = string.IsNullOrEmpty(existing?.Group) ? "（无）" : existing!.Group;
        StyleCombo(_group);

        var root = new StackPanel { Margin = new Thickness(20) };
        root.Children.Add(Label("用户名"));
        root.Children.Add(_name);
        root.Children.Add(Label(existing == null ? "密码" : "密码（留空 = 不修改）"));
        StylePw(_pw);
        root.Children.Add(_pw);
        root.Children.Add(Label("所属分组"));
        root.Children.Add(_group);

        root.Children.Add(Label("主目录（用户被限制在此目录内；留空且选了分组则继承分组主目录）"));
        var homeRow = new DockPanel();
        var browse = MiniBtn("浏览…");
        browse.Click += (_, _) => { var d = new Microsoft.Win32.OpenFolderDialog { Title = "选择主目录" }; if (d.ShowDialog() == true) _home.Text = d.FolderName; };
        DockPanel.SetDock(browse, Dock.Right);
        browse.Margin = new Thickness(8, 0, 0, 0);
        homeRow.Children.Add(browse);
        homeRow.Children.Add(_home);
        root.Children.Add(homeRow);

        root.Children.Add(_useGroup);
        _useGroup.IsChecked = existing?.UseGroupPermissions ?? true;
        root.Children.Add(_perms.Build());
        _perms.Set(existing?.Permissions ?? FtpPerm.ReadOnly);
        _useGroup.Checked += (_, _) => UpdatePermEnabled();
        _useGroup.Unchecked += (_, _) => UpdatePermEnabled();
        _group.SelectionChanged += (_, _) => UpdatePermEnabled();
        UpdatePermEnabled();

        var capRow = new DockPanel { Margin = new Thickness(0, 10, 0, 0) };
        var capLabel = new TextBlock { Text = "并发连接上限（0 = 用服务器默认）", FontSize = 12, Foreground = Brush("TextTertiary"), VerticalAlignment = VerticalAlignment.Center };
        _maxConn.Width = 80;
        DockPanel.SetDock(_maxConn, Dock.Right);
        capRow.Children.Add(_maxConn);
        capRow.Children.Add(capLabel);
        root.Children.Add(capRow);

        _enabled.IsChecked = existing?.Enabled ?? true;
        root.Children.Add(_enabled);

        root.Children.Add(Buttons(existing == null ? "添加" : "保存"));
        Content = root;
        SourceInitialized += (_, _) => Services.ThemeManager.ApplyTitleBar(this);
    }

    private void UpdatePermEnabled()
    {
        var inherit = _useGroup.IsChecked == true && (_group.SelectedItem as string) is not (null or "（无）");
        _perms.SetEnabled(!inherit);
    }

    private void OnOk()
    {
        var name = _name.Text.Trim();
        if (name.Length == 0) { Warn("请填写用户名"); return; }
        var grp = _group.SelectedItem as string;
        grp = grp == "（无）" ? null : grp;
        var home = _home.Text.Trim();
        if (home.Length == 0 && grp == null) { Warn("请填写主目录（或先选择一个带主目录的分组）"); return; }
        var pw = _pw.Password;
        if (_existing == null && pw.Length == 0) { Warn("新用户必须设置密码"); return; }
        int.TryParse(_maxConn.Text.Trim(), out var maxConn);

        var u = new FtpUser
        {
            Name = name,
            Group = grp,
            Home = home,
            UseGroupPermissions = _useGroup.IsChecked == true,
            Permissions = _perms.Get(),
            Enabled = _enabled.IsChecked == true,
            MaxConnections = Math.Max(0, maxConn),
        };
        if (pw.Length > 0) (u.PasswordHash, u.PasswordSalt) = FtpPassword.Create(pw);
        else if (_existing != null) { u.PasswordHash = _existing.PasswordHash; u.PasswordSalt = _existing.PasswordSalt; }

        Result = u;
        DialogResult = true;
    }

    // ── shared UI helpers ────────────────────────────────────────────────────
    private StackPanel Buttons(string okText)
    {
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0) };
        var ok = new Button { Content = okText, MinWidth = 88, Margin = new Thickness(0, 0, 8, 0), IsDefault = true };
        var cancel = new Button { Content = "取消", MinWidth = 72, IsCancel = true };
        if (Application.Current.TryFindResource("PrimaryButton") is Style okS) ok.Style = okS;
        if (Application.Current.TryFindResource("MiniButton") is Style caS) cancel.Style = caS;
        ok.Click += (_, _) => OnOk();
        cancel.Click += (_, _) => DialogResult = false;
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        return buttons;
    }

    private void Warn(string m) => MessageBox.Show(m, Title, MessageBoxButton.OK, MessageBoxImage.Warning);

    private static Button MiniBtn(string text)
    {
        var b = new Button { Content = text, MinWidth = 64 };
        if (Application.Current.TryFindResource("MiniButton") is Style s) b.Style = s;
        return b;
    }

    internal static TextBox Tb(string text) => new()
    {
        Text = text, FontSize = 13.5, Padding = new Thickness(8, 6, 8, 6), Margin = new Thickness(0, 0, 0, 2),
        Background = Brush("CardBg"), Foreground = Brush("TextPrimary"), BorderBrush = Brush("BorderStrong"),
    };

    private static void StylePw(PasswordBox p)
    {
        p.Background = Brush("CardBg"); p.Foreground = Brush("TextPrimary"); p.BorderBrush = Brush("BorderStrong");
        p.Margin = new Thickness(0, 0, 0, 2);
    }

    private static void StyleCombo(ComboBox c) { c.FontSize = 13; c.Margin = new Thickness(0, 0, 0, 2); }

    internal static TextBlock Label(string text) => new()
    {
        Text = text, FontSize = 12, Foreground = Brush("TextTertiary"), Margin = new Thickness(0, 10, 0, 4), TextWrapping = TextWrapping.Wrap,
    };

    internal static Brush Brush(string key) => Application.Current.TryFindResource(key) as Brush ?? Brushes.Gray;
}
