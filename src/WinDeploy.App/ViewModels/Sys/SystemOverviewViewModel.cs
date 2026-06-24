using System.Collections.ObjectModel;
using System.Security.Principal;
using System.Windows.Threading;
using WinDeploy.App.Services;
using WinDeploy.Core.Engine;
using WinDeploy.Core.I18n;

namespace WinDeploy.App.ViewModels.Sys;

public sealed class DiskRowViewModel
{
    public string Drive { get; init; } = "";
    public string Label { get; init; } = "";
    public string Text { get; init; } = "";
    public double UsedPercent { get; init; }
    public bool Low { get; init; }   // < 10% free
}

public sealed class PhysDiskRowViewModel
{
    public string Name { get; init; } = "";
    public string Detail { get; init; } = "";
    public string Health { get; init; } = "";
    public bool Healthy { get; init; }
    public string? DeviceId { get; init; }
    public string? Bus { get; init; }

    /// <summary>True for hot-pluggable drives (USB / FireWire / card reader) — surfaces the 弹出 button so the
    /// device can be safely removed without leaving the app.</summary>
    public bool IsRemovable => DeviceEject.IsRemovableBus(Bus);

    /// <summary>Classify a physical disk into a concise type, preferring the interface (NVMe / USB) over the
    /// raw media type, so an NVMe SSD reads "NVMe" (not "SSD") and an external drive reads "USB". Shown in the
    /// card's left detail line.</summary>
    public static string Classify(string? media, string? bus)
    {
        var b = (bus ?? "").Trim();
        var m = (media ?? "").Trim();
        if (b.Equals("USB", StringComparison.OrdinalIgnoreCase) || b == "1394") return "USB";
        if (b.Equals("NVMe", StringComparison.OrdinalIgnoreCase)) return "NVMe";
        if (m.Equals("SSD", StringComparison.OrdinalIgnoreCase)) return "SSD";
        if (m.Equals("HDD", StringComparison.OrdinalIgnoreCase)) return "HDD";
        if (m.Equals("SCM", StringComparison.OrdinalIgnoreCase)) return "SCM";
        if (b.Length > 0 && !b.Equals("Unknown", StringComparison.OrdinalIgnoreCase)) return b;   // SD / MMC / virtual …
        return m.Length > 0 ? m : Localizer.T("common.unknown");
    }
}

public sealed class PowerRowViewModel
{
    /// <summary>Internal device-kind identifier (matches the hardware monitor's grouping key, e.g. "CPU",
    /// "GPU", "硬盘"). Used for layout/scaling logic — not shown directly.</summary>
    public string Kind { get; init; } = "";

    /// <summary>Localized badge label for <see cref="Kind"/>. CPU / GPU stay as their acronyms; the
    /// hardware monitor's Chinese category keys map to translated labels.</summary>
    public string KindLabel => Kind switch
    {
        "硬盘" => Localizer.T("sysov.power.kind.disk"),
        "内存" => Localizer.T("sysov.power.kind.memory"),
        "其他" => Localizer.T("sysov.power.kind.other"),
        _ => Kind,
    };

    public string Name { get; init; } = "";
    public double Watts { get; init; }
    public string WattsText { get; init; } = "";
    public double BarPercent { get; init; }
    public string BarTip { get; init; } = "";
}

/// <summary>The "系统概览" page: a one-glance health board — OS, CPU, RAM, drives (+ SMART), battery,
/// Windows activation — plus live CPU/memory/power telemetry and a one-click software-inventory export.
/// CPU/memory/power refresh on a 2-second timer while the page is visible (see <see cref="StartLive"/>).</summary>
public sealed class SystemOverviewViewModel : LocalizedObject
{
    public ObservableCollection<DiskRowViewModel> Disks { get; } = new();
    public ObservableCollection<PhysDiskRowViewModel> PhysicalDisks { get; } = new();
    public ObservableCollection<PowerRowViewModel> Powers { get; } = new();
    public RelayCommand RefreshCommand { get; }
    public RelayCommand ExportInventoryCommand { get; }
    public RelayCommand ShowSmartCommand { get; }
    public RelayCommand EjectCommand { get; }
    public RelayCommand RelaunchAdminCommand { get; }

    private DispatcherTimer? _timer;
    private bool _sampling;
    private static readonly bool Elevated = IsElevated();
    private readonly Dictionary<string, double> _powerCeiling = new();   // per-kind TDP ceiling (peak-hold)

