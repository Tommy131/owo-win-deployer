using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using WinDeploy.Core.I18n;

namespace WinDeploy.App.Views.Common;

/// <summary>Final, type-to-confirm gate for a destructive action: the OK button stays disabled until the user
/// types the exact required phrase (case-insensitive, trimmed). Themed to match the app.</summary>
public sealed class ConfirmPhraseDialog : Window
{
    private readonly string _phrase;
    private readonly Button _ok;
    private readonly TextBox _box;

    public ConfirmPhraseDialog(string title, string body, string requiredPhrase)
    {
        _phrase = requiredPhrase;
        Title = title;
        Width = 480;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;
        Background = Brush("PageBg");

        var root = new StackPanel { Margin = new Thickness(24, 20, 24, 18) };

        // warning banner (icon + body), Grid so the text wraps within the remaining width
        var banner = new Border
        {
            Background = Brush("FailBg"),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(14, 12, 14, 12),
            Margin = new Thickness(0, 0, 0, 16),
        };
        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var icon = new TextBlock
        {
            Text = "",                            // Warning (Segoe MDL2 Assets)
            FontFamily = new FontFamily("Segoe MDL2 Assets"),
            FontSize = 22,
            Foreground = Brush("FailFg"),
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 1, 10, 0),
        };
        Grid.SetColumn(icon, 0);
        row.Children.Add(icon);
        var bodyText = new TextBlock
        {
            Text = body,
            TextWrapping = TextWrapping.Wrap,
            FontSize = 13,
            Foreground = Brush("FailFg"),
        };
        Grid.SetColumn(bodyText, 1);
        row.Children.Add(bodyText);
        banner.Child = row;
        root.Children.Add(banner);

        // the exact phrase the user must type
        root.Children.Add(new TextBlock
        {
            Text = Localizer.Format("settings.reset.confirm.typePrompt", requiredPhrase),
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12.5,
            Foreground = Brush("TextSecondary"),
            Margin = new Thickness(0, 0, 0, 8),
        });

        _box = new TextBox
        {
            FontSize = 14,
            FontFamily = new FontFamily("Consolas"),
            Padding = new Thickness(8, 7, 8, 7),
            Background = Brush("CardBg"),
            Foreground = Brush("TextPrimary"),
            BorderBrush = Brush("BorderStrong"),
        };
        root.Children.Add(_box);

        // buttons
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 20, 0, 0) };
        var cancel = new Button { Content = Localizer.T("common.cancel"), MinWidth = 84, IsCancel = true };
        if (Application.Current.TryFindResource("MiniButton") is Style cs) cancel.Style = cs;
        cancel.Click += (_, _) => DialogResult = false;
        _ok = new Button { Content = Localizer.T("settings.reset.confirm.ok"), MinWidth = 120, Margin = new Thickness(10, 0, 0, 0), IsDefault = true, IsEnabled = false };
        if (Application.Current.TryFindResource("DangerButton") is Style ds) _ok.Style = ds;
        _ok.Click += (_, _) => { if (Matches()) DialogResult = true; };
        buttons.Children.Add(cancel);
        buttons.Children.Add(_ok);
        root.Children.Add(buttons);

        _box.TextChanged += (_, _) => _ok.IsEnabled = Matches();   // wired after _ok exists

        Content = root;
        Loaded += (_, _) => _box.Focus();
        SourceInitialized += (_, _) => ThemeManager.ApplyTitleBar(this);
    }

    private bool Matches() => _box.Text.Trim().Equals(_phrase, StringComparison.OrdinalIgnoreCase);

    private static Brush Brush(string key) => Application.Current.TryFindResource(key) as Brush ?? Brushes.Gray;
}
