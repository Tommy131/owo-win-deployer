using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using WinDeploy.App.Services;

namespace WinDeploy.App.Views;

/// <summary>SMART detail for one physical disk: identity (model/serial), health, temperature, power-on
/// hours, HDD high-risk counters (C5/C6/reallocated) or SSD endurance (host writes/reads, life), plus the
/// full raw ATA attribute table. The full table needs admin — offers an elevated re-read.</summary>
public sealed class SmartDialog : Window
{
    private readonly string? _deviceId;
    private readonly StackPanel _body;
    private SmartInfo? _last;
    private bool _showSerial;
    private bool _offerElevate = true;   // preserved across serial show/hide re-renders
    private readonly bool _elevated = IsElevated();
    private const double LabelCol = 148;  // wide enough for the longest label ("介质 / 完整性错误") so it never overlaps the value

    public SmartDialog(string title, string? deviceId)
    {
        _deviceId = deviceId;
        Title = $"{title} · SMART";
        Width = 560;
        SizeToContent = SizeToContent.Height;
        MaxHeight = 760;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;
        Background = Brush("PageBg");

        // DockPanel keeps the 关闭 button pinned to the bottom (never clipped), while the body scrolls.
        var dock = new DockPanel { Margin = new Thickness(18) };

        var close = new Button { Content = "关闭", MinWidth = 72, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0), IsCancel = true };
        if (Application.Current.TryFindResource("MiniButton") is Style s) close.Style = s;
        close.Click += (_, _) => Close();
        DockPanel.SetDock(close, Dock.Bottom);
        dock.Children.Add(close);

        var content = new StackPanel();
        content.Children.Add(new TextBlock { Text = title, FontSize = 15, FontWeight = FontWeights.SemiBold, Foreground = Brush("TextPrimary"), TextTrimming = TextTrimming.CharacterEllipsis });
        content.Children.Add(new TextBlock { Text = "SMART / 磁盘健康诊断", FontSize = 12, Foreground = Brush("TextTertiary"), Margin = new Thickness(0, 2, 0, 0) });

