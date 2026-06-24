using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using WinDeploy.Core.I18n;

namespace WinDeploy.App.Views.Common;

/// <summary>A tiny themed single-line text-input dialog. Returns the entered text via <see cref="Value"/>.</summary>
public sealed class InputDialog : Window
{
    private readonly TextBox _box;
    public string Value => _box.Text.Trim();

    public InputDialog(string title, string prompt, string placeholder = "", string initial = "")
    {
        Title = title;
        Width = 460;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;
        Background = Brush("PageBg");

        var root = new StackPanel { Margin = new Thickness(18) };
        root.Children.Add(new TextBlock { Text = prompt, TextWrapping = TextWrapping.Wrap, FontSize = 13, Foreground = Brush("TextSecondary"), Margin = new Thickness(0, 0, 0, 10) });

        _box = new TextBox
        {
            Text = initial, FontSize = 13.5, Padding = new Thickness(8, 6, 8, 6),
            Background = Brush("CardBg"), Foreground = Brush("TextPrimary"), BorderBrush = Brush("BorderStrong"),
        };
        if (!string.IsNullOrEmpty(placeholder) && string.IsNullOrEmpty(initial))
            _box.Tag = placeholder;
        root.Children.Add(_box);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        var ok = new Button { Content = Localizer.T("common.ok"), MinWidth = 80, Margin = new Thickness(0, 0, 8, 0), IsDefault = true };
        var cancel = new Button { Content = Localizer.T("common.cancel"), MinWidth = 72, IsCancel = true };
        if (Application.Current.TryFindResource("PrimaryButton") is Style okS) ok.Style = okS;
        if (Application.Current.TryFindResource("MiniButton") is Style caS) cancel.Style = caS;
        ok.Click += (_, _) => { if (Value.Length > 0) DialogResult = true; };
        cancel.Click += (_, _) => DialogResult = false;
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        root.Children.Add(buttons);

        Content = root;
        Loaded += (_, _) => { _box.Focus(); _box.SelectAll(); };
        SourceInitialized += (_, _) => ThemeManager.ApplyTitleBar(this);
    }

    private static Brush Brush(string key) => Application.Current.TryFindResource(key) as Brush ?? Brushes.Gray;
}