    public SystemOverviewViewModel()
    {
        RefreshCommand = new RelayCommand(_ => _ = LoadAsync());
        ExportInventoryCommand = new RelayCommand(_ => _ = ExportInventoryAsync());
        ShowSmartCommand = new RelayCommand(p =>
        {
            if (p is PhysDiskRowViewModel r)
                new SmartDialog(r.Name, r.DeviceId) { Owner = System.Windows.Application.Current.MainWindow }.ShowDialog();
        });
        EjectCommand = new RelayCommand(p => { if (p is PhysDiskRowViewModel r) _ = EjectAsync(r); });
        RelaunchAdminCommand = new RelayCommand(_ => RelaunchAsAdmin());
        _ = LoadAsync();
    }

    /// <summary>Safely eject a removable drive, then show a themed result popup: green when removed, amber with
    /// the reason when Windows vetoes it (device still in use). On success the device's cards are removed from the
    /// page immediately (see <see cref="RemoveDiskFromView"/>).</summary>
    private async Task EjectAsync(PhysDiskRowViewModel row)
    {
        if (!int.TryParse(row.DeviceId, out var idx))
        {
            ShowEjectResult(false, row.Name, Localizer.T("sysov.eject.noIndex"));
            return;
        }

        // Capture the disk's drive letters BEFORE ejecting — afterwards they're gone, so we couldn't look them up.
        var letters = await Task.Run(() => DeviceEject.VolumesOnDrive(idx));

        var result = await Task.Run(() => DeviceEject.Eject(idx));
        switch (result.Status)
        {
            case DeviceEject.EjectStatus.Success:
                AuditLog.Action($"已安全弹出设备：{row.Name}");
                RemoveDiskFromView(row, letters);
                ShowEjectResult(true, row.Name, Localizer.T("sysov.eject.success"));
                break;
            case DeviceEject.EjectStatus.NotFound:
                // Already gone — just drop it from the view.
                RemoveDiskFromView(row, letters);
                ShowEjectResult(false, row.Name, Localizer.T("sysov.eject.notFound"));
                break;
            default:   // Busy / Failed
                AuditLog.App($"弹出设备失败：{row.Name} — {result.Reason}");
                // Busy means something holds the device open — offer to force-dismount and retry.
                var allowForce = result.Status == DeviceEject.EjectStatus.Busy;
                var dlg = new EjectResultDialog(false, row.Name, result.Reason ?? Localizer.T("sysov.eject.busyFallback"), allowForce)
                { Owner = System.Windows.Application.Current.MainWindow };
                dlg.ShowDialog();
                if (dlg.ForceRequested) await ForceEjectAsync(row, idx, letters);
                break;
        }
    }

    /// <summary>Force-dismount the drive's volumes and retry the eject, after the user accepted the data-loss
    /// risk in the busy dialog.</summary>
    private async Task ForceEjectAsync(PhysDiskRowViewModel row, int idx, IReadOnlyList<char> letters)
    {
        var forced = await Task.Run(() => DeviceEject.ForceEject(idx));
        if (forced.Status == DeviceEject.EjectStatus.Success)
        {
            AuditLog.Action($"已强制弹出设备：{row.Name}");
            RemoveDiskFromView(row, letters);
            ShowEjectResult(true, row.Name, Localizer.T("sysov.eject.forced"));
        }
        else
        {
            AuditLog.App($"强制弹出设备失败：{row.Name} — {forced.Reason}");
            ShowEjectResult(false, row.Name, forced.Reason ?? Localizer.T("sysov.eject.forceFail"));
        }
    }

    /// <summary>Remove an ejected disk from the page right away — its physical-disk card and each of its drive-letter
    /// cards — instead of reloading (the PnP removal lags, so a reload could momentarily re-list the gone device).</summary>
    private void RemoveDiskFromView(PhysDiskRowViewModel row, IReadOnlyList<char> letters)
    {
        PhysicalDisks.Remove(row);
        foreach (var letter in letters)
        {
            var key = letter + ":";
            foreach (var d in Disks.Where(d => string.Equals(d.Drive, key, StringComparison.OrdinalIgnoreCase)).ToList())
                Disks.Remove(d);
        }
    }

    private static void ShowEjectResult(bool success, string name, string message)
        => new EjectResultDialog(success, name, message) { Owner = System.Windows.Application.Current.MainWindow }.ShowDialog();

    /// <summary>True when NOT elevated — surfaces the "以管理员身份运行" button (CPU 温度/功耗 需管理员).</summary>
    public bool ShowAdminHint => !Elevated;

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

    private bool _isLoading;
    public bool IsLoading { get => _isLoading; set { if (Set(ref _isLoading, value)) OnPropertyChanged(nameof(IsReady)); } }
    public bool IsReady => !_isLoading;

