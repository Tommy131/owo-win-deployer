using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using WinDeploy.App.Services;

namespace WinDeploy.App.Views;

/// <summary>A small themed dialog that shows one physical disk's simplified SMART / reliability counters
/// (health, temperature, wear, power-on hours, error counts). Fetches asynchronously on open.</summary>
public sealed class SmartDialog : Window
{
    private readonly string? _deviceId;
    private readonly StackPanel _body;

    public SmartDialog(string title, string? deviceId)
    {
        _deviceId = deviceId;
        Title = $"{title} · SMART";
        Width = 460;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;
        Background = Brush("PageBg");

        var root = new StackPanel { Margin = new Thickness(18) };
        root.Children.Add(new TextBlock
        {
            Text = $"{title}", FontSize = 15, FontWeight = FontWeights.SemiBold, Foreground = Brush("TextPrimary"),
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        root.Children.Add(new TextBlock
        {
            Text = "SMART / 磁盘可靠性计数器", FontSize = 12, Foreground = Brush("TextTertiary"), Margin = new Thickness(0, 2, 0, 0),
        });

        _body = new StackPanel { Margin = new Thickness(0, 14, 0, 0) };
        _body.Children.Add(Row("读取中 …", ""));
        root.Children.Add(_body);

        var close = new Button { Content = "关闭", MinWidth = 72, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0), IsCancel = true };
        if (Application.Current.TryFindResource("MiniButton") is Style s) close.Style = s;
        close.Click += (_, _) => Close();
        root.Children.Add(close);

        Content = root;
        Loaded += async (_, _) => await LoadAsync();
        SourceInitialized += (_, _) => ThemeManager.ApplyTitleBar(this);
    }

    private async Task LoadAsync()
    {
        var s = await SystemInfo.GetSmartAsync(_deviceId);
        _body.Children.Clear();
        if (!s.Ok)
        {
            _body.Children.Add(Row("无法读取", "该磁盘未提供 SMART / 可靠性数据"));
            _body.Children.Add(new TextBlock
            {
                Text = "（部分 USB 移动硬盘 / RAID / 虚拟磁盘不暴露这些计数器）",
                FontSize = 11.5, Foreground = Brush("TextTertiary"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0),
            });
            return;
        }

        _body.Children.Add(Row("健康状态", s.Health ?? "未知", healthy: string.Equals(s.Health, "Healthy", StringComparison.OrdinalIgnoreCase)));
        _body.Children.Add(Row("介质类型", s.Media ?? "未知"));
        _body.Children.Add(Row("总线类型", s.Bus ?? "未知"));
        _body.Children.Add(Row("温度", s.Temperature is int t ? $"{t} °C" + (s.TemperatureMax is int tm && tm > 0 ? $"（峰值 {tm} °C）" : "") : "不可用"));
        _body.Children.Add(Row("已用寿命", s.Wear is int w ? $"{w}%" : "不可用"));
        _body.Children.Add(Row("通电时间", s.PowerOnHours is long h ? $"{h} 小时（约 {h / 24} 天）" : "不可用"));
        _body.Children.Add(Row("启停次数", s.StartStop?.ToString() ?? "不可用"));
        _body.Children.Add(Row("读取错误", ErrText(s.ReadErrorsUncorrected, s.ReadErrorsTotal)));
        _body.Children.Add(Row("写入错误", ErrText(s.WriteErrorsUncorrected, s.WriteErrorsTotal)));

        if (s.Temperature == null && s.Wear == null && s.PowerOnHours == null)
            _body.Children.Add(new TextBlock
            {
                Text = "部分驱动器（SATA / USB）不暴露温度与寿命等计数器，NVMe 通常更完整；以管理员身份运行本程序可获取更多数据。",
                FontSize = 11.5, Foreground = Brush("TextTertiary"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 0),
            });
    }

    private static string ErrText(long? unc, long? total)
        => unc == null && total == null ? "不可用" : $"未纠正 {(unc?.ToString() ?? "?")} · 累计 {(total?.ToString() ?? "?")}";

    private Border Row(string label, string value, bool? healthy = null)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(96) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var l = new TextBlock { Text = label, FontSize = 13, Foreground = Brush("TextSecondary"), VerticalAlignment = VerticalAlignment.Center };
        var v = new TextBlock
        {
            Text = value, FontSize = 13, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center,
            Foreground = healthy switch { true => Brush("OkFg"), false => Brush("WarnFg"), _ => Brush("TextPrimary") },
            FontWeight = healthy.HasValue ? FontWeights.SemiBold : FontWeights.Normal,
        };
        Grid.SetColumn(l, 0);
        Grid.SetColumn(v, 1);
        grid.Children.Add(l);
        grid.Children.Add(v);
        return new Border { Padding = new Thickness(0, 5, 0, 5), Child = grid };
    }

    private static Brush Brush(string key) => Application.Current.TryFindResource(key) as Brush ?? Brushes.Gray;
}
