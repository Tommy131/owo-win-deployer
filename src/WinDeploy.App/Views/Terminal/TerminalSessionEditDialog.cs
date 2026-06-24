using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using WinDeploy.App.ViewModels;
using WinDeploy.Core.I18n;

namespace WinDeploy.App.Views.Terminal;

/// <summary>Themed dialog to rename (remark) a terminal session and pick its accent color — a quick visual
/// locator in the session picker. Fully theme-adapted (palette brushes + titled bar).</summary>
public sealed class TerminalSessionEditDialog : Window
{
    private readonly TextBox _name;
    private readonly List<Border> _swatches = new();
    private string _colorHex;

    public string SessionName => _name.Text.Trim();
    public string ColorHex => _colorHex;

    public TerminalSessionEditDialog(string name, string colorHex)
    {
        _colorHex = colorHex;
        Title = Localizer.T("termdlg.edit.title");
        Width = 420;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;
        Background = Brush("PageBg");

        var root = new StackPanel { Margin = new Thickness(24, 20, 24, 20) };

        // ── name / remark ───────────────────────────────────────────────
        root.Children.Add(Label(Localizer.T("termdlg.edit.nameLabel")));
        _name = new TextBox { Text = name, Margin = new Thickness(0, 6, 0, 0), Padding = new Thickness(8, 6, 8, 6) };
        root.Children.Add(_name);

        // ── color swatches ──────────────────────────────────────────────
        root.Children.Add(Label(Localizer.T("termdlg.edit.colorLabel"), top: 18));
        var wrap = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
        var colors = TerminalViewModel.Palette.ToList();
        if (!colors.Contains(_colorHex, StringComparer.OrdinalIgnoreCase)) colors.Insert(0, _colorHex);
        foreach (var hex in colors)
        {
            var sw = MakeSwatch(hex);
            _swatches.Add(sw);
            wrap.Children.Add(sw);
        }
        root.Children.Add(wrap);
        UpdateSwatchSelection();

        // ── buttons ─────────────────────────────────────────────────────
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 22, 0, 0),
        };
        var ok = new Button { Content = Localizer.T("common.save"), MinWidth = 88, IsDefault = true };
        if (Application.Current.TryFindResource("PrimaryButton") is Style okStyle) ok.Style = okStyle;
        ok.Click += (_, _) => DialogResult = true;
        var cancel = new Button { Content = Localizer.T("common.cancel"), MinWidth = 72, Margin = new Thickness(10, 0, 0, 0), IsCancel = true };
        if (Application.Current.TryFindResource("MiniButton") is Style cancelStyle) cancel.Style = cancelStyle;
        cancel.Click += (_, _) => DialogResult = false;
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        root.Children.Add(buttons);

        Content = root;
        Loaded += (_, _) => { _name.Focus(); _name.SelectAll(); };
        SourceInitialized += (_, _) => ThemeManager.ApplyTitleBar(this);
    }

    private Border MakeSwatch(string hex)
    {
        Color color;
        try { color = (Color)ColorConverter.ConvertFromString(hex); } catch { color = Colors.Gray; }
        var b = new Border
        {
            Width = 30,
            Height = 30,
            CornerRadius = new CornerRadius(6),
            Background = new SolidColorBrush(color),
            BorderThickness = new Thickness(1),
            Margin = new Thickness(0, 0, 8, 8),
            Cursor = Cursors.Hand,
            Tag = hex,
            ToolTip = hex,
        };
        b.MouseLeftButtonUp += (_, _) => { _colorHex = hex; UpdateSwatchSelection(); };
        return b;
    }

    /// <summary>Ring the chosen swatch with the accent color so the current pick is obvious.</summary>
    private void UpdateSwatchSelection()
    {
        foreach (var sw in _swatches)
        {
            var selected = string.Equals((string)sw.Tag, _colorHex, StringComparison.OrdinalIgnoreCase);
            sw.BorderBrush = selected ? Brush("Accent") : Brush("BorderStrong");
            sw.BorderThickness = new Thickness(selected ? 3 : 1);
        }
    }

    private static TextBlock Label(string text, double top = 0) => new()
    {
        Text = text,
        FontSize = 12.5,
        FontWeight = FontWeights.SemiBold,
        Foreground = Brush("TextSecondary"),
        Margin = new Thickness(0, top, 0, 0),
    };

    private static Brush Brush(string key) => Application.Current.TryFindResource(key) as Brush ?? Brushes.Gray;
}