    private string _os = "", _cpu = "", _machine = "", _uptime = "", _activation = "", _battery = "";
    public string Os { get => _os; set => Set(ref _os, value); }
    public string Cpu { get => _cpu; set => Set(ref _cpu, value); }
    public string Machine { get => _machine; set => Set(ref _machine, value); }
    public string Uptime { get => _uptime; set => Set(ref _uptime, value); }
    public string Activation { get => _activation; set => Set(ref _activation, value); }
    public string Battery { get => _battery; set => Set(ref _battery, value); }

    // Live telemetry
    private string _cpuLive = Localizer.T("sysov.cpu.loadDash"), _ramLive = "—";
    public string CpuLive { get => _cpuLive; set => Set(ref _cpuLive, value); }
    /// <summary>Memory breakdown line, e.g. "已用 17.4 GB · 可用 14.5 GB · 共 31.9 GB".</summary>
    public string RamLive { get => _ramLive; set => Set(ref _ramLive, value); }

    private double _ramPercent;
    /// <summary>Memory usage 0–100 for the bar.</summary>
    public double RamPercent { get => _ramPercent; set => Set(ref _ramPercent, value); }

    private string _ramPercentText = "—";
    /// <summary>Memory usage as "55%".</summary>
    public string RamPercentText { get => _ramPercentText; set => Set(ref _ramPercentText, value); }

    private bool _ramHigh;
    /// <summary>True when memory is nearly full (≥90%) — drives the "占用偏高" badge.</summary>
    public bool RamHigh { get => _ramHigh; set => Set(ref _ramHigh, value); }

    private string _totalPowerText = "—";
    public string TotalPowerText { get => _totalPowerText; set => Set(ref _totalPowerText, value); }

    private bool _hasPower;
    public bool HasPower { get => _hasPower; set { if (Set(ref _hasPower, value)) OnPropertyChanged(nameof(NoPower)); } }
    public bool NoPower => !_hasPower;

    private string _powerNote = Localizer.T("sysov.power.note.reading");
    public string PowerNote { get => _powerNote; set => Set(ref _powerNote, value); }

    private string _inventoryNote = "";
    public string InventoryNote { get => _inventoryNote; set => Set(ref _inventoryNote, value); }

    // ── live timer ──────────────────────────────────────────────────────────
    public void StartLive()
    {
        HardwareMonitor.TryInit();
        _timer ??= new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _timer.Tick -= OnTick;
        _timer.Tick += OnTick;
        Tick();
        _timer.Start();
    }

    public void StopLive() => _timer?.Stop();

    private void OnTick(object? sender, EventArgs e) => Tick();

    private async void Tick()
    {
        if (_sampling) return;
        _sampling = true;
        try
        {
            var hw = await Task.Run(HardwareMonitor.Sample);
            ApplyLive(hw);
        }
        catch { /* sensor read can throw transiently */ }
        finally { _sampling = false; }
    }

