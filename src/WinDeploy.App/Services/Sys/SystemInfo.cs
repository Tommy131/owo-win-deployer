using System.Text.Json;
using WinDeploy.Core.Util;

namespace WinDeploy.App.Services.Sys;

public sealed record DiskInfo(string Drive, long SizeBytes, long FreeBytes, string? Label);
public sealed record PhysDiskInfo(string Name, string? Media, string? Health, long SizeGb, string? DeviceId, string? Bus);

/// <summary>One parsed SMART attribute (raw ATA table). Raw is the 48-bit vendor raw value.</summary>
public sealed record SmartAttr(int Id, string Name, int Current, int Worst, int Threshold, long Raw, bool Critical)
{
    public string IdHex => $"0x{Id:X2}";
}

/// <summary>SMART / health for one physical disk. Identity (model/serial/capacity) comes from Win32_DiskDrive
/// (no admin); the raw ATA attribute table (temperature, C5/C6, host reads/writes, wear) needs admin.</summary>
public sealed class SmartInfo
{
    public string Friendly { get; set; } = "";
    public string? Health { get; set; }
    public string? Media { get; set; }
    public string? Bus { get; set; }
    public string? Model { get; set; }
    public string? Serial { get; set; }
    public long SizeBytes { get; set; }
    public bool IsSsd { get; set; }
    /// <summary>True for an NVMe drive — its counters come from the NVMe SMART/Health log, not the ATA table.</summary>
    public bool IsNvme { get; set; }

    public int? Temperature { get; set; }
    public long? PowerOnHours { get; set; }
    public long? PowerCycles { get; set; }
    // HDD-relevant high-risk counters
    public long? Reallocated { get; set; }      // 0x05
    public long? Pending { get; set; }          // 0xC5
    public long? Uncorrectable { get; set; }    // 0xC6
    public long? Crc { get; set; }              // 0xC7
    // SSD endurance
    public long? HostWritesBytes { get; set; }  // 0xF1
    public long? HostReadsBytes { get; set; }   // 0xF2
    public int? RemainingLifePercent { get; set; }
    // NVMe SMART/Health (log page 0x02)
    public int? PercentageUsed { get; set; }          // 0–255, % of rated endurance consumed
    public int? AvailableSpare { get; set; }          // %
    public int? AvailableSpareThreshold { get; set; } // %
    public int? CriticalWarning { get; set; }         // bitfield (0 = healthy)
    public long? MediaErrors { get; set; }            // media & data-integrity errors
    public long? UnsafeShutdowns { get; set; }
    public long? ErrorLogEntries { get; set; }
    public long? HostReadCommands { get; set; }
    public long? HostWriteCommands { get; set; }
    public long? ControllerBusyMinutes { get; set; }

    public List<SmartAttr> Attributes { get; } = new();

    public bool Ok { get; set; }
    /// <summary>True when SMART counters were read (ATA attribute table needs admin; NVMe log does not).</summary>
    public bool HasCounters { get; set; }
    /// <summary>Any high-risk indicator is non-zero (ATA reallocated/pending/uncorrectable, or NVMe critical
    /// warning / media errors / spare below threshold).</summary>
    public bool HasWarning => (Reallocated ?? 0) > 0 || (Pending ?? 0) > 0 || (Uncorrectable ?? 0) > 0
        || (CriticalWarning ?? 0) != 0 || (MediaErrors ?? 0) > 0
        || (AvailableSpare is int sp && AvailableSpareThreshold is int th && th > 0 && sp < th);
}

