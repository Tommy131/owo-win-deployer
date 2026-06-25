using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using WinDeploy.App.Services.Net;
using WinDeploy.Core.I18n;

namespace WinDeploy.App.Views.Deploy;

/// <summary>Push the local repo to another machine over SSH/SCP and run a deploy command there. Uses the
/// user's existing SSH key (no passwords). Self-contained themed window (no XAML/DataTemplate needed).</summary>
public sealed class RemoteDeployDialog : Window
{
    private readonly string _repoRoot;
    private readonly TextBox _host, _user, _port, _dir, _cmd, _output;
    private readonly Button _test, _deploy;

    public RemoteDeployDialog(string repoRoot)
    {
        _repoRoot = repoRoot;
        Title = Localizer.T("remote.title");
        Width = 620;
        Height = 560;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Brush("PageBg");

        var root = new Grid { Margin = new Thickness(18) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // intro
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // form
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // buttons
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });   // output

        root.Children.Add(Row(new TextBlock
        {
            Text = Localizer.T("remote.subtitle"), TextWrapping = TextWrapping.Wrap, FontSize = 12.5,
            Foreground = Brush("TextSecondary"), Margin = new Thickness(0, 0, 0, 12),
        }, 0));

        var form = new Grid();
        form.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });
        form.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _host = AddField(form, 0, "remote.host", "");
        _user = AddField(form, 1, "remote.user", Environment.UserName);
        _port = AddField(form, 2, "remote.port", "22");
        _dir = AddField(form, 3, "remote.dir", "owo-win-deployer");
        _cmd = AddField(form, 4, "remote.command", "windeploy apply --silent");
        root.Children.Add(Row(form, 1));

        _test = MakeButton("remote.test", "MiniButton");
        _deploy = MakeButton("remote.deploy", "PrimaryButton");
        var close = MakeButton("common.close", "MiniButton");
        _test.Click += async (_, _) => await RunAsync(test: true);
        _deploy.Click += async (_, _) => await RunAsync(test: false);
        close.Click += (_, _) => Close();
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 14, 0, 10) };
        buttons.Children.Add(_test);
        buttons.Children.Add(_deploy);
        buttons.Children.Add(close);
        root.Children.Add(Row(buttons, 2));

        _output = new TextBox
        {
            IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.NoWrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            FontFamily = new FontFamily("Consolas"), FontSize = 12,
            Background = Brush("CardBg"), Foreground = Brush("TextPrimary"), BorderBrush = Brush("BorderStrong"),
        };
        root.Children.Add(Row(_output, 3));

        Content = root;
        SourceInitialized += (_, _) => ThemeManager.ApplyTitleBar(this);
    }

    private async Task RunAsync(bool test)
    {
        var host = _host.Text.Trim();
        if (host.Length == 0) { _output.Text = Localizer.T("remote.needHost"); return; }
        if (!int.TryParse(_port.Text.Trim(), out var port) || port <= 0) port = 22;
        var user = _user.Text.Trim().Length == 0 ? Environment.UserName : _user.Text.Trim();

        _test.IsEnabled = _deploy.IsEnabled = false;
        _output.Text = Localizer.T(test ? "remote.testing" : "remote.deploying");
        try
        {
            if (test)
            {
                var (ok, output) = await RemoteDeploy.TestAsync(host, user, port);
                _output.Text = (ok ? "✓ " : "✗ ") + Localizer.T(ok ? "remote.testOk" : "remote.testFail")
                    + "\n\n" + output;
            }
            else
            {
                var (ok, output) = await RemoteDeploy.DeployAsync(host, user, port, _repoRoot, _dir.Text.Trim(), _cmd.Text.Trim());
                AuditLog.Action($"远程部署 {user}@{host}:{port} · {(ok ? "成功" : "失败")}");
                _output.Text = output + "\n\n" + (ok ? "✓ " : "✗ ") + Localizer.T(ok ? "remote.done" : "remote.fail");
            }
        }
        catch (Exception ex) { _output.Text = Localizer.Format("remote.error", ex.Message); }
        finally { _test.IsEnabled = _deploy.IsEnabled = true; }
    }

    private TextBox AddField(Grid form, int row, string labelKey, string initial)
    {
        form.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var label = new TextBlock { Text = Localizer.T(labelKey), FontSize = 13, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 5, 8, 5) };
        Grid.SetRow(label, row); Grid.SetColumn(label, 0);
        var box = new TextBox
        {
            Text = initial, FontSize = 13, Padding = new Thickness(7, 5, 7, 5), Margin = new Thickness(0, 4, 0, 4),
            Background = Brush("CardBg"), Foreground = Brush("TextPrimary"), BorderBrush = Brush("BorderStrong"),
        };
        Grid.SetRow(box, row); Grid.SetColumn(box, 1);
        form.Children.Add(label);
        form.Children.Add(box);
        return box;
    }

    private Button MakeButton(string key, string styleKey)
    {
        var b = new Button { Content = Localizer.T(key), MinWidth = 88, Margin = new Thickness(0, 0, 8, 0) };
        if (Application.Current.TryFindResource(styleKey) is Style s) b.Style = s;
        return b;
    }

    private static FrameworkElement Row(FrameworkElement el, int row) { Grid.SetRow(el, row); return el; }
    private static Brush Brush(string key) => Application.Current.TryFindResource(key) as Brush ?? Brushes.Gray;
}