    private void ApplyLive(HwSample hw)
    {
        // CPU — only show a temperature when it's a real reading (>0). AMD Tctl/Tdie needs admin (WinRing0
        // MSR); without it the sensor reads 0, so show a hint instead of a misleading "0 °C".
        if (hw.CpuLoad is double load)
        {
            var tempPart = hw.CpuTemp is double tc && tc > 0 ? $"  ·  {tc:0} °C"
                         : Elevated ? "" : Localizer.T("sysov.cpu.needAdminTemp");
            CpuLive = Localizer.Format("sysov.cpu.loadTemp", load.ToString("0"), tempPart);
        }

        // Memory
        if (hw.MemUsedGb is double used && hw.MemAvailGb is double avail && used + avail > 0)
            SetMemory(used, used + avail);

        // Power — one curated row per device kind (CPU / GPU / 硬盘 / 内存 / 其他)
        Powers.Clear();
        double sum = 0;
        var order = new[] { "CPU", "GPU", "硬盘", "内存", "其他" };
        foreach (var grp in hw.Powers.GroupBy(p => p.Kind).OrderBy(g => Array.IndexOf(order, g.Key) is var i && i >= 0 ? i : 99))
        {
            // prefer the package/total reading; otherwise the largest draw in that group
            var primary = grp.FirstOrDefault(p => p.Name.Contains("Package") || p.Name.Contains("Total"))
                          ?? grp.OrderByDescending(p => p.Watts).First();
            sum += primary.Watts;
            Powers.Add(new PowerRowViewModel
            {
                Kind = grp.Key,
                Name = primary.Name,
                Watts = primary.Watts,
                WattsText = $"{primary.Watts:0.0} W",
                BarPercent = 0, // filled below once total is known
            });
        }

        HasPower = Powers.Count > 0;
        if (HasPower)
        {
            // Scale each bar against the device's real power limit: the NVIDIA GPU's board limit (TGP) comes
            // from nvidia-smi (exact — e.g. 300 W for an RTX 5070 Ti); other devices use a peak-hold estimate
            // that converges to their realized max draw. Scaling against the largest current draw (or an
            // over-high nominal TDP) would leave the busiest device pinned full / never full.
            var scaled = Powers.Select(p =>
            {
                var (ceiling, exact) = PowerCeiling(p.Kind, p.Watts, hw.GpuPowerLimitW);
                var pct = ceiling > 0 ? Math.Min(100, p.Watts / ceiling * 100) : 0;
                return new PowerRowViewModel
                {
                    Kind = p.Kind, Name = p.Name, Watts = p.Watts, WattsText = p.WattsText,
                    BarPercent = pct,
                    BarTip = exact
                        ? Localizer.Format("sysov.powerTip.exact", pct.ToString("0"), ceiling.ToString("0"))
                        : Localizer.Format("sysov.powerTip.adaptive", pct.ToString("0"), ceiling.ToString("0")),
                };
            }).ToList();
            Powers.Clear();
            foreach (var p in scaled) Powers.Add(p);

            TotalPowerText = $"{sum:0.0} W";
            var hasCpuPower = Powers.Any(p => p.Kind == "CPU");
            PowerNote = (!hasCpuPower && !Elevated)
                ? Localizer.T("sysov.power.note.noCpu")
                : Localizer.T("sysov.power.note.estimate");
        }
        else
        {
            TotalPowerText = Localizer.T("sysov.power.unavailable");
            PowerNote = HardwareMonitor.Available
                ? (Elevated
                    ? Localizer.T("sysov.power.note.noSensorAdmin")
                    : Localizer.T("sysov.power.note.noSensorUser"))
                : Localizer.T("sysov.power.note.noDriver");
        }
    }

    /// <summary>The bar's 100% reference for a device kind, plus whether it's an exact hardware limit. The GPU
    /// uses the NVIDIA board power limit from nvidia-smi when available (exact). Everything else uses peak-hold:
    /// seeded with a LOW per-kind floor and grown to the highest wattage actually seen, so it converges to the
    /// device's realized max draw (PPT) instead of being pinned by an over-high nominal guess.</summary>
    private (double Ceiling, bool Exact) PowerCeiling(string kind, double watts, double? gpuLimitW)
    {
        // Exact: NVIDIA GPU board power limit (TGP). The card can momentarily draw slightly above it, so the
        // caller clamps the bar to 100%.
        if (kind == "GPU" && gpuLimitW is double gl && gl > 1)
            return (gl, true);

        // Floor = a typical lower-bound max for that device kind (desktop CPU TDP ≈ 65 W), so a lightly-loaded
        // device doesn't read as a full bar before a real peak is observed; it then grows to the realized peak.
        var floor = kind switch { "CPU" => 65.0, "GPU" => 120.0, "硬盘" => 6.0, "内存" => 10.0, _ => 25.0 };
        var c = _powerCeiling.TryGetValue(kind, out var v) ? v : floor;
        c = Math.Max(c, Math.Max(floor, watts));   // never below the floor; grow to realized peak
        _powerCeiling[kind] = c;
        return (c, false);
    }

    /// <summary>Apply used / total memory (GB) to the card consistently from both the live sampler and the
    /// initial WMI load: a labeled breakdown (已用 / 可用 / 共), the percent, and the bar value — so the
    /// numbers are self-explanatory instead of a bare "17.4 / 31.9 GB · 55%".</summary>
    private void SetMemory(double usedGb, double totalGb)
    {
        if (totalGb <= 0) return;
        usedGb = Math.Clamp(usedGb, 0, totalGb);
        var availGb = totalGb - usedGb;
        var pct = usedGb / totalGb * 100;
        RamPercent = pct;
        RamPercentText = $"{pct:0}%";
        RamHigh = pct >= 90;
        RamLive = Localizer.Format("sysov.mem.live", usedGb.ToString("0.0"), availGb.ToString("0.0"), totalGb.ToString("0.0"));
    }