/// <summary>A one-shot snapshot of the machine's health: OS, CPU, RAM, drives (+ SMART), battery, activation.</summary>
public sealed class SystemSnapshot
{
    public string? OsCaption { get; set; }
    public string? OsVersion { get; set; }
    public string? Arch { get; set; }
    public string? CpuName { get; set; }
    public string? Manufacturer { get; set; }
    public string? Model { get; set; }
    public string? User { get; set; }
    public int CpuLoad { get; set; }
    public int Cores { get; set; }
    public int Threads { get; set; }
    public long TotalMemKb { get; set; }
    public long FreeMemKb { get; set; }
    public double UptimeHours { get; set; }
    public int? BatteryCharge { get; set; }
    public int? Activation { get; set; }   // LicenseStatus: 1 = licensed (see slmgr.vbs /dli)
    // Activation detail (mirrors slmgr.vbs /dlv fields on the SoftwareLicensingProduct CIM class).
    public string? ActName { get; set; }          // e.g. "Windows(R), Professional edition"
    public string? ActDescription { get; set; }   // e.g. "Windows(R) Operating System, RETAIL channel"
    public string? PartialKey { get; set; }        // last 5 chars of the product key
    public int? GraceMinutes { get; set; }         // remaining grace period, in minutes (0 / null = fully activated)
    public List<DiskInfo> Disks { get; } = new();
    public List<PhysDiskInfo> PhysicalDisks { get; } = new();
}

/// <summary>Reads machine health via CIM/WMI (through PowerShell, so the App needs no extra dependency).
/// Read-only. Used by the 系统概览 page.</summary>
public static class SystemInfo
{
    private const string Ps = """
        [Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false)
        $os = Get-CimInstance Win32_OperatingSystem
        $cs = Get-CimInstance Win32_ComputerSystem
        $cpu = Get-CimInstance Win32_Processor | Select-Object -First 1
        $disks = @(Get-CimInstance Win32_LogicalDisk -Filter "DriveType=3" | Select-Object DeviceID,Size,FreeSpace,VolumeName)
        $bat = Get-CimInstance Win32_Battery -ErrorAction SilentlyContinue | Select-Object -First 1
        $phys = @()
        try { $phys = @(Get-PhysicalDisk -ErrorAction Stop | Select-Object FriendlyName,MediaType,BusType,HealthStatus,DeviceId,@{n='SizeGB';e={[math]::Round($_.Size/1GB,0)}}) } catch {}
        # Windows licensing — same source slmgr.vbs /dlv reads: the Windows SoftwareLicensingProduct (by the
        # Windows ApplicationID), preferring the actively-licensed SKU. Name=edition, Description=channel,
        # PartialProductKey=last 5 of the key, GracePeriodRemaining=minutes left in any grace window.
        $lic = $null
        try {
          $lic = Get-CimInstance SoftwareLicensingProduct -Filter "ApplicationID='55c92734-d682-4d71-983e-d6ec3f16059f' AND LicenseStatus=1" -ErrorAction Stop | Select-Object -First 1
          if (-not $lic) { $lic = Get-CimInstance SoftwareLicensingProduct -Filter "ApplicationID='55c92734-d682-4d71-983e-d6ec3f16059f' AND PartialProductKey IS NOT NULL" -ErrorAction Stop | Select-Object -First 1 }
        } catch {}
        $uptime = [math]::Round(((Get-Date) - $os.LastBootUpTime).TotalHours, 1)
        [pscustomobject]@{
          OsCaption=$os.Caption; OsVersion=$os.Version; Arch=$os.OSArchitecture;
          FreeMemKb=$os.FreePhysicalMemory; TotalMemKb=$os.TotalVisibleMemorySize; UptimeHours=$uptime;
          Manufacturer=$cs.Manufacturer; Model=$cs.Model; User=$cs.UserName;
          CpuName=$cpu.Name; CpuLoad=$cpu.LoadPercentage; Cores=$cpu.NumberOfCores; Threads=$cpu.NumberOfLogicalProcessors;
          Disks=$disks; BatteryCharge=$(if($bat){$bat.EstimatedChargeRemaining}else{$null});
          PhysicalDisks=$phys;
          Activation=$(if($lic){$lic.LicenseStatus}else{$null});
          ActName=$(if($lic){$lic.Name}else{$null});
          ActDescription=$(if($lic){$lic.Description}else{$null});
          PartialKey=$(if($lic){$lic.PartialProductKey}else{$null});
          GraceMinutes=$(if($lic){$lic.GracePeriodRemaining}else{$null})
        } | ConvertTo-Json -Depth 4 -Compress
        """;

