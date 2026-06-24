using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using WinDeploy.App.Services;
using WinDeploy.Core.I18n;

namespace WinDeploy.App.Views.Sys;

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

        var close = new Button { Content = Localizer.T("common.close"), MinWidth = 72, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0), IsCancel = true };
        if (Application.Current.TryFindResource("MiniButton") is Style s) close.Style = s;
        close.Click += (_, _) => Close();
        DockPanel.SetDock(close, Dock.Bottom);
        dock.Children.Add(close);

        var content = new StackPanel();
        content.Children.Add(new TextBlock { Text = title, FontSize = 15, FontWeight = FontWeights.SemiBold, Foreground = Brush("TextPrimary"), TextTrimming = TextTrimming.CharacterEllipsis });
        content.Children.Add(new TextBlock { Text = Localizer.T("sysov.smart.heading"), FontSize = 12, Foreground = Brush("TextTertiary"), Margin = new Thickness(0, 2, 0, 0) });

        _body = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };
        _body.Children.Add(Row(Localizer.T("sysov.smart.reading"), ""));
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
            _body.Children.Add(Row(Localizer.T("sysov.smart.unableRead"), Localizer.T("sysov.smart.unableRead.detail")));
            return;
        }

        var na = Localizer.T("sysov.smart.na");
        var unknown = Localizer.T("common.unknown");
        _body.Children.Add(Row(Localizer.T("sysov.smart.model"), s.Model ?? s.Friendly));
        _body.Children.Add(SerialRow(s));
        _body.Children.Add(Row(Localizer.T("sysov.smart.capacity"), s.SizeBytes > 0 ? Gb(s.SizeBytes) : unknown));
        _body.Children.Add(Row(Localizer.T("sysov.smart.health"), s.Health ?? unknown, healthy: string.Equals(s.Health, "Healthy", StringComparison.OrdinalIgnoreCase)));
        _body.Children.Add(Row(Localizer.T("sysov.smart.mediaBus"), $"{s.Media ?? unknown} · {s.Bus ?? unknown}"));
        _body.Children.Add(Row(Localizer.T("sysov.smart.temperature"), s.Temperature is int t ? $"{t} °C" : na));
        _body.Children.Add(Row(Localizer.T("sysov.smart.poh"), s.PowerOnHours is long h ? Localizer.Format("sysov.smart.poh.value", h, h / 24) : na));
        _body.Children.Add(Row(Localizer.T("sysov.smart.powerCycles"), s.PowerCycles?.ToString() ?? na));

        if (s.IsSsd)
        {
            _body.Children.Add(Row(Localizer.T("sysov.smart.remainingLife"), s.RemainingLifePercent is int life ? $"{life}%" : na,
                danger: s.RemainingLifePercent is int lp && lp <= 10));
            if (s.IsNvme && s.PercentageUsed is int pu)
                _body.Children.Add(Row(Localizer.T("sysov.smart.usedLife"), $"{pu}%", danger: pu >= 90));
            // Some SATA SSDs (e.g. Samsung 860 EVO) only expose Total LBAs Written (0xF1) and omit reads (0xF2);
            // distinguish "drive doesn't report it" from a read failure. NVMe always reports both.
            _body.Children.Add(Row(Localizer.T("sysov.smart.writesTotal"), s.HostWritesBytes is long hw ? Gb(hw) : s.IsNvme ? na : Localizer.T("sysov.smart.writesTotal.unreported")));
            _body.Children.Add(Row(Localizer.T("sysov.smart.readsTotal"), s.HostReadsBytes is long hr ? Gb(hr) : s.IsNvme ? na : Localizer.T("sysov.smart.readsTotal.unreported")));
            if (s.IsNvme)
            {
                if (s.AvailableSpare is int sp)
                    _body.Children.Add(Row(Localizer.T("sysov.smart.ssd.availSpare"), $"{sp}%" + (s.AvailableSpareThreshold is int th ? Localizer.Format("sysov.smart.spareThreshold.suffix", th) : ""),
                        danger: s.AvailableSpareThreshold is int t2 && t2 > 0 && sp < t2));
                _body.Children.Add(Row(Localizer.T("sysov.smart.nvmeMediaErrors"), s.MediaErrors?.ToString() ?? na, danger: (s.MediaErrors ?? 0) > 0));
                _body.Children.Add(Row(Localizer.T("sysov.smart.nvmeUnsafeShutdowns"), s.UnsafeShutdowns?.ToString() ?? na));
                _body.Children.Add(Row(Localizer.T("sysov.smart.nvmeErrorLog"), s.ErrorLogEntries?.ToString() ?? na));
                _body.Children.Add(Row(Localizer.T("sysov.smart.nvmeSevereWarning"), NvmeWarn(s.CriticalWarning), danger: (s.CriticalWarning ?? 0) != 0));
            }
        }
        else
        {
            _body.Children.Add(Row(Localizer.T("sysov.smart.hdd.reallocated"), s.Reallocated?.ToString() ?? na, danger: (s.Reallocated ?? 0) > 0));
            _body.Children.Add(Row(Localizer.T("sysov.smart.hdd.pending"), s.Pending?.ToString() ?? na, danger: (s.Pending ?? 0) > 0));
            _body.Children.Add(Row(Localizer.T("sysov.smart.hdd.uncorrectable"), s.Uncorrectable?.ToString() ?? na, danger: (s.Uncorrectable ?? 0) > 0));
            _body.Children.Add(Row(Localizer.T("sysov.smart.hdd.crc"), s.Crc?.ToString() ?? na, danger: (s.Crc ?? 0) > 0));
        }

        if (s.HasWarning)
            _body.Children.Add(Banner(Localizer.T("sysov.smart.banner.highRisk")));

        if (s.Attributes.Count > 0) _body.Children.Add(AttributeTable(s));
        else if (s.IsNvme && s.HasCounters) _body.Children.Add(NvmeAttributeTable(s));

        if (!s.HasCounters)
        {
            if (_elevated)
            {
                // Already admin: the initial read already used full privileges, so re-elevating can't help —
                // this drive / controller simply doesn't expose standard ATA SMART (some USB / RAID / NVMe bridges).
                _body.Children.Add(Note(Localizer.T("sysov.smart.note.elevated")));
            }
            else if (IsExternalBus(s.Bus))
            {
                // USB / external bridge: the per-read WMI elevation can't reach it — only the in-process SAT
                // (SCSI/ATA) pass-through can, and that needs the whole app elevated. Offer to relaunch as admin.
                _body.Children.Add(Note(Localizer.T("sysov.smart.note.external")));
                var relaunch = new Button { Content = Localizer.T("sysov.adminRun.full"), Margin = new Thickness(0, 10, 0, 0), HorizontalAlignment = HorizontalAlignment.Left };
                if (Application.Current.TryFindResource("WarnButton") is Style ws) relaunch.Style = ws;
                relaunch.Click += (_, _) => RelaunchAsAdmin();
                _body.Children.Add(relaunch);
            }
            else
            {
                _body.Children.Add(Note(Localizer.T("sysov.smart.note.local")));
                if (offerElevate)
                {
                    var btn = new Button { Content = Localizer.T("sysov.smart.elevateBtn"), Margin = new Thickness(0, 10, 0, 0), HorizontalAlignment = HorizontalAlignment.Left };
                    if (Application.Current.TryFindResource("InfoButton") is Style st) btn.Style = st;
                    btn.Click += async (_, _) =>
                    {
                        btn.IsEnabled = false; btn.Content = Localizer.T("sysov.smart.reading");
                        var s2 = await SystemInfo.GetSmartElevatedAsync(_deviceId);
                        if (s2.HasCounters) Render(s2, offerElevate: false);
                        else { Render(s, offerElevate: false); _body.Children.Add(Note(Localizer.T("sysov.smart.note.noAttr"))); }
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

        grid.Children.Add(Col(new TextBlock { Text = Localizer.T("sysov.smart.serial"), FontSize = 13, Foreground = Brush("TextSecondary"), VerticalAlignment = VerticalAlignment.Center }, 0));
        var serial = string.IsNullOrWhiteSpace(s.Serial) ? null : s.Serial;
        var shown = serial == null ? Localizer.T("sysov.smart.na") : _showSerial ? serial : new string('•', Math.Min(16, Math.Max(6, serial.Length)));
        grid.Children.Add(Col(new TextBlock { Text = shown, FontSize = 13, FontFamily = new FontFamily("Consolas"), Foreground = Brush("TextPrimary"), VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis }, 1));
        if (serial != null)
        {
            var toggle = new Button { Content = _showSerial ? Localizer.T("sysov.smart.serial.hide") : Localizer.T("sysov.smart.serial.show"), FontSize = 11, Padding = new Thickness(8, 2, 8, 2), VerticalAlignment = VerticalAlignment.Center };
            if (Application.Current.TryFindResource("MiniButton") is Style st) toggle.Style = st;
            toggle.Click += (_, _) => { _showSerial = !_showSerial; if (_last != null) Render(_last, _offerElevate); };
            grid.Children.Add(Col(toggle, 2));
        }
        return new Border { Padding = new Thickness(0, 5, 0, 5), Child = grid };
    }

    private Border AttributeTable(SmartInfo s)
    {
        var outer = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };
        outer.Children.Add(new TextBlock { Text = Localizer.T("sysov.smart.attrTable.title"), FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = Brush("TextPrimary"), Margin = new Thickness(0, 0, 0, 6) });
        outer.Children.Add(MakeRow("ID", Localizer.T("sysov.smart.attr.name"), Localizer.T("sysov.smart.attr.cur"), Localizer.T("sysov.smart.attr.worst"), Localizer.T("sysov.smart.attr.thr"), Localizer.T("sysov.smart.attr.raw"), header: true, critical: false));

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
        outer.Children.Add(new TextBlock { Text = Localizer.T("sysov.smart.nvme.tableTitle"), FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = Brush("TextPrimary"), Margin = new Thickness(0, 0, 0, 6) });
        outer.Children.Add(NvmeRow(Localizer.T("sysov.smart.nvme.field"), Localizer.T("sysov.smart.nvme.value"), header: true, danger: false));
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

        Add(Localizer.T("sysov.smart.nvme.criticalWarning"), s.CriticalWarning is int cw ? $"0x{cw:X2} · {NvmeWarn(cw)}" : null, (s.CriticalWarning ?? 0) != 0);
        Add(Localizer.T("sysov.smart.nvme.compositeTemp"), s.Temperature is int t ? $"{t} °C" : null);
        Add(Localizer.T("sysov.smart.nvme.availSpare"), s.AvailableSpare is int sp ? $"{sp}%" : null, s.AvailableSpareThreshold is int t0 && t0 > 0 && (s.AvailableSpare ?? 100) < t0);
        Add(Localizer.T("sysov.smart.nvme.availSpareThreshold"), s.AvailableSpareThreshold is int th ? $"{th}%" : null);
        Add(Localizer.T("sysov.smart.nvme.usedLife"), s.PercentageUsed is int pu ? $"{pu}%" : null, (s.PercentageUsed ?? 0) >= 90);
        Add(Localizer.T("sysov.smart.remainingLife"), s.RemainingLifePercent is int life ? $"{life}%" : null, (s.RemainingLifePercent ?? 100) <= 10);
        Add(Localizer.T("sysov.smart.nvme.dataWritten"), s.HostWritesBytes is long hw ? Gb(hw) : null);
        Add(Localizer.T("sysov.smart.nvme.dataRead"), s.HostReadsBytes is long hr ? Gb(hr) : null);
        Add(Localizer.T("sysov.smart.nvme.hostWriteCmds"), s.HostWriteCommands?.ToString("N0"));
        Add(Localizer.T("sysov.smart.nvme.hostReadCmds"), s.HostReadCommands?.ToString("N0"));
        Add(Localizer.T("sysov.smart.nvme.controllerBusy"), s.ControllerBusyMinutes is long cb ? Localizer.Format("sysov.smart.nvme.minutes", cb.ToString("N0")) : null);
        Add(Localizer.T("sysov.smart.nvme.powerCycles"), s.PowerCycles?.ToString("N0"));
        Add(Localizer.T("sysov.smart.nvme.powerOnHours"), s.PowerOnHours is long poh ? Localizer.Format("sysov.smart.nvmeHours", poh.ToString("N0")) : null);
        Add(Localizer.T("sysov.smart.nvme.unsafeShutdowns"), s.UnsafeShutdowns?.ToString("N0"));
        Add(Localizer.T("sysov.smart.nvme.mediaErrors"), s.MediaErrors?.ToString("N0"), (s.MediaErrors ?? 0) > 0);
        Add(Localizer.T("sysov.smart.nvme.errorLog"), s.ErrorLogEntries?.ToString("N0"));
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
        if (cw is not int w) return Localizer.T("sysov.smart.na");
        if (w == 0) return Localizer.T("sysov.smart.warn.normal");
        var bits = new List<string>();
        if ((w & 0x01) != 0) bits.Add(Localizer.T("sysov.smart.warn.spareLow"));
        if ((w & 0x02) != 0) bits.Add(Localizer.T("sysov.smart.warn.tempExceeded"));
        if ((w & 0x04) != 0) bits.Add(Localizer.T("sysov.smart.warn.reliabilityDegraded"));
        if ((w & 0x08) != 0) bits.Add(Localizer.T("sysov.smart.warn.mediaReadOnly"));
        if ((w & 0x10) != 0) bits.Add(Localizer.T("sysov.smart.warn.backupCapFail"));
        if ((w & 0x20) != 0) bits.Add(Localizer.T("sysov.smart.warn.persistentMem"));
        return bits.Count > 0 ? string.Join(Localizer.T("sysov.smart.warn.sep"), bits) : $"0x{w:X2}";
    }

    private static string Gb(long bytes) => bytes >= 1024L * 1024 * 1024 * 1024
        ? $"{bytes / 1024.0 / 1024 / 1024 / 1024:0.00} TB"
        : $"{bytes / 1024.0 / 1024 / 1024:0.0} GB";

    /// <summary>True for drives reached through an external bridge (USB / FireWire / SCSI / SAS), whose SMART
    /// can only be read via the admin-only SAT pass-through — so we offer an app relaunch rather than the
    /// per-read WMI elevation (which can't reach them).</summary>
    private static bool IsExternalBus(string? bus)
    {
        bus = (bus ?? "").Trim();
        return bus.Equals("USB", StringComparison.OrdinalIgnoreCase) || bus == "1394"
            || bus.Equals("SCSI", StringComparison.OrdinalIgnoreCase) || bus.Equals("SAS", StringComparison.OrdinalIgnoreCase);
    }

    private static void RelaunchAsAdmin()
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe)) return;
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe) { UseShellExecute = true, Verb = "runas" });
            System.Windows.Application.Current.Shutdown();
        }
        catch (System.ComponentModel.Win32Exception) { /* user declined UAC */ }
        catch { /* ignore */ }
    }

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
