using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using WinDeploy.App.Services;
using WinDeploy.Core.I18n;

namespace WinDeploy.App.Views.Server;

/// <summary>Form for a new virtual host: server name, listen port, document root, optional HTTPS with a
/// certificate / key picked from the server's SSL dir. Produces a <see cref="VhostSpec"/>.</summary>
public sealed class VhostDialog : Window
{
    private readonly TextBox _name = Tb("example.local");
    private readonly TextBox _port;
    private readonly TextBox _root = Tb(@"C:\www\example");
    private readonly CheckBox _ssl = new() { Content = Localizer.T("ftp.vhost.enableSsl"), Margin = new Thickness(0, 4, 0, 0) };
    private readonly ComboBox _cert = new();
    private readonly ComboBox _key = new();
    private readonly StackPanel _sslPanel;

    public VhostSpec? Result { get; private set; }

    public VhostDialog(ServerInfo server)
    {
        Title = Localizer.Format("ftp.vhost.title", server.Name);
        Width = 520;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;
        Background = Brush("PageBg");

        _port = Tb("80");
        WinDeploy.App.Behaviors.InputFilter.SetMode(_port, "portlist");   // digits + separators only

        var root = new StackPanel { Margin = new Thickness(20) };
        root.Children.Add(Label(Localizer.T("ftp.vhost.nameLabel")));
        root.Children.Add(_name);
        root.Children.Add(Label(Localizer.T("ftp.vhost.portLabel")));
        root.Children.Add(_port);
        root.Children.Add(Label(Localizer.T("ftp.vhost.docRootLabel")));
        var rootRow = new DockPanel { Margin = new Thickness(0, 0, 0, 2) };
        var browse = new Button { Content = Localizer.T("ftp.vhost.browse"), MinWidth = 64, Margin = new Thickness(8, 0, 0, 0) };
        if (Application.Current.TryFindResource("MiniButton") is Style ms) browse.Style = ms;
        browse.Click += (_, _) =>
        {
            var d = new Microsoft.Win32.OpenFolderDialog { Title = Localizer.T("ftp.vhost.rootPickTitle") };
            if (d.ShowDialog() == true) _root.Text = d.FolderName;
        };
        DockPanel.SetDock(browse, Dock.Right);
        rootRow.Children.Add(browse);
        rootRow.Children.Add(_root);
        root.Children.Add(rootRow);

        root.Children.Add(_ssl);
        _sslPanel = new StackPanel { Margin = new Thickness(0, 4, 0, 0), Visibility = Visibility.Collapsed };
        _sslPanel.Children.Add(Label(Localizer.T("ftp.vhost.certLabel")));
        _sslPanel.Children.Add(StyleCombo(_cert));
        _sslPanel.Children.Add(Label(Localizer.T("ftp.vhost.keyLabel")));
        _sslPanel.Children.Add(StyleCombo(_key));
        var certs = ServerManager.ListCerts(server);
        foreach (var c in certs)
        {
            if (c.Kind is "证书" or "PFX" or "其他") _cert.Items.Add(c.Path);
            if (c.Kind is "私钥" or "其他") _key.Items.Add(c.Path);
        }
        if (_cert.Items.Count > 0) _cert.SelectedIndex = 0;
        if (_key.Items.Count > 0) _key.SelectedIndex = 0;
        if (certs.Count == 0)
            _sslPanel.Children.Add(new TextBlock { Text = Localizer.T("ftp.vhost.noCert"), FontSize = 11.5, Foreground = Brush("WarnFg"), Margin = new Thickness(0, 6, 0, 0), TextWrapping = TextWrapping.Wrap });
        root.Children.Add(_sslPanel);
        _ssl.Checked += (_, _) => { _sslPanel.Visibility = Visibility.Visible; if (_port.Text.Trim() == "80") _port.Text = "443"; };
        _ssl.Unchecked += (_, _) => { _sslPanel.Visibility = Visibility.Collapsed; if (_port.Text.Trim() == "443") _port.Text = "80"; };

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0) };
        var ok = new Button { Content = Localizer.T("ftp.vhost.create"), MinWidth = 88, Margin = new Thickness(0, 0, 8, 0), IsDefault = true };
        var cancel = new Button { Content = Localizer.T("common.cancel"), MinWidth = 72, IsCancel = true };
        if (Application.Current.TryFindResource("PrimaryButton") is Style okS) ok.Style = okS;
        if (Application.Current.TryFindResource("MiniButton") is Style caS) cancel.Style = caS;
        ok.Click += (_, _) => OnOk();
        cancel.Click += (_, _) => DialogResult = false;
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        root.Children.Add(buttons);

        Content = root;
        SourceInitialized += (_, _) => ThemeManager.ApplyTitleBar(this);
    }

    private void OnOk()
    {
        var name = _name.Text.Trim();
        var rootDir = _root.Text.Trim();
        if (name.Length == 0) { Dialogs.Show(Localizer.T("ftp.vhost.nameRequired"), Localizer.T("ftp.vhost.titleShort")); return; }
        if (rootDir.Length == 0) { Dialogs.Show(Localizer.T("ftp.vhost.rootRequired"), Localizer.T("ftp.vhost.titleShort")); return; }
        var ports = ParsePorts(_port.Text);
        if (ports.Count == 0) { Dialogs.Show(Localizer.T("ftp.vhost.portInvalid"), Localizer.T("ftp.vhost.titleShort")); return; }
        var ssl = _ssl.IsChecked == true;
        if (ssl && (_cert.SelectedItem == null || _key.SelectedItem == null)) { Dialogs.Show(Localizer.T("ftp.vhost.sslNeedCert"), Localizer.T("ftp.vhost.titleShort")); return; }
        Result = new VhostSpec(name, ports, rootDir, ssl, _cert.SelectedItem as string, _key.SelectedItem as string);
        DialogResult = true;
    }

    /// <summary>Parse "80,8080,8443" (English or Chinese comma) into a deduped, ordered, valid port list.</summary>
    private static List<int> ParsePorts(string raw)
    {
        var ports = new List<int>();
        foreach (var part in raw.Split(new[] { ',', '，', ' ', ';', '；' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            if (int.TryParse(part, out var p) && p > 0 && p <= 65535 && !ports.Contains(p))
                ports.Add(p);
        return ports;
    }

    private static TextBox Tb(string placeholder) => new()
    {
        FontSize = 13.5, Padding = new Thickness(8, 6, 8, 6), Margin = new Thickness(0, 0, 0, 2),
        Background = Brush("CardBg"), Foreground = Brush("TextPrimary"), BorderBrush = Brush("BorderStrong"),
        Tag = placeholder,
    };

    private static ComboBox StyleCombo(ComboBox c)
    {
        c.FontSize = 12.5; c.Margin = new Thickness(0, 0, 0, 2);
        c.Background = Brush("CardBg"); c.Foreground = Brush("TextPrimary");
        return c;
    }

    private static TextBlock Label(string text) => new()
    {
        Text = text, FontSize = 12, Foreground = Brush("TextTertiary"), Margin = new Thickness(0, 10, 0, 4),
    };

    private static Brush Brush(string key) => Application.Current.TryFindResource(key) as Brush ?? Brushes.Gray;
}