    public static async Task<SystemSnapshot> GetAsync(CancellationToken ct = default)
    {
        var snap = new SystemSnapshot();
        ProcResult r;
        try { r = await Proc.RunAsync("powershell", new[] { "-NoProfile", "-NonInteractive", "-Command", Ps }, ct: ct); }
        catch { return snap; }
        if (string.IsNullOrWhiteSpace(r.StdOut)) return snap;

        try
        {
            using var doc = JsonDocument.Parse(r.StdOut);
            var e = doc.RootElement;
            snap.OsCaption = Str(e, "OsCaption");
            snap.OsVersion = Str(e, "OsVersion");
            snap.Arch = Str(e, "Arch");
            snap.CpuName = Str(e, "CpuName")?.Trim();
            snap.Manufacturer = Str(e, "Manufacturer");
            snap.Model = Str(e, "Model");
            snap.User = Str(e, "User");
            snap.CpuLoad = (int)Num(e, "CpuLoad");
            snap.Cores = (int)Num(e, "Cores");
            snap.Threads = (int)Num(e, "Threads");
            snap.TotalMemKb = Num(e, "TotalMemKb");
            snap.FreeMemKb = Num(e, "FreeMemKb");
            snap.UptimeHours = Dbl(e, "UptimeHours");
            if (e.TryGetProperty("BatteryCharge", out var bc) && bc.ValueKind == JsonValueKind.Number) snap.BatteryCharge = bc.GetInt32();
            if (e.TryGetProperty("Activation", out var ac) && ac.ValueKind == JsonValueKind.Number) snap.Activation = ac.GetInt32();
            snap.ActName = Str(e, "ActName");
            snap.ActDescription = Str(e, "ActDescription");
            snap.PartialKey = Str(e, "PartialKey");
            if (e.TryGetProperty("GraceMinutes", out var gmin) && gmin.ValueKind == JsonValueKind.Number) snap.GraceMinutes = gmin.GetInt32();

            foreach (var d in Items(e, "Disks"))
                snap.Disks.Add(new DiskInfo(Str(d, "DeviceID") ?? "?", Num(d, "Size"), Num(d, "FreeSpace"), Str(d, "VolumeName")));
            foreach (var p in Items(e, "PhysicalDisks"))
                snap.PhysicalDisks.Add(new PhysDiskInfo(Str(p, "FriendlyName") ?? "?", Str(p, "MediaType"), Str(p, "HealthStatus"), Num(p, "SizeGB"), IdStr(p, "DeviceId"), Str(p, "BusType")));
        }
        catch { /* partial / unparseable */ }
        return snap;
    }

    private static IEnumerable<JsonElement> Items(JsonElement e, string prop)
    {
        if (!e.TryGetProperty(prop, out var v)) yield break;
        if (v.ValueKind == JsonValueKind.Array) { foreach (var x in v.EnumerateArray()) yield return x; }
        else if (v.ValueKind == JsonValueKind.Object) yield return v;
    }

