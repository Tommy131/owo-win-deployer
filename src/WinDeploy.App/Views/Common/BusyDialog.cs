using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using WinDeploy.Core.I18n;

namespace WinDeploy.App.Views.Common;

/// <summary>A small modal, non-closable "please wait" dialog (themed, no title-bar close button, Alt+F4
/// blocked). Use <see cref="RunAsync{T}"/> to run a background operation while it's shown — it opens,
/// runs the work off the UI thread, then closes itself.</summary>
public sealed class BusyDialog : Window
{
    private bool _allowClose;

    private BusyDialog(string title, string message)
    {
        Title = title;
        Width = 440;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;
        WindowStyle = WindowStyle.None;     // no chrome → no close button
        ShowInTaskbar = false;
        Background = Brush("PageBg");

        // Block every close path (Alt+F4 / programmatic) until the work is done.
        Closing += (_, e) => { if (!_allowClose) e.Cancel = true; };

        var card = new Border
        {
            Background = Brush("CardBg"),
            BorderBrush = Brush("BorderStrong"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Margin = new Thickness(10),
            Padding = new Thickness(22, 20, 22, 20),
        };
        var stack = new StackPanel();

        stack.Children.Add(new TextBlock
        {
            Text = title, FontSize = 15, FontWeight = FontWeights.SemiBold,
            Foreground = Brush("TextPrimary"),
        });
        stack.Children.Add(new TextBlock
        {
            Text = message, FontSize = 13, TextWrapping = TextWrapping.Wrap,
            Foreground = Brush("TextSecondary"), Margin = new Thickness(0, 8, 0, 0),
        });
        stack.Children.Add(new ProgressBar
        {
            IsIndeterminate = true, Height = 6, Margin = new Thickness(0, 16, 0, 0),
            Background = Brush("PageBg"), Foreground = Brush("Accent"), BorderThickness = new Thickness(0),
        });
        stack.Children.Add(new TextBlock
        {
            Text = Localizer.T("dialog.busy.warning"), FontSize = 12,
            Foreground = Brush("WarnFg"), Margin = new Thickness(0, 12, 0, 0), TextWrapping = TextWrapping.Wrap,
        });

        card.Child = stack;
        Content = card;
        SourceInitialized += (_, _) => ThemeManager.ApplyTitleBar(this);
    }

    /// <summary>Show the dialog, run <paramref name="work"/> off the UI thread, then auto-close. Returns the
    /// work's result; re-throws if it failed.</summary>
    public static async Task<T> RunAsync<T>(Window? owner, string title, string message, Func<Task<T>> work)
    {
        var dlg = new BusyDialog(title, message);
        if (owner != null && !ReferenceEquals(owner, dlg)) dlg.Owner = owner;

        T result = default!;
        System.Exception? error = null;
        dlg.Loaded += async (_, _) =>
        {
            try { result = await work(); }
            catch (System.Exception ex) { error = ex; }
            finally { dlg._allowClose = true; dlg.Close(); }
        };
        dlg.ShowDialog();
        if (error != null) throw error;
        return result;
    }

    private static Brush Brush(string key) => Application.Current.TryFindResource(key) as Brush ?? Brushes.Gray;
}
