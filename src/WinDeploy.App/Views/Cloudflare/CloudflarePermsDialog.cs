using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using WinDeploy.Core.I18n;

namespace WinDeploy.App.Views.Cloudflare;

/// <summary>Themed help dialog listing the exact Cloudflare permissions a scoped API token needs for DDNS.
/// Built in code using the app's theme resource brushes (resolved at open time), so it matches the current
/// light / dark theme automatically — same approach as <see cref="DevModeConfirmDialog"/>.</summary>
public sealed class CloudflarePermsDialog : Window
{
    public CloudflarePermsDialog()
    {
        Title = Localizer.T("cloud.perms.dialogTitle");
        Width = 560;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;
        Background = Brush("PageBg");

        var root = new StackPanel { Margin = new Thickness(24, 20, 24, 20) };

        root.Children.Add(new TextBlock
        {
            Text = Localizer.T("cloud.perms.intro"),
            FontSize = 14, FontWeight = FontWeights.SemiBold, Foreground = Brush("TextPrimary"),
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 4),
        });
        root.Children.Add(new TextBlock
        {
            Text = Localizer.T("cloud.perms.path"),
            FontSize = 12, Foreground = Brush("TextTertiary"), TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 14),
        });

        // ── 权限 ──────────────────────────────────────────────────────────────
        root.Children.Add(SectionTitle(Localizer.T("cloud.perms.permissions")));
        root.Children.Add(PermRow(new[] { Localizer.T("cloud.perms.chipZone"), Localizer.T("cloud.perms.chipZone"), Localizer.T("cloud.perms.chipRead") },
            Localizer.T("cloud.perms.rowZoneReadDesc")));
        root.Children.Add(PermRow(new[] { Localizer.T("cloud.perms.chipZone"), Localizer.T("cloud.perms.chipDns"), Localizer.T("cloud.perms.chipEdit") },
            Localizer.T("cloud.perms.rowZoneDnsDesc")));

        // ── 区域资源 ──────────────────────────────────────────────────────────
        root.Children.Add(SectionTitle(Localizer.T("cloud.perms.zoneResources")));
        root.Children.Add(PermRow(new[] { Localizer.T("cloud.perms.chipInclude"), Localizer.T("cloud.perms.chipAllZones") },
            Localizer.T("cloud.perms.rowResourcesDesc")));

        // ── 说明 ──────────────────────────────────────────────────────────────
        var note = new Border
        {
            Background = Brush("AccentBg"), CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12, 10, 12, 10), Margin = new Thickness(0, 16, 0, 0),
        };
        note.Child = new TextBlock
        {
            Text = Localizer.T("cloud.perms.note"),
            FontSize = 12, Foreground = Brush("Accent"), TextWrapping = TextWrapping.Wrap, LineHeight = 19,
        };
        root.Children.Add(note);

        // ── 按钮 ──────────────────────────────────────────────────────────────
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 20, 0, 0),
        };
        var go = new Button { Content = Localizer.T("cloud.perms.goCreate"), MinWidth = 110, Margin = new Thickness(0, 0, 8, 0) };
        if (Application.Current.TryFindResource("PrimaryButton") is Style gs) go.Style = gs;
        go.Click += (_, _) => Open("https://dash.cloudflare.com/profile/api-tokens");

        var close = new Button { Content = Localizer.T("common.close"), MinWidth = 72, IsCancel = true, IsDefault = true };
        if (Application.Current.TryFindResource("MiniButton") is Style cs) close.Style = cs;
        close.Click += (_, _) => DialogResult = true;

        buttons.Children.Add(go);
        buttons.Children.Add(close);
        root.Children.Add(buttons);

        Content = root;
        SourceInitialized += (_, _) => ThemeManager.ApplyTitleBar(this);
    }

    private static TextBlock SectionTitle(string text) => new()
    {
        Text = text, FontSize = 12.5, FontWeight = FontWeights.SemiBold,
        Foreground = Brush("TextSecondary"), Margin = new Thickness(0, 8, 0, 6),
    };

    /// <summary>One permission line: a chain of chips (joined by ›) above a one-line description.</summary>
    private static UIElement PermRow(string[] chips, string desc)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };

        var chipRow = new StackPanel { Orientation = Orientation.Horizontal };
        for (var i = 0; i < chips.Length; i++)
        {
            if (i > 0)
                chipRow.Children.Add(new TextBlock
                {
                    Text = "›", FontSize = 14, Foreground = Brush("TextTertiary"),
                    Margin = new Thickness(6, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center,
                });
            chipRow.Children.Add(Chip(chips[i]));
        }
        panel.Children.Add(chipRow);

        panel.Children.Add(new TextBlock
        {
            Text = desc, FontSize = 12, Foreground = Brush("TextTertiary"),
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(2, 5, 0, 0),
        });
        return panel;
    }

    private static Border Chip(string text) => new()
    {
        Background = Brush("CardBg"), BorderBrush = Brush("Accent"), BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(6), Padding = new Thickness(9, 3, 9, 3),
        Child = new TextBlock { Text = text, FontSize = 12, Foreground = Brush("Accent"), VerticalAlignment = VerticalAlignment.Center },
    };

    private static void Open(string url)
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }); }
        catch { /* ignore */ }
    }

    private static Brush Brush(string key) => Application.Current.TryFindResource(key) as Brush ?? Brushes.Gray;
}
