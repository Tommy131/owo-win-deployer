using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using WinDeploy.App.Services.Ftp;
using WinDeploy.Core.I18n;

namespace WinDeploy.App.Views.Ftp;

/// <summary>A reusable block of per-command permission checkboxes (List / Download / Upload / …) with
/// quick 只读 / 完全 / 无 presets, shared by the user and group dialogs.</summary>
internal sealed class FtpPermPanel
{
    private readonly (FtpPerm Flag, string Text)[] _defs =
    {
        (FtpPerm.List,      Localizer.T("ftp.perm.list")),
        (FtpPerm.Download,  Localizer.T("ftp.perm.download")),
        (FtpPerm.Upload,    Localizer.T("ftp.perm.upload")),
        (FtpPerm.Append,    Localizer.T("ftp.perm.append")),
        (FtpPerm.Delete,    Localizer.T("ftp.perm.delete")),
        (FtpPerm.Rename,    Localizer.T("ftp.perm.rename")),
        (FtpPerm.CreateDir, Localizer.T("ftp.perm.createDir")),
        (FtpPerm.DeleteDir, Localizer.T("ftp.perm.deleteDir")),
    };
    private readonly List<(FtpPerm Flag, CheckBox Box)> _boxes = new();
    private readonly List<Button> _presets = new();

    public FrameworkElement Build()
    {
        var outer = new StackPanel { Margin = new Thickness(0, 6, 0, 0) };
        outer.Children.Add(new TextBlock { Text = Localizer.T("ftp.perm.title"), FontSize = 12, Foreground = Brush("TextTertiary"), Margin = new Thickness(0, 4, 0, 4) });

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (var i = 0; i < _defs.Length; i++)
        {
            var (flag, text) = _defs[i];
            var cb = new CheckBox { Content = text, Margin = new Thickness(0, 3, 12, 3) };
            Grid.SetColumn(cb, i % 2);
            Grid.SetRow(cb, i / 2);
            if (grid.RowDefinitions.Count <= i / 2) grid.RowDefinitions.Add(new RowDefinition());
            grid.Children.Add(cb);
            _boxes.Add((flag, cb));
        }
        outer.Children.Add(grid);

        var presets = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
        presets.Children.Add(Preset(Localizer.T("ftp.perm.presetReadOnly"), FtpPerm.ReadOnly));
        presets.Children.Add(Preset(Localizer.T("ftp.perm.presetFull"), FtpPerm.Full));
        presets.Children.Add(Preset(Localizer.T("ftp.perm.presetNone"), FtpPerm.None));
        outer.Children.Add(presets);
        return outer;
    }

    private Button Preset(string text, FtpPerm value)
    {
        var b = new Button { Content = text, MinWidth = 64, Margin = new Thickness(0, 0, 8, 0) };
        if (Application.Current.TryFindResource("MiniButton") is Style s) b.Style = s;
        b.Click += (_, _) => Set(value);
        _presets.Add(b);
        return b;
    }

    public void Set(FtpPerm p)
    {
        foreach (var (flag, box) in _boxes) box.IsChecked = p.HasFlag(flag);
    }

    public FtpPerm Get()
    {
        var p = FtpPerm.None;
        foreach (var (flag, box) in _boxes) if (box.IsChecked == true) p |= flag;
        return p;
    }

    public void SetEnabled(bool enabled)
    {
        foreach (var (_, box) in _boxes) box.IsEnabled = enabled;
        foreach (var b in _presets) b.IsEnabled = enabled;
    }

    private static Brush Brush(string key) => Application.Current.TryFindResource(key) as Brush ?? Brushes.Gray;
}
