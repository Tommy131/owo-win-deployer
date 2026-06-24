using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using WinDeploy.Core.I18n;

namespace WinDeploy.App.Views.Common;

/// <summary>
/// Drop-in themed replacement for <see cref="MessageBox"/>. WPF's <c>MessageBox</c> renders its
/// Yes/No/Cancel/OK buttons in the OS UI language, which breaks i18n; this dialog draws the buttons itself
/// with localized captions (<c>common.ok/cancel/yes/no</c>) and matches the app theme.
///
/// Same call shape and return type as <c>MessageBox.Show(body, title, buttons, icon)</c> so call sites only
/// swap the type name. Lives in namespace <c>WinDeploy.App</c> so both ViewModels and Views resolve
/// <c>Dialogs.Show</c> with no extra using.
/// </summary>
public static class Dialogs
{
    public static MessageBoxResult Show(string body, string title,
        MessageBoxButton buttons = MessageBoxButton.OK, MessageBoxImage icon = MessageBoxImage.None)
    {
        // Must run on the UI thread; marshal if a caller is on a worker thread.
        var disp = Application.Current?.Dispatcher;
        if (disp != null && !disp.CheckAccess())
            return disp.Invoke(() => Show(body, title, buttons, icon));

        var dlg = new MessageDialogWindow(body, title, buttons, icon);
        if (Application.Current?.MainWindow is { } mw && !ReferenceEquals(mw, dlg) && mw.IsLoaded)
            dlg.Owner = mw;
        dlg.ShowDialog();
        return dlg.Result;
    }
}

internal sealed class MessageDialogWindow : Window
{
    public MessageBoxResult Result { get; private set; }

    public MessageDialogWindow(string body, string title, MessageBoxButton buttons, MessageBoxImage icon)
    {
        Title = title;
        Width = 460;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;
        Background = Brush("PageBg");
        // Default result when closed via Esc / the title-bar X.
        Result = buttons is MessageBoxButton.OK ? MessageBoxResult.OK
               : buttons is MessageBoxButton.YesNo ? MessageBoxResult.No
               : MessageBoxResult.Cancel;

        var root = new StackPanel { Margin = new Thickness(22, 20, 22, 18) };

        // icon + body row
        var top = new Grid();
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var (glyphCode, brushKey) = IconGlyph(icon);
        if (glyphCode != 0)
        {
            var ic = new TextBlock
            {
                Text = ((char)glyphCode).ToString(),
                FontFamily = new FontFamily("Segoe MDL2 Assets"),
                FontSize = 22,
                Foreground = Brush(brushKey),
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 1, 12, 0),
            };
            Grid.SetColumn(ic, 0);
            top.Children.Add(ic);
        }
        var text = new TextBlock
        {
            Text = body,
            TextWrapping = TextWrapping.Wrap,
            FontSize = 13.5,
            Foreground = Brush("TextPrimary"),
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(text, 1);
        top.Children.Add(text);
        root.Children.Add(top);

        // button row (right-aligned)
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 20, 0, 0),
        };
        foreach (var (caption, result, isDefault, isCancel) in ButtonSpecs(buttons))
        {
            var b = new Button { Content = caption, MinWidth = 84, Margin = new Thickness(8, 0, 0, 0), IsDefault = isDefault, IsCancel = isCancel };
            var styleKey = isDefault ? "PrimaryButton" : "MiniButton";
            if (Application.Current.TryFindResource(styleKey) is Style st) b.Style = st;
            var r = result;
            b.Click += (_, _) => { Result = r; DialogResult = true; };
            row.Children.Add(b);
        }
        root.Children.Add(row);

        Content = root;
        SourceInitialized += (_, _) => ThemeManager.ApplyTitleBar(this);
    }

    // (caption, result, isDefault, isCancel) in left-to-right order.
    private static IEnumerable<(string, MessageBoxResult, bool, bool)> ButtonSpecs(MessageBoxButton b)
    {
        string ok = Localizer.T("common.ok"), cancel = Localizer.T("common.cancel"),
               yes = Localizer.T("common.yes"), no = Localizer.T("common.no");
        return b switch
        {
            MessageBoxButton.OKCancel => new[]
            {
                (ok, MessageBoxResult.OK, true, false),
                (cancel, MessageBoxResult.Cancel, false, true),
            },
            MessageBoxButton.YesNo => new[]
            {
                (yes, MessageBoxResult.Yes, true, false),
                (no, MessageBoxResult.No, false, true),
            },
            MessageBoxButton.YesNoCancel => new[]
            {
                (yes, MessageBoxResult.Yes, true, false),
                (no, MessageBoxResult.No, false, false),
                (cancel, MessageBoxResult.Cancel, false, true),
            },
            _ => new[] { (ok, MessageBoxResult.OK, true, true) },
        };
    }

    // Segoe MDL2 Assets glyph code-point + theme brush key per icon (0 = no icon column).
    private static (int, string) IconGlyph(MessageBoxImage icon) => icon switch
    {
        MessageBoxImage.Warning => (0xE7BA, "WarnFg"),        // Warning
        MessageBoxImage.Error => (0xEA39, "FailFg"),          // ErrorBadge
        MessageBoxImage.Question => (0xE9CE, "Accent"),       // Unknown
        MessageBoxImage.Information => (0xE946, "Accent"),    // Info
        _ => (0, "TextPrimary"),
    };

    private static Brush Brush(string key) => Application.Current.TryFindResource(key) as Brush ?? Brushes.Gray;
}
