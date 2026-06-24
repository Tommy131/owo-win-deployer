using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using WinDeploy.Core.I18n;

namespace WinDeploy.App.Views.Sys;

/// <summary>Themed result popup for a device-eject attempt: a green confirmation when the device was safely
/// removed, or an amber notice (with the reason) when Windows vetoed the eject because the device is still in
/// use. Colors come from the active theme's resource brushes so it matches the rest of the app.</summary>
public sealed class EjectResultDialog : Window
{
    /// <summary>True when the user clicked 强制弹出 — the caller should then force-dismount and retry.</summary>
    public bool ForceRequested { get; private set; }

    public EjectResultDialog(bool success, string deviceName, string message, bool allowForce = false)
    {
        Title = success ? Localizer.T("sysov.ejectDlg.success.title") : Localizer.T("sysov.ejectDlg.fail.title");
        Width = 440;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;
        Background = Brush("PageBg");

        var root = new StackPanel { Margin = new Thickness(24, 20, 24, 20) };

        var fgKey = success ? "OkFg" : "WarnFg";
        var bgKey = success ? "OkBg" : "WarnBg";

        // ── header: status icon + device name ────────────────────────────
        var header = new Grid { Margin = new Thickness(0, 0, 0, 14) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var icon = new Border
        {
            Background = Brush(bgKey),
            CornerRadius = new CornerRadius(20),
            Width = 40,
            Height = 40,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 0, 12, 0),
            Child = new TextBlock
            {
                Text = success ? "" : "",   // CheckMark / Warning (Segoe MDL2 Assets)
                FontFamily = new FontFamily("Segoe MDL2 Assets"),
                FontSize = 18,
                Foreground = Brush(fgKey),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };
        Grid.SetColumn(icon, 0);
        header.Children.Add(icon);

        var titleStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(titleStack, 1);
        titleStack.Children.Add(new TextBlock
        {
            Text = success ? Localizer.T("sysov.ejectDlg.subSuccess") : Localizer.T("sysov.ejectDlg.subFail"),
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
            Foreground = Brush("TextPrimary"),
            TextWrapping = TextWrapping.Wrap,
        });
        titleStack.Children.Add(new TextBlock
        {
            Text = deviceName,
            FontSize = 12,
            Foreground = Brush("TextSecondary"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 2, 0, 0),
        });
        header.Children.Add(titleStack);
        root.Children.Add(header);

        // ── message body ─────────────────────────────────────────────────
        root.Children.Add(new TextBlock
        {
            Text = message,
            FontSize = 13,
            Foreground = Brush("TextSecondary"),
            TextWrapping = TextWrapping.Wrap,
            LineHeight = 20,
        });

        if (!success)
            root.Children.Add(new TextBlock
            {
                Text = allowForce
                    ? Localizer.T("sysov.ejectDlg.busy.body")
                    : Localizer.T("sysov.ejectDlg.notBusy.body"),
                FontSize = 12,
                Foreground = Brush(allowForce ? "WarnFg" : "TextTertiary"),
                TextWrapping = TextWrapping.Wrap,
                LineHeight = 18,
                Margin = new Thickness(0, 10, 0, 0),
            });

        // ── buttons ──────────────────────────────────────────────────────
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 20, 0, 0) };
        if (allowForce)
        {
            var cancel = new Button { Content = Localizer.T("sysov.ejectDlg.cancel"), MinWidth = 80, IsCancel = true };
            if (Application.Current.TryFindResource("MiniButton") is Style cs) cancel.Style = cs;
            cancel.Click += (_, _) => DialogResult = false;

            var force = new Button { Content = Localizer.T("sysov.ejectDlg.force"), MinWidth = 96, Margin = new Thickness(10, 0, 0, 0) };
            if (Application.Current.TryFindResource("DangerButton") is Style ds) force.Style = ds;
            force.Click += (_, _) => { ForceRequested = true; DialogResult = true; };

            buttons.Children.Add(cancel);
            buttons.Children.Add(force);
        }
        else
        {
            var ok = new Button { Content = Localizer.T("sysov.ejectDlg.gotIt"), MinWidth = 88, IsDefault = true, IsCancel = true };
            if (Application.Current.TryFindResource("PrimaryButton") is Style s) ok.Style = s;
            ok.Click += (_, _) => DialogResult = true;
            buttons.Children.Add(ok);
        }
        root.Children.Add(buttons);

        Content = root;
        SourceInitialized += (_, _) => ThemeManager.ApplyTitleBar(this);
    }

    private static Brush Brush(string key) => Application.Current.TryFindResource(key) as Brush ?? Brushes.Gray;
}
