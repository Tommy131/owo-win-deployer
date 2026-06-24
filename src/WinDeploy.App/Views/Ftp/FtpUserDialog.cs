using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using WinDeploy.App.Behaviors;
using WinDeploy.App.Services.Ftp;
using WinDeploy.Core.I18n;

namespace WinDeploy.App.Views.Ftp;

/// <summary>Add / edit an FTP user: name, password (PBKDF2 at OK time), group, home directory, per-command
/// permissions (or inherit from the group), enabled flag and a per-user connection cap.</summary>
public sealed class FtpUserDialog : Window
{
    private readonly FtpUser? _existing;
    private readonly TextBox _name;
    private readonly PasswordBox _pw = new() { FontSize = 13.5, Padding = new Thickness(8, 6, 8, 6) };
    private readonly ComboBox _group = new();
    private readonly TextBox _home;
    private readonly CheckBox _useGroup = new() { Content = Localizer.T("ftp.user.useGroupPerms"), Margin = new Thickness(0, 8, 0, 0) };
    private readonly CheckBox _enabled = new() { Content = Localizer.T("ftp.user.enabled"), Margin = new Thickness(0, 8, 0, 0), IsChecked = true };
    private readonly TextBox _maxConn;
    private readonly FtpPermPanel _perms = new();

    public FtpUser? Result { get; private set; }

    public FtpUserDialog(FtpUser? existing, IReadOnlyList<string> groups)
    {
        _existing = existing;
        Title = existing == null ? Localizer.T("ftp.user.addTitle") : Localizer.Format("ftp.user.editTitle", existing.Name);
        Width = 520;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;
        Background = Brush("PageBg");

        _name = Tb(existing?.Name ?? "");
        _home = Tb(existing?.Home ?? "");
        _maxConn = Tb((existing?.MaxConnections ?? 0).ToString());
        InputFilter.SetMode(_maxConn, "digits");   // per-user connection cap: digits only

        _group.Items.Add(Localizer.T("ftp.user.groupNone"));
        foreach (var g in groups) _group.Items.Add(g);
        _group.SelectedItem = string.IsNullOrEmpty(existing?.Group) ? Localizer.T("ftp.user.groupNone") : existing!.Group;
        StyleCombo(_group);

        var root = new StackPanel { Margin = new Thickness(20) };
        root.Children.Add(Label(Localizer.T("ftp.user.nameLabel")));
        root.Children.Add(_name);
        root.Children.Add(Label(existing == null ? Localizer.T("ftp.user.passwordLabel") : Localizer.T("ftp.user.passwordLabelKeep")));
        StylePw(_pw);
        root.Children.Add(_pw);
        root.Children.Add(Label(Localizer.T("ftp.user.groupLabel")));
        root.Children.Add(_group);

        root.Children.Add(Label(Localizer.T("ftp.user.homeLabel")));
        var homeRow = new DockPanel();
        var browse = MiniBtn(Localizer.T("ftp.config.browse"));
        browse.Click += (_, _) => { var d = new Microsoft.Win32.OpenFolderDialog { Title = Localizer.T("ftp.user.homePickTitle") }; if (d.ShowDialog() == true) _home.Text = d.FolderName; };
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
        var capLabel = new TextBlock { Text = Localizer.T("ftp.user.capLabel"), FontSize = 12, Foreground = Brush("TextTertiary"), VerticalAlignment = VerticalAlignment.Center };
        _maxConn.Width = 80;
        DockPanel.SetDock(_maxConn, Dock.Right);
        capRow.Children.Add(_maxConn);
        capRow.Children.Add(capLabel);
        root.Children.Add(capRow);

        _enabled.IsChecked = existing?.Enabled ?? true;
        root.Children.Add(_enabled);

        root.Children.Add(Buttons(existing == null ? Localizer.T("common.add") : Localizer.T("common.save")));
        Content = root;
        SourceInitialized += (_, _) => ThemeManager.ApplyTitleBar(this);
    }

    private void UpdatePermEnabled()
    {
        var inherit = _useGroup.IsChecked == true && (_group.SelectedItem as string) is { } sel && sel != Localizer.T("ftp.user.groupNone");
        _perms.SetEnabled(!inherit);
    }

    private void OnOk()
    {
        var name = _name.Text.Trim();
        if (name.Length == 0) { Warn(Localizer.T("ftp.user.nameRequired")); return; }
        var grp = _group.SelectedItem as string;
        grp = grp == Localizer.T("ftp.user.groupNone") ? null : grp;
        var home = _home.Text.Trim();
        if (home.Length == 0 && grp == null) { Warn(Localizer.T("ftp.user.homeRequired")); return; }
        var pw = _pw.Password;
        if (_existing == null && pw.Length == 0) { Warn(Localizer.T("ftp.user.passwordRequired")); return; }
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
        var cancel = new Button { Content = Localizer.T("common.cancel"), MinWidth = 72, IsCancel = true };
        if (Application.Current.TryFindResource("PrimaryButton") is Style okS) ok.Style = okS;
        if (Application.Current.TryFindResource("MiniButton") is Style caS) cancel.Style = caS;
        ok.Click += (_, _) => OnOk();
        cancel.Click += (_, _) => DialogResult = false;
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        return buttons;
    }

    private void Warn(string m) => Dialogs.Show(m, Title, MessageBoxButton.OK, MessageBoxImage.Warning);

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
