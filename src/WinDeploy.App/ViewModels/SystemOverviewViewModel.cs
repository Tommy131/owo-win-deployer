using System.Collections.ObjectModel;
using System.Windows;
using WinDeploy.App.Services;
using WinDeploy.Core.Engine;

namespace WinDeploy.App.ViewModels;

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
}

/// <summary>The "系统概览" page: a one-glance health board — OS, CPU, RAM, drives (+ SMART), battery,
/// Windows activation — plus a one-click installed-software inventory export.</summary>
public sealed class SystemOverviewViewModel : ObservableObject
{
    public ObservableCollection<DiskRowViewModel> Disks { get; } = new();
    public ObservableCollection<PhysDiskRowViewModel> PhysicalDisks { get; } = new();
    public RelayCommand RefreshCommand { get; }
    public RelayCommand ExportInventoryCommand { get; }
    public RelayCommand ShowSmartCommand { get; }

    public SystemOverviewViewModel()
    {
        RefreshCommand = new RelayCommand(_ => _ = LoadAsync());
        ExportInventoryCommand = new RelayCommand(_ => _ = ExportInventoryAsync());
        ShowSmartCommand = new RelayCommand(p =>
        {
            if (p is PhysDiskRowViewModel r)
                new Views.SmartDialog(r.Name, r.DeviceId) { Owner = System.Windows.Application.Current.MainWindow }.ShowDialog();
        });
        _ = LoadAsync();
    }

    private bool _isLoading;
    public bool IsLoading { get => _isLoading; set { if (Set(ref _isLoading, value)) OnPropertyChanged(nameof(IsReady)); } }
    public bool IsReady => !_isLoading;

    private string _os = "", _cpu = "", _ram = "", _machine = "", _uptime = "", _activation = "", _battery = "";
    public string Os { get => _os; set => Set(ref _os, value); }
    public string Cpu { get => _cpu; set => Set(ref _cpu, value); }
    public string Ram { get => _ram; set => Set(ref _ram, value); }
    public string Machine { get => _machine; set => Set(ref _machine, value); }
    public string Uptime { get => _uptime; set => Set(ref _uptime, value); }
    public string Activation { get => _activation; set => Set(ref _activation, value); }
    public string Battery { get => _battery; set => Set(ref _battery, value); }

    private string _inventoryNote = "";
    public string InventoryNote { get => _inventoryNote; set => Set(ref _inventoryNote, value); }

    private async Task LoadAsync()
    {
        IsLoading = true;
        var s = await SystemInfo.GetAsync();

        Os = $"{s.OsCaption} · {s.Arch} · {s.OsVersion}";
        Cpu = $"{s.CpuName}  ·  {s.Cores} 核 {s.Threads} 线程" + (s.CpuLoad > 0 ? $"  ·  负载 {s.CpuLoad}%" : "");
        var usedKb = Math.Max(0, s.TotalMemKb - s.FreeMemKb);
        var memPct = s.TotalMemKb > 0 ? usedKb * 100.0 / s.TotalMemKb : 0;
        Ram = $"{Gb(usedKb * 1024)} / {Gb(s.TotalMemKb * 1024)}  ·  {memPct:0}%";
        Machine = $"{s.Manufacturer} {s.Model}".Trim() + (string.IsNullOrWhiteSpace(s.User) ? "" : $"  ·  {s.User}");
        Uptime = s.UptimeHours >= 24 ? $"已运行 {(int)(s.UptimeHours / 24)} 天 {(int)(s.UptimeHours % 24)} 小时" : $"已运行 {s.UptimeHours:0.0} 小时";
        Activation = s.Activation switch { 1 => "Windows 已激活", null => "激活状态未知", _ => "Windows 未激活" };
        Battery = s.BatteryCharge is int c ? $"电池 {c}%" : "无电池（台式机 / 未检测）";

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
                Text = $"{Gb(d.FreeBytes)} 可用 / {Gb(d.SizeBytes)}",
                UsedPercent = pct,
                Low = freePct < 10,
            });
        }

        PhysicalDisks.Clear();
        foreach (var p in s.PhysicalDisks)
            PhysicalDisks.Add(new PhysDiskRowViewModel
            {
                Name = p.Name,
                Detail = $"{p.Media} · {p.SizeGb} GB",
                Health = string.IsNullOrWhiteSpace(p.Health) ? "未知" : p.Health,
                Healthy = string.Equals(p.Health, "Healthy", StringComparison.OrdinalIgnoreCase),
                DeviceId = p.DeviceId,
            });

        IsLoading = false;
    }

    private async Task ExportInventoryAsync()
    {
        InventoryNote = "正在读取已装软件清单 …";
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = "导出软件清单",
            FileName = $"软件清单-{Environment.MachineName}.html",
            Filter = "HTML 网页 (*.html)|*.html|CSV 表格 (*.csv)|*.csv|JSON (*.json)|*.json",
        };
        if (dlg.ShowDialog() != true) { InventoryNote = ""; return; }

        var items = await Inventory.ListAsync();
        var ext = System.IO.Path.GetExtension(dlg.FileName).ToLowerInvariant();
        var text = ext switch { ".csv" => Inventory.ToCsv(items), ".json" => Inventory.ToJson(items), _ => Inventory.ToHtml(items) };
        try
        {
            await System.IO.File.WriteAllTextAsync(dlg.FileName, text);
            AuditLog.Action($"导出软件清单：{items.Count} 项 → {dlg.FileName}");
            InventoryNote = $"已导出 {items.Count} 项 → {dlg.FileName}";
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(dlg.FileName) { UseShellExecute = true });
        }
        catch (Exception ex) { InventoryNote = "导出失败：" + ex.Message; }
    }

    private static string Gb(long bytes) => bytes >= 1024L * 1024 * 1024
        ? $"{bytes / 1024.0 / 1024 / 1024:0.0} GB" : $"{bytes / 1024.0 / 1024:0.0} MB";
}