    // Identity (model/serial/size) comes from Win32_DiskDrive (no admin). The raw ATA SMART attribute table
    // (MSStorageDriver_FailurePredictData, 512 bytes) needs admin — parsed in C#. __DEV__ = disk index.
    private const string SmartPs = """
        [Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false)
        $idx = __DEV__
        $pd = Get-PhysicalDisk -ErrorAction SilentlyContinue | Where-Object { "$($_.DeviceId)" -eq "$idx" } | Select-Object -First 1
        $dd = Get-CimInstance Win32_DiskDrive -ErrorAction SilentlyContinue | Where-Object { $_.Index -eq $idx } | Select-Object -First 1
        $pnp = if ($dd) { "$($dd.PNPDeviceID)" } else { "" }
        $vendor = $null; $thr = $null
        try {
          $all = Get-CimInstance -Namespace root\wmi -ClassName MSStorageDriver_FailurePredictData -ErrorAction Stop
          $m = $all | Where-Object { $pnp -and (($_.InstanceName -replace '_0$','') -ieq $pnp) } | Select-Object -First 1
          if (-not $m) { $m = $all | Where-Object { $pnp -and $_.InstanceName -like "$pnp*" } | Select-Object -First 1 }
          if (-not $m -and (@($all).Count -eq 1)) { $m = @($all)[0] }
          if ($m) { $vendor = $m.VendorSpecific }
          $allt = Get-CimInstance -Namespace root\wmi -ClassName MSStorageDriver_FailurePredictThresholds -ErrorAction SilentlyContinue
          $mt = $allt | Where-Object { $pnp -and (($_.InstanceName -replace '_0$','') -ieq $pnp) } | Select-Object -First 1
          if (-not $mt -and (@($allt).Count -eq 1)) { $mt = @($allt)[0] }
          if ($mt) { $thr = $mt.VendorSpecific }
        } catch {}
        [pscustomobject]@{
          Friendly="$($pd.FriendlyName)"; Health="$($pd.HealthStatus)"; Media="$($pd.MediaType)"; Bus="$($pd.BusType)";
          Model=("$($dd.Model)").Trim(); Serial=("$($dd.SerialNumber)").Trim(); SizeBytes=[int64]($dd.Size);
          Vendor=$vendor; Thresholds=$thr
        } | ConvertTo-Json -Compress -Depth 4
        """;

    /// <summary>Read simplified SMART for one physical disk (by Get-PhysicalDisk DeviceId), WITHOUT elevation.
    /// Health / media / bus work; temperature / wear / power-on-hours etc. need admin (Windows denies the
    /// reliability counters to non-elevated callers) — use <see cref="GetSmartElevatedAsync"/> for those.</summary>
    public static async Task<SmartInfo> GetSmartAsync(string? deviceId, CancellationToken ct = default)
    {
        var info = new SmartInfo();
        if (string.IsNullOrWhiteSpace(deviceId)) return info;
        ProcResult r;
        try { r = await Proc.RunAsync("powershell", new[] { "-NoProfile", "-NonInteractive", "-Command", SmartPs.Replace("__DEV__", deviceId) }, ct: ct); }
        catch { return info; }
        ParseSmart(r.StdOut, info);
        await AugmentNvmeAsync(deviceId, info, ct);
        await AugmentSmartctlAsync(deviceId, info, ct);
        return info;
    }

    /// <summary>Read the NVMe SMART/Health log via IOCTL for INTERNAL NVMe drives — the authoritative source
    /// (overrides any garbage the ATA WMI path produced) and needs no admin. External NVMe (USB / etc.) is left
    /// to smartctl: the native IOCTLs can't tunnel through bridge chips and may even hang the call, so we never
    /// run them on external buses. Wrapped in a timeout as a belt-and-suspenders against a stuck controller.</summary>
    private static async Task AugmentNvmeAsync(string? deviceId, SmartInfo info, CancellationToken ct)
    {
        if (!int.TryParse(deviceId, out var idx)) return;
        if (!(info.Bus ?? "").Trim().Equals("NVMe", StringComparison.OrdinalIgnoreCase)) return;
        try
        {
            var read = Task.Run(() => NvmeSmart.Read(idx));
            if (await Task.WhenAny(read, Task.Delay(8000, ct)) == read && read.Result is { } nv) ApplyNvme(info, nv);
        }
        catch { /* best-effort */ }
    }