        _body = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };
        _body.Children.Add(Row("读取中 …", ""));
        content.Children.Add(_body);

        // Margin right -18 / Padding right 10: cancels the DockPanel's 18px right margin so the scrollbar sits
        // flush at the dialog's outer edge, while the body keeps a small gap from it.
        dock.Children.Add(new ScrollViewer { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(0, 0, -18, 0), Padding = new Thickness(0, 0, 10, 0) });

        Content = dock;
        Loaded += async (_, _) => await LoadAsync();
        SourceInitialized += (_, _) => ThemeManager.ApplyTitleBar(this);
    }

    private async Task LoadAsync() => Render(await SystemInfo.GetSmartAsync(_deviceId), offerElevate: true);

    private void Render(SmartInfo s, bool offerElevate)
    {
        _last = s;
        _offerElevate = offerElevate;
        _body.Children.Clear();

        if (!s.Ok)
        {
            _body.Children.Add(Row("无法读取", "未能识别该磁盘"));
            return;
        }

        _body.Children.Add(Row("型号", s.Model ?? s.Friendly));
        _body.Children.Add(SerialRow(s));
        _body.Children.Add(Row("容量", s.SizeBytes > 0 ? Gb(s.SizeBytes) : "未知"));
        _body.Children.Add(Row("健康状态", s.Health ?? "未知", healthy: string.Equals(s.Health, "Healthy", StringComparison.OrdinalIgnoreCase)));
        _body.Children.Add(Row("介质 / 总线", $"{s.Media ?? "未知"} · {s.Bus ?? "未知"}"));
        _body.Children.Add(Row("温度", s.Temperature is int t ? $"{t} °C" : "不可用"));
        _body.Children.Add(Row("通电时间", s.PowerOnHours is long h ? $"{h} 小时（约 {h / 24} 天）" : "不可用"));
        _body.Children.Add(Row("通电次数", s.PowerCycles?.ToString() ?? "不可用"));

        if (s.IsSsd)
        {
            _body.Children.Add(Row("剩余寿命", s.RemainingLifePercent is int life ? $"{life}%" : "不可用",
                danger: s.RemainingLifePercent is int lp && lp <= 10));
            if (s.IsNvme && s.PercentageUsed is int pu)
                _body.Children.Add(Row("已用寿命", $"{pu}%", danger: pu >= 90));
            // Some SATA SSDs (e.g. Samsung 860 EVO) only expose Total LBAs Written (0xF1) and omit reads (0xF2);
            // distinguish "drive doesn't report it" from a read failure. NVMe always reports both.
            _body.Children.Add(Row("累计写入", s.HostWritesBytes is long hw ? Gb(hw) : s.IsNvme ? "不可用" : "该型号未上报 (0xF1)"));
            _body.Children.Add(Row("累计读取", s.HostReadsBytes is long hr ? Gb(hr) : s.IsNvme ? "不可用" : "该型号未上报 (0xF2)"));
            if (s.IsNvme)
            {
                if (s.AvailableSpare is int sp)
                    _body.Children.Add(Row("可用备件", $"{sp}%" + (s.AvailableSpareThreshold is int th ? $"（阈值 {th}%）" : ""),
                        danger: s.AvailableSpareThreshold is int t2 && t2 > 0 && sp < t2));
                _body.Children.Add(Row("介质 / 完整性错误", s.MediaErrors?.ToString() ?? "不可用", danger: (s.MediaErrors ?? 0) > 0));
                _body.Children.Add(Row("不安全关机", s.UnsafeShutdowns?.ToString() ?? "不可用"));
                _body.Children.Add(Row("错误日志条目", s.ErrorLogEntries?.ToString() ?? "不可用"));
                _body.Children.Add(Row("严重警告", NvmeWarn(s.CriticalWarning), danger: (s.CriticalWarning ?? 0) != 0));
            }
        }
        else
        {
            _body.Children.Add(Row("重映射扇区 (05)", s.Reallocated?.ToString() ?? "不可用", danger: (s.Reallocated ?? 0) > 0));
            _body.Children.Add(Row("当前待映射 (C5)", s.Pending?.ToString() ?? "不可用", danger: (s.Pending ?? 0) > 0));
            _body.Children.Add(Row("无法纠正 (C6)", s.Uncorrectable?.ToString() ?? "不可用", danger: (s.Uncorrectable ?? 0) > 0));
            _body.Children.Add(Row("UDMA CRC (C7)", s.Crc?.ToString() ?? "不可用", danger: (s.Crc ?? 0) > 0));
        }

        if (s.HasWarning)
            _body.Children.Add(Banner("⚠ 检测到高危项（重映射 / 待映射 / 无法纠正扇区 > 0），建议尽快备份数据。"));

        if (s.Attributes.Count > 0) _body.Children.Add(AttributeTable(s));
        else if (s.IsNvme && s.HasCounters) _body.Children.Add(NvmeAttributeTable(s));

        if (!s.HasCounters)
        {
            if (_elevated)
            {
                // Already admin: the initial read already used full privileges, so re-elevating can't help —
                // this drive / controller simply doesn't expose standard ATA SMART (some USB / RAID / NVMe bridges).
                _body.Children.Add(Note("该驱动器 / 控制器未提供 SMART 属性（部分 USB / RAID / NVMe 桥接控制器不支持标准 ATA SMART 读取）。"));
            }
            else
            {
                _body.Children.Add(Note("温度 / 寿命 / SMART 属性需要管理员权限才能读取（机械盘 C5/C6、固态盘读写量等）。"));
                if (offerElevate)
                {
                    var btn = new Button { Content = "以管理员身份读取完整 SMART", Margin = new Thickness(0, 10, 0, 0), HorizontalAlignment = HorizontalAlignment.Left };
                    if (Application.Current.TryFindResource("InfoButton") is Style st) btn.Style = st;
                    btn.Click += async (_, _) =>
                    {
                        btn.IsEnabled = false; btn.Content = "读取中 …";
                        var s2 = await SystemInfo.GetSmartElevatedAsync(_deviceId);
                        if (s2.HasCounters) Render(s2, offerElevate: false);
                        else { Render(s, offerElevate: false); _body.Children.Add(Note("未获取到 SMART 属性（已取消授权，或该控制器/驱动器不支持 ATA SMART，如部分 NVMe / USB / RAID）。")); }
                    };
                    _body.Children.Add(btn);
                }
            }
        }
    }

    private Border SerialRow(SmartInfo s)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(LabelCol) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        grid.Children.Add(Col(new TextBlock { Text = "序列号", FontSize = 13, Foreground = Brush("TextSecondary"), VerticalAlignment = VerticalAlignment.Center }, 0));
        var serial = string.IsNullOrWhiteSpace(s.Serial) ? null : s.Serial;
        var shown = serial == null ? "不可用" : _showSerial ? serial : new string('•', Math.Min(16, Math.Max(6, serial.Length)));
        grid.Children.Add(Col(new TextBlock { Text = shown, FontSize = 13, FontFamily = new FontFamily("Consolas"), Foreground = Brush("TextPrimary"), VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis }, 1));
        if (serial != null)
        {
            var toggle = new Button { Content = _showSerial ? "隐藏" : "显示", FontSize = 11, Padding = new Thickness(8, 2, 8, 2), VerticalAlignment = VerticalAlignment.Center };
            if (Application.Current.TryFindResource("MiniButton") is Style st) toggle.Style = st;
            toggle.Click += (_, _) => { _showSerial = !_showSerial; if (_last != null) Render(_last, _offerElevate); };
            grid.Children.Add(Col(toggle, 2));
        }
        return new Border { Padding = new Thickness(0, 5, 0, 5), Child = grid };
    }

    private Border AttributeTable(SmartInfo s)
    {
        var outer = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };
        outer.Children.Add(new TextBlock { Text = "SMART 属性", FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = Brush("TextPrimary"), Margin = new Thickness(0, 0, 0, 6) });
        outer.Children.Add(MakeRow("ID", "属性", "当前", "最差", "阈值", "原始值", header: true, critical: false));

        foreach (var a in s.Attributes)
            outer.Children.Add(MakeRow(a.IdHex, a.Name, a.Current.ToString(), a.Worst.ToString(),
                a.Threshold > 0 ? a.Threshold.ToString() : "—", a.Raw.ToString(), header: false, critical: a.Critical));

        return new Border { Child = outer };
    }

    /// <summary>The NVMe equivalent of the ATA attribute table: a detailed dump of the NVMe SMART/Health log
    /// (log page 0x02). NVMe has no normalized current/worst/threshold model, so it's a 字段 / 值 table.</summary>
    private Border NvmeAttributeTable(SmartInfo s)
    {
        var outer = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };
        outer.Children.Add(new TextBlock { Text = "SMART 属性（NVMe Health Log · 0x02）", FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = Brush("TextPrimary"), Margin = new Thickness(0, 0, 0, 6) });
        outer.Children.Add(NvmeRow("字段", "值", header: true, danger: false));
        foreach (var (name, value, danger) in NvmeRows(s))
            outer.Children.Add(NvmeRow(name, value, header: false, danger: danger));
        return new Border { Child = outer };
    }

    private Grid NvmeRow(string name, string value, bool header, bool danger)
    {
        var g = new Grid { Margin = new Thickness(0, 2, 0, 2) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(176) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var fg = header ? Brush("TextTertiary") : danger ? Brush("FailFg") : Brush("TextPrimary");
        var fs = header ? 11.0 : 12.0;
        var fw = danger ? FontWeights.SemiBold : FontWeights.Normal;
        g.Children.Add(Col(new TextBlock { Text = name, FontSize = fs, Foreground = fg, FontWeight = fw, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center }, 0));
        g.Children.Add(Col(new TextBlock { Text = value, FontSize = fs, Foreground = fg, FontWeight = fw, FontFamily = header ? SystemFonts.MessageFontFamily : new FontFamily("Consolas"), TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center }, 1));
        return g;
    }

    private IEnumerable<(string Name, string Value, bool Danger)> NvmeRows(SmartInfo s)
    {
        var rows = new List<(string, string, bool)>();
        void Add(string name, string? value, bool danger = false) { if (!string.IsNullOrEmpty(value)) rows.Add((name, value!, danger)); }

        Add("严重警告", s.CriticalWarning is int cw ? $"0x{cw:X2} · {NvmeWarn(cw)}" : null, (s.CriticalWarning ?? 0) != 0);
        Add("复合温度", s.Temperature is int t ? $"{t} °C" : null);
        Add("可用备件", s.AvailableSpare is int sp ? $"{sp}%" : null, s.AvailableSpareThreshold is int t0 && t0 > 0 && (s.AvailableSpare ?? 100) < t0);
        Add("可用备件阈值", s.AvailableSpareThreshold is int th ? $"{th}%" : null);
        Add("已用寿命", s.PercentageUsed is int pu ? $"{pu}%" : null, (s.PercentageUsed ?? 0) >= 90);
        Add("剩余寿命", s.RemainingLifePercent is int life ? $"{life}%" : null, (s.RemainingLifePercent ?? 100) <= 10);
        Add("数据写入量", s.HostWritesBytes is long hw ? Gb(hw) : null);
        Add("数据读取量", s.HostReadsBytes is long hr ? Gb(hr) : null);
        Add("主机写入命令数", s.HostWriteCommands?.ToString("N0"));
        Add("主机读取命令数", s.HostReadCommands?.ToString("N0"));
        Add("控制器繁忙时间", s.ControllerBusyMinutes is long cb ? $"{cb:N0} 分钟" : null);
        Add("通电周期", s.PowerCycles?.ToString("N0"));
        Add("通电时间", s.PowerOnHours is long poh ? $"{poh:N0} 小时" : null);
        Add("不安全关机", s.UnsafeShutdowns?.ToString("N0"));
        Add("介质与完整性错误", s.MediaErrors?.ToString("N0"), (s.MediaErrors ?? 0) > 0);
        Add("错误信息日志条目数", s.ErrorLogEntries?.ToString("N0"));
        return rows;
    }

    private Grid MakeRow(string id, string name, string cur, string worst, string thr, string raw, bool header, bool critical)
    {
        var g = new Grid { Margin = new Thickness(0, 2, 0, 2) };
        foreach (var w in new[] { 42.0, 0, 44, 44, 44, 96 })
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = w == 0 ? new GridLength(1, GridUnitType.Star) : new GridLength(w) });
        var fg = header ? Brush("TextTertiary") : critical ? Brush("FailFg") : Brush("TextPrimary");
        var fs = header ? 11.0 : 11.5;
        var fw = critical ? FontWeights.SemiBold : FontWeights.Normal;
        string[] cells = { id, name, cur, worst, thr, raw };
        for (var i = 0; i < cells.Length; i++)
            g.Children.Add(Col(new TextBlock { Text = cells[i], FontSize = fs, Foreground = fg, FontWeight = fw, FontFamily = i is 0 or 5 ? new FontFamily("Consolas") : SystemFonts.MessageFontFamily, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center }, i));
        return g;
    }

    private static UIElement Col(UIElement el, int col) { Grid.SetColumn(el, col); return el; }

    private TextBlock Note(string text) => new()
    {
        Text = text, FontSize = 11.5, Foreground = Brush("TextTertiary"),
        TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0),
    };

    private Border Banner(string text) => new()
    {
        Background = Brush("WarnBg"), CornerRadius = new CornerRadius(6), Padding = new Thickness(10, 7, 10, 7), Margin = new Thickness(0, 10, 0, 0),
        Child = new TextBlock { Text = text, FontSize = 12, Foreground = Brush("WarnFg"), TextWrapping = TextWrapping.Wrap },
    };

    private Border Row(string label, string value, bool? healthy = null, bool danger = false)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(LabelCol) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.Children.Add(Col(new TextBlock { Text = label, FontSize = 13, Foreground = Brush("TextSecondary"), VerticalAlignment = VerticalAlignment.Center }, 0));
        grid.Children.Add(Col(new TextBlock
        {
            Text = value, FontSize = 13, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center,
            Foreground = danger ? Brush("FailFg") : healthy switch { true => Brush("OkFg"), false => Brush("WarnFg"), _ => Brush("TextPrimary") },
            FontWeight = healthy.HasValue || danger ? FontWeights.SemiBold : FontWeights.Normal,
        }, 1));
        return new Border { Padding = new Thickness(0, 5, 0, 5), Child = grid };
    }

    /// <summary>Decode the NVMe Critical Warning bitfield (byte 0 of the health log) into a readable summary.</summary>
    private static string NvmeWarn(int? cw)
    {
        if (cw is not int w) return "不可用";
        if (w == 0) return "正常";
        var bits = new List<string>();
        if ((w & 0x01) != 0) bits.Add("可用备件不足");
        if ((w & 0x02) != 0) bits.Add("温度越限");
        if ((w & 0x04) != 0) bits.Add("可靠性下降");
        if ((w & 0x08) != 0) bits.Add("介质只读");
        if ((w & 0x10) != 0) bits.Add("备份电容失效");
        if ((w & 0x20) != 0) bits.Add("持久内存异常");
        return bits.Count > 0 ? string.Join("、", bits) : $"0x{w:X2}";
    }

    private static string Gb(long bytes) => bytes >= 1024L * 1024 * 1024 * 1024
        ? $"{bytes / 1024.0 / 1024 / 1024 / 1024:0.00} TB"
        : $"{bytes / 1024.0 / 1024 / 1024:0.0} GB";

    private static bool IsElevated()
    {
        try
        {
            using var id = System.Security.Principal.WindowsIdentity.GetCurrent();
            return new System.Security.Principal.WindowsPrincipal(id).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        }
        catch { return false; }
    }

    private static Brush Brush(string key) => Application.Current.TryFindResource(key) as Brush ?? Brushes.Gray;
}