    private static bool IsElevated()
    {
        try
        {
            using var id = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch { return false; }
    }

    private SystemSnapshot? _lastInfo;

    private async Task LoadAsync()
    {
        IsLoading = true;
        var s = await SystemInfo.GetAsync();
        _lastInfo = s;
        ApplyInfo(s);
        IsLoading = false;
    }

    /// <summary>Compose the snapshot's localized display lines. Re-invoked on language switch so the cached
    /// data (OS / CPU / uptime / activation / battery / disks) re-localizes without a reload.</summary>
    private void ApplyInfo(SystemSnapshot s)
    {
        Os = $"{s.OsCaption} · {s.Arch} · {s.OsVersion}";
        Cpu = $"{s.CpuName}  ·  " + Localizer.Format("sysov.cpu.cores", s.Cores, s.Threads);
        var usedKb = Math.Max(0, s.TotalMemKb - s.FreeMemKb);
        SetMemory(usedKb / 1024.0 / 1024, s.TotalMemKb / 1024.0 / 1024);
        if (s.CpuLoad > 0) CpuLive = Localizer.Format("sysov.cpu.load", s.CpuLoad);
        Machine = $"{s.Manufacturer} {s.Model}".Trim() + (string.IsNullOrWhiteSpace(s.User) ? "" : $"  ·  {s.User}");
        Uptime = s.UptimeHours >= 24
            ? Localizer.Format("sysov.uptime.days", (int)(s.UptimeHours / 24), (int)(s.UptimeHours % 24))
            : Localizer.Format("sysov.uptime.hours", s.UptimeHours.ToString("0.0"));
        Activation = s.Activation switch { 1 => Localizer.T("sysov.activation.active"), null => Localizer.T("sysov.activation.unknown"), _ => Localizer.T("sysov.activation.inactive") };
        Battery = s.BatteryCharge is int c ? Localizer.Format("sysov.battery.charge", c) : Localizer.T("sysov.battery.none");

        Disks.Clear();
        foreach (var d in s.Disks)
        {
            var used = Math.Max(0, d.SizeBytes - d.FreeBytes);
            var pct = d.SizeBytes > 0 ? used * 100.0 / d.SizeBytes : 0;
            var freePct = d.SizeBytes > 0 ? d.FreeBytes * 100.0 / d.SizeBytes : 100;
            Disks.Add(new DiskRowViewModel
            {
                Drive = d.Drive,
                Label = string.IsNullOrWhiteSpace(d.Label) ? "" : d.Label,
                Text = Localizer.Format("sysov.disk.free", Gb(d.FreeBytes), Gb(d.SizeBytes)),
                UsedPercent = pct,
                Low = freePct < 10,
            });
        }

        PhysicalDisks.Clear();
        foreach (var p in s.PhysicalDisks)
            PhysicalDisks.Add(new PhysDiskRowViewModel
            {
                Name = p.Name,
                Detail = $"{PhysDiskRowViewModel.Classify(p.Media, p.Bus)} · {p.SizeGb} GB",
                Health = string.IsNullOrWhiteSpace(p.Health) ? Localizer.T("common.unknown") : p.Health,
                Healthy = string.Equals(p.Health, "Healthy", StringComparison.OrdinalIgnoreCase),
                DeviceId = p.DeviceId,
                Bus = p.Bus,
            });
    }

    /// <summary>On language switch, re-localize the cached snapshot (no WMI reload) plus all bound props.</summary>
    protected override void OnCultureChanged()
    {
        if (_lastInfo is { } snap) ApplyInfo(snap);
        base.OnCultureChanged();
    }

    private async Task ExportInventoryAsync()
    {
        InventoryNote = Localizer.T("sysov.export.reading");
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = Localizer.T("sysov.export.title"),
            FileName = Localizer.Format("sysov.export.fileName", Environment.MachineName),
            Filter = Localizer.T("sysov.export.filter"),
        };
        if (dlg.ShowDialog() != true) { InventoryNote = ""; return; }

        var items = await Inventory.ListAsync();
        var ext = System.IO.Path.GetExtension(dlg.FileName).ToLowerInvariant();
        var text = ext switch { ".csv" => Inventory.ToCsv(items), ".json" => Inventory.ToJson(items), _ => Inventory.ToHtml(items) };
        try
        {
            await System.IO.File.WriteAllTextAsync(dlg.FileName, text);
            AuditLog.Action($"导出软件清单：{items.Count} 项 → {dlg.FileName}");
            InventoryNote = Localizer.Format("sysov.exported", items.Count, dlg.FileName);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(dlg.FileName) { UseShellExecute = true });
        }
        catch (Exception ex) { InventoryNote = Localizer.Format("sysov.export.failed", ex.Message); }
    }

    private static string Gb(long bytes) => bytes >= 1024L * 1024 * 1024
        ? $"{bytes / 1024.0 / 1024 / 1024:0.0} GB" : $"{bytes / 1024.0 / 1024:0.0} MB";
}
