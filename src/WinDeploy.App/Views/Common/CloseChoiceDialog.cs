using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using WinDeploy.Core.I18n;

namespace WinDeploy.App.Views.Common;

public enum CloseChoice { Cancel, Tray, Exit }

/// <summary>Asked when the user closes the main window (and the close behavior is "ask"): minimize to the
/// system tray (background-resident) or quit the app. A checkbox remembers the choice into settings.</summary>
public sealed class CloseChoiceDialog : Window
{
    private readonly CheckBox _remember;
    public CloseChoice Choice { get; private set; } = CloseChoice.Cancel;
    public bool Remember => _remember.IsChecked == true;

    public CloseChoiceDialog()
    {
        Title = Localizer.T("dialog.close.title");
        Width = 460;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;
        Background = Brush("PageBg");

        var root = new StackPanel { Margin = new Thickness(20) };
        root.Children.Add(new TextBlock
        {
            Text = Localizer.T("dialog.close.q"),
            FontSize = 15, FontWeight = FontWeights.SemiBold, Foreground = Brush("TextPrimary"),
        });
        root.Children.Add(new TextBlock
        {
            Text = Localizer.T("dialog.close.desc"),
            FontSize = 12.5, Foreground = Brush("TextSecondary"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 14),
        });

        _remember = new CheckBox { Content = Localizer.T("dialog.close.remember"), FontSize = 12.5, Foreground = Brush("TextSecondary") };
        root.Children.Add(_remember);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        var tray = new Button { Content = Localizer.T("dialog.close.tray"), MinWidth = 110, Margin = new Thickness(0, 0, 8, 0), IsDefault = true };
        var exit = new Button { Content = Localizer.T("dialog.close.exit"), MinWidth = 88, Margin = new Thickness(0, 0, 8, 0) };
        var cancel = new Button { Content = Localizer.T("common.cancel"), MinWidth = 72, IsCancel = true };
        if (Application.Current.TryFindResource("PrimaryButton") is Style ps) tray.Style = ps;
        if (Application.Current.TryFindResource("DangerButton") is Style ds) exit.Style = ds;
        if (Application.Current.TryFindResource("MiniButton") is Style ms) cancel.Style = ms;
        tray.Click += (_, _) => { Choice = CloseChoice.Tray; DialogResult = true; };
        exit.Click += (_, _) => { Choice = CloseChoice.Exit; DialogResult = true; };
        cancel.Click += (_, _) => { Choice = CloseChoice.Cancel; DialogResult = false; };
        buttons.Children.Add(tray);
        buttons.Children.Add(exit);
        buttons.Children.Add(cancel);
        root.Children.Add(buttons);

        Content = root;
        SourceInitialized += (_, _) => ThemeManager.ApplyTitleBar(this);
    }

    private static Brush Brush(string key) => Application.Current.TryFindResource(key) as Brush ?? Brushes.Gray;
}