    /// <summary>Final fallback for any drive still without SMART counters — most importantly NVMe behind a USB
    /// bridge (ASMedia / JMicron / Realtek), which the native IOCTLs can't tunnel. Runs the bundled smartctl
    /// (auto-detects the bridge) and parses its JSON. Needs admin (smartctl opens the raw disk).</summary>
    private static async Task AugmentSmartctlAsync(string? deviceId, SmartInfo info, CancellationToken ct)
    {
        if (info.HasCounters || !int.TryParse(deviceId, out var idx)) return;
        string? json;
        try { json = await SmartctlReader.RunAsync(idx, ct); }
        catch { return; }
        if (string.IsNullOrWhiteSpace(json)) return;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var e = doc.RootElement;
            if (e.ValueKind != JsonValueKind.Object) return;

            if (string.IsNullOrEmpty(info.Model) && Str(e, "model_name") is { Length: > 0 } m) info.Model = m;
            if (string.IsNullOrEmpty(info.Serial) && Str(e, "serial_number") is { Length: > 0 } sn) info.Serial = sn;
            if (info.SizeBytes <= 0 && e.TryGetProperty("user_capacity", out var uc) && uc.ValueKind == JsonValueKind.Object && Num(uc, "bytes") is var ub && ub > 0) info.SizeBytes = ub;
            if (e.TryGetProperty("temperature", out var tp) && tp.ValueKind == JsonValueKind.Object && tp.TryGetProperty("current", out var tc) && tc.ValueKind == JsonValueKind.Number) info.Temperature = tc.GetInt32();
            if (e.TryGetProperty("power_on_time", out var pot) && pot.ValueKind == JsonValueKind.Object && pot.TryGetProperty("hours", out var ph) && ph.ValueKind == JsonValueKind.Number) info.PowerOnHours = ph.GetInt64();
            if (e.TryGetProperty("power_cycle_count", out var pcc) && pcc.ValueKind == JsonValueKind.Number) info.PowerCycles = pcc.GetInt64();
            if (string.IsNullOrEmpty(info.Health) && e.TryGetProperty("smart_status", out var ss) && ss.ValueKind == JsonValueKind.Object && ss.TryGetProperty("passed", out var pa))
                info.Health = pa.ValueKind == JsonValueKind.True ? "Healthy" : pa.ValueKind == JsonValueKind.False ? "Warning" : info.Health;

            if (e.TryGetProperty("nvme_smart_health_information_log", out var nv) && nv.ValueKind == JsonValueKind.Object)
            {
                info.IsNvme = true; info.IsSsd = true;
                info.Attributes.Clear();
                int? I(string k) => nv.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : null;
                long? L(string k) => nv.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt64() : null;
                info.CriticalWarning = I("critical_warning");
                if (info.Temperature is null && I("temperature") is int t2) info.Temperature = t2;
                info.AvailableSpare = I("available_spare");
                info.AvailableSpareThreshold = I("available_spare_threshold");
                info.PercentageUsed = I("percentage_used");
                if (info.PercentageUsed is int pu) info.RemainingLifePercent = Math.Clamp(100 - pu, 0, 100);
                if (L("data_units_read") is long dr) info.HostReadsBytes = dr * 512000;       // NVMe data units = 1000 × 512 B
                if (L("data_units_written") is long dw) info.HostWritesBytes = dw * 512000;
                info.MediaErrors = L("media_errors");
                info.UnsafeShutdowns = L("unsafe_shutdowns");
                info.ErrorLogEntries = L("num_err_log_entries");
                info.PowerOnHours ??= L("power_on_hours");
                info.PowerCycles ??= L("power_cycles");
                info.HasCounters = true;
            }
            else if (e.TryGetProperty("ata_smart_attributes", out var aa) && aa.ValueKind == JsonValueKind.Object
                     && aa.TryGetProperty("table", out var tbl) && tbl.ValueKind == JsonValueKind.Array)
            {
                foreach (var a in tbl.EnumerateArray())
                {
                    var id = (int)Num(a, "id");
                    if (id == 0) continue;
                    int cur = (int)Num(a, "value"), worst = (int)Num(a, "worst"), thr = (int)Num(a, "thresh");
                    long raw = a.TryGetProperty("raw", out var rw) && rw.ValueKind == JsonValueKind.Object ? Num(rw, "value") : 0;
                    var critical = (id is 5 or 196 or 197 or 198) && raw > 0;
                    info.Attributes.Add(new SmartAttr(id, AttrName(id), cur, worst, thr, raw, critical));
                }
                long? Raw(int id) => info.Attributes.FirstOrDefault(x => x.Id == id)?.Raw;
                int? Cur(int id) => info.Attributes.FirstOrDefault(x => x.Id == id)?.Current;
                if (info.Temperature is null && Raw(194) is long t194) info.Temperature = (int)(t194 & 0xFF);
                info.PowerOnHours ??= Raw(9);
                info.PowerCycles ??= Raw(12);
                info.Reallocated = Raw(5); info.Pending = Raw(197); info.Uncorrectable = Raw(198); info.Crc = Raw(199);
                // Host read/write totals: the Device Statistics log (GP Log 0x04, via "-l devstat") is the
                // authoritative source — it reports the true logical-sector counts. Prefer it over attrs
                // 0xF1/0xF2, whose raw value is vendor-defined: SanDisk / WD store it pre-scaled to GiB (e.g.
                // 1162 for 1162 GiB), so the usual ×512 under-reports by ~10⁹× and rounds to 0. Use 0xF1/0xF2
                // only as a fallback when the Device Statistics log is unavailable.
                ApplyDeviceStatistics(e, info);
                if (info.HostWritesBytes is null && Raw(241) is long w) info.HostWritesBytes = w * 512;
                if (info.HostReadsBytes is null && Raw(242) is long rd) info.HostReadsBytes = rd * 512;
                info.RemainingLifePercent ??= Cur(231) ?? Cur(202) ?? Cur(173) ?? Cur(169) ?? Cur(177);
                if (e.TryGetProperty("rotation_rate", out var rr) && rr.ValueKind == JsonValueKind.Number && rr.GetInt32() == 0) info.IsSsd = true;
                info.HasCounters = info.Attributes.Count > 0;
            }
            if (info.HasCounters) info.Ok = true;
        }
        catch { /* unparseable JSON — leave whatever the native readers already populated */ }
    }

    /// <summary>Read host read/write totals from smartctl's Device Statistics log (GP Log 0x04). Used for SATA SSDs
    /// that don't expose ATA attributes 0xF1/0xF2 — "Logical Sectors Written/Read" × 512 B = host bytes.</summary>
    private static void ApplyDeviceStatistics(JsonElement e, SmartInfo info)
    {
        if (!e.TryGetProperty("ata_device_statistics", out var ds) || ds.ValueKind != JsonValueKind.Object) return;
        if (!ds.TryGetProperty("pages", out var pages) || pages.ValueKind != JsonValueKind.Array) return;
        foreach (var pg in pages.EnumerateArray())
        {
            if (!pg.TryGetProperty("table", out var t) || t.ValueKind != JsonValueKind.Array) continue;
            foreach (var row in t.EnumerateArray())
            {
                if (row.TryGetProperty("valid", out var vd) && vd.ValueKind == JsonValueKind.False) continue;
                if (!row.TryGetProperty("value", out var vv) || vv.ValueKind != JsonValueKind.Number) continue;
                var name = Str(row, "name") ?? "";
                if (info.HostWritesBytes is null && name == "Logical Sectors Written") info.HostWritesBytes = vv.GetInt64() * 512;
                else if (info.HostReadsBytes is null && name == "Logical Sectors Read") info.HostReadsBytes = vv.GetInt64() * 512;
            }
        }
    }

    private static void ApplyNvme(SmartInfo info, NvmeSmart.NvmeHealthLog nv)
    {
        info.IsNvme = true;
        info.IsSsd = true;
        // Any ATA attributes / counters parsed from the NVMe drive's FailurePredictData are meaningless
        // (wrong layout) — drop them so they can't show a garbage table or trigger a false warning banner.
        info.Attributes.Clear();
        info.Reallocated = info.Pending = info.Uncorrectable = info.Crc = null;
        if (nv.TemperatureC is int t) info.Temperature = t;
        info.PowerOnHours = nv.PowerOnHours;
        info.PowerCycles = nv.PowerCycles;
        info.HostWritesBytes = nv.DataUnitsWrittenBytes;
        info.HostReadsBytes = nv.DataUnitsReadBytes;
        info.PercentageUsed = nv.PercentageUsed;
        info.RemainingLifePercent = Math.Clamp(100 - nv.PercentageUsed, 0, 100);
        info.AvailableSpare = nv.AvailableSpare;
        info.AvailableSpareThreshold = nv.AvailableSpareThreshold;
        info.CriticalWarning = nv.CriticalWarning;
        info.MediaErrors = nv.MediaErrors;
        info.UnsafeShutdowns = nv.UnsafeShutdowns;
        info.ErrorLogEntries = nv.ErrorLogEntries;
        info.HostReadCommands = nv.HostReadCommands;
        info.HostWriteCommands = nv.HostWriteCommands;
        info.ControllerBusyMinutes = nv.ControllerBusyMinutes;
        info.HasCounters = true;
    }

    /// <summary>Read full SMART for one disk by running the same query ELEVATED (UAC). Windows only returns
    /// temperature / wear / power-on-hours / error counts to an administrator, so the elevated child writes
    /// the JSON to a temp file we then read back. Returns an empty result if the user cancels UAC.</summary>
    public static async Task<SmartInfo> GetSmartElevatedAsync(string? deviceId, CancellationToken ct = default)
    {
        var info = new SmartInfo();
        if (string.IsNullOrWhiteSpace(deviceId)) return info;
        var jsonPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"wd_smart_{Guid.NewGuid():N}.json");
        var ps1Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"wd_smart_{Guid.NewGuid():N}.ps1");
        try
        {
            var script = SmartPs.Replace("__DEV__", deviceId).TrimEnd()
                         + $" | Out-File -LiteralPath '{jsonPath}' -Encoding utf8";
            await System.IO.File.WriteAllTextAsync(ps1Path, script, ct);

            var psi = new System.Diagnostics.ProcessStartInfo("powershell")
            {
                Arguments = $"-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"{ps1Path}\"",
                UseShellExecute = true,        // required for Verb=runas (UAC)
                Verb = "runas",
                WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden,
            };
            var p = System.Diagnostics.Process.Start(psi);
            if (p == null) return info;
            await p.WaitForExitAsync(ct);
            if (System.IO.File.Exists(jsonPath))
                ParseSmart(await System.IO.File.ReadAllTextAsync(jsonPath, ct), info);
            await AugmentNvmeAsync(deviceId, info, ct);
        }
        catch (System.ComponentModel.Win32Exception) { /* user cancelled the UAC prompt */ }
        catch { /* best-effort */ }
        finally
        {
            try { System.IO.File.Delete(ps1Path); } catch { }
            try { System.IO.File.Delete(jsonPath); } catch { }
        }
        return info;
    }

    private static void ParseSmart(string? stdout, SmartInfo info)
    {
        if (string.IsNullOrWhiteSpace(stdout)) return;
        try
        {
            using var doc = JsonDocument.Parse(stdout.Trim());
            var e = doc.RootElement;
            if (e.ValueKind != JsonValueKind.Object) return;
            info.Friendly = Str(e, "Friendly") ?? info.Friendly;
            info.Health = Str(e, "Health") ?? info.Health;
            info.Media = Str(e, "Media") ?? info.Media;
            info.Bus = Str(e, "Bus") ?? info.Bus;
            info.Model = Str(e, "Model") ?? info.Model;
            info.Serial = Str(e, "Serial") ?? info.Serial;
            info.SizeBytes = Num(e, "SizeBytes") is var sz && sz > 0 ? sz : info.SizeBytes;
            info.IsSsd = (info.Media?.Contains("SSD", StringComparison.OrdinalIgnoreCase) ?? false)
                         || (info.Bus?.Equals("NVMe", StringComparison.OrdinalIgnoreCase) ?? false);
            info.Ok = !string.IsNullOrEmpty(info.Friendly) || !string.IsNullOrEmpty(info.Health) || !string.IsNullOrEmpty(info.Model);

            var vendor = Bytes(e, "Vendor");
            if (vendor != null && vendor.Length >= 14)
            {
                ParseAttributes(vendor, Bytes(e, "Thresholds"), info);
                info.HasCounters = info.Attributes.Count > 0;
            }
        }
        catch { /* unparseable */ }
    }

    /// <summary>Parse the 512-byte ATA SMART attribute table (30 × 12-byte entries from offset 2) and derive
    /// the headline values (temperature / power-on hours / reallocated-C5/C6 / host reads+writes / wear).</summary>
    private static void ParseAttributes(byte[] v, byte[]? thr, SmartInfo info)
    {
        var thrMap = new Dictionary<int, int>();
        if (thr != null)
            for (var o = 2; o + 12 <= thr.Length; o += 12) { int id = thr[o]; if (id != 0) thrMap[id] = thr[o + 1]; }

        for (var o = 2; o + 12 <= v.Length; o += 12)
        {
            int id = v[o];
            if (id == 0) continue;
            int cur = v[o + 3], worst = v[o + 4];
            long raw = 0;
            for (var k = 0; k < 6; k++) raw |= (long)v[o + 5 + k] << (8 * k);
            var threshold = thrMap.TryGetValue(id, out var tv) ? tv : 0;
            var critical = (id is 5 or 196 or 197 or 198) && raw > 0;
            info.Attributes.Add(new SmartAttr(id, AttrName(id), cur, worst, threshold, raw, critical));
        }

        long? Raw(int id) => info.Attributes.FirstOrDefault(a => a.Id == id)?.Raw;
        int? Cur(int id) => info.Attributes.FirstOrDefault(a => a.Id == id)?.Current;

        info.Temperature = Raw(194) is long t194 ? (int)(t194 & 0xFF) : Raw(190) is long t190 ? (int)(t190 & 0xFF) : null;
        info.PowerOnHours = Raw(9);
        info.PowerCycles = Raw(12);
        info.Reallocated = Raw(5);
        info.Pending = Raw(197);
        info.Uncorrectable = Raw(198);
        info.Crc = Raw(199);
        if (Raw(241) is long w) info.HostWritesBytes = w * 512;
        if (Raw(242) is long rd) info.HostReadsBytes = rd * 512;
        info.RemainingLifePercent = Cur(231) ?? Cur(202) ?? Cur(173) ?? Cur(169) ?? Cur(177);
    }

    private static readonly Dictionary<int, string> SmartNames = new()
    {
        [1] = "读取错误率", [4] = "启停次数", [5] = "重映射扇区数 (05)", [9] = "通电时间", [10] = "主轴重试",
        [12] = "通电次数", [187] = "已报告无法纠正", [188] = "命令超时", [190] = "气流温度",
        [194] = "温度", [195] = "硬件 ECC 已恢复", [196] = "重映射事件 (C4)", [197] = "当前待映射扇区 (C5)",
        [198] = "无法纠正扇区 (C6)", [199] = "UDMA CRC 错误 (C7)", [231] = "SSD 剩余寿命", [233] = "媒体磨损指标",
        [173] = "磨损均衡次数", [177] = "磨损均衡次数", [202] = "剩余寿命百分比", [241] = "总写入量 (LBA)",
        [242] = "总读取量 (LBA)", [192] = "断电磁头收回", [193] = "磁头加载次数", [169] = "剩余寿命",
    };
    private static string AttrName(int id) => SmartNames.TryGetValue(id, out var n) ? n : $"属性 0x{id:X2}";

    private static byte[]? Bytes(JsonElement e, string p)
    {
        if (!e.TryGetProperty(p, out var v) || v.ValueKind != JsonValueKind.Array) return null;
        var list = new List<byte>(v.GetArrayLength());
        foreach (var x in v.EnumerateArray())
            list.Add(x.ValueKind == JsonValueKind.Number && x.TryGetInt32(out var b) ? (byte)b : (byte)0);
        return list.ToArray();
    }

    private static string? IdStr(JsonElement e, string p)
    {
        if (!e.TryGetProperty(p, out var v)) return null;
        return v.ValueKind switch { JsonValueKind.String => v.GetString(), JsonValueKind.Number => v.GetRawText(), _ => null };
    }

    private static string? Str(JsonElement e, string p)
        => e.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    private static long Num(JsonElement e, string p)
        => e.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) ? n : 0;
    private static int? NumI(JsonElement e, string p)
        => e.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) ? n : null;
    private static long? NumL(JsonElement e, string p)
        => e.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) ? n : null;
    private static double Dbl(JsonElement e, string p)
        => e.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var n) ? n : 0;
}
