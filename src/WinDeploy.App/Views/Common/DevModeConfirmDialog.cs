using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using WinDeploy.Core.I18n;

namespace WinDeploy.App.Views.Common;

/// <summary>Themed confirmation dialog shown before enabling developer mode.</summary>
public sealed class DevModeConfirmDialog : Window
{
    public DevModeConfirmDialog()
    {
        Title = Localizer.T("dialog.devmode.title");
        Width = 480;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;
        Background = Brush("PageBg");

        var root = new StackPanel { Margin = new Thickness(24, 20, 24, 20) };

        // ── warning banner ──────────────────────────────────────────────
        var banner = new Border
        {
            Background = Brush("WarnBg"),
            BorderBrush = Brush("WarnFg"),
            BorderThickness = new Thickness(0, 0, 0, 0),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(14, 12, 14, 12),
            Margin = new Thickness(0, 0, 0, 18),
        };
        // Use a Grid so the text column gets remaining width and TextBlocks wrap correctly.
        var bannerRow = new Grid();
        bannerRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        bannerRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var warnIcon = new TextBlock
        {
            Text = "",
            FontFamily = new FontFamily("Segoe MDL2 Assets"),
            FontSize = 22,
            Foreground = Brush("WarnFg"),
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 1, 10, 0),
        };
        Grid.SetColumn(warnIcon, 0);
        bannerRow.Children.Add(warnIcon);

        var bannerText = new StackPanel();
        Grid.SetColumn(bannerText, 1);
        bannerText.Children.Add(new TextBlock
        {
            Text = Localizer.T("dialog.devmode.bannerTitle"),
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Foreground = Brush("WarnFg"),
            TextWrapping = TextWrapping.Wrap,
        });
        bannerText.Children.Add(new TextBlock
        {
            Text = Localizer.T("dialog.devmode.bannerBody"),
            FontSize = 12,
            Foreground = Brush("WarnFg"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 4, 0, 0),
            Opacity = 0.88,
        });
        bannerRow.Children.Add(bannerText);
        banner.Child = bannerRow;
        root.Children.Add(banner);

        // ── feature list ────────────────────────────────────────────────
        root.Children.Add(new TextBlock
        {
            Text = Localizer.T("dialog.devmode.featuresTitle"),
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Foreground = Brush("TextPrimary"),
            Margin = new Thickness(0, 0, 0, 8),
        });

        foreach (var item in new[]
        {
            ("", Localizer.T("dialog.devmode.feat1")),
            ("", Localizer.T("dialog.devmode.feat2")),
            ("", Localizer.T("dialog.devmode.feat3")),
        })
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 3, 0, 0) };
            row.Children.Add(new TextBlock
            {
                Text = item.Item1,
                FontFamily = new FontFamily("Segoe MDL2 Assets"),
                FontSize = 11,
                Foreground = Brush("Accent"),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 6, 0),
                Width = 16,
            });
            row.Children.Add(new TextBlock
            {
                Text = item.Item2,
                FontSize = 12,
                Foreground = Brush("TextSecondary"),
                TextWrapping = TextWrapping.Wrap,
            });
            root.Children.Add(row);
        }

        root.Children.Add(new TextBlock
        {
            Text = Localizer.T("dialog.devmode.footer"),
            FontSize = 12,
            Foreground = Brush("TextTertiary"),
            Margin = new Thickness(0, 14, 0, 0),
        });

        // ── buttons ─────────────────────────────────────────────────────
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 20, 0, 0),
        };
        var cancel = new Button { Content = Localizer.T("common.cancel"), MinWidth = 72, IsCancel = true };
        if (Application.Current.TryFindResource("MiniButton") is Style cancelStyle) cancel.Style = cancelStyle;
        cancel.Click += (_, _) => DialogResult = false;

        var confirm = new Button { Content = Localizer.T("dialog.devmode.confirm"), MinWidth = 96, Margin = new Thickness(10, 0, 0, 0), IsDefault = true };
        if (Application.Current.TryFindResource("PrimaryButton") is Style confirmStyle) confirm.Style = confirmStyle;
        confirm.Click += (_, _) => DialogResult = true;

        buttons.Children.Add(cancel);
        buttons.Children.Add(confirm);
        root.Children.Add(buttons);

        Content = root;
        SourceInitialized += (_, _) => ThemeManager.ApplyTitleBar(this);
    }

    private static Brush Brush(string key)
        => Application.Current.TryFindResource(key) as Brush ?? Brushes.Gray;
}
