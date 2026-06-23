using System.Text.Json;
using WinDeploy.Core.Util;

namespace WinDeploy.App.Services;

public sealed record DiskInfo(string Drive, long SizeBytes, long FreeBytes, string? Label);
public sealed record PhysDiskInfo(string Name, string? Media, string? Health, long SizeGb, string? DeviceId);

/// <summary>Simplified SMART / reliability counters for one physical disk (from Get-StorageReliabilityCounter).
/// Fields are null when the drive / driver doesn't expose them.</summary>
public sealed class SmartInfo
{
    public string Friendly { get; set; } = "";
    public string? Health { get; set; }
    public string? Media { get; set; }
    public string? Bus { get; set; }
    public int? Temperature { get; set; }
    public int? TemperatureMax { get; set; }
    public int? Wear { get; set; }                 // % used (SSD endurance)
    public long? PowerOnHours { get; set; }
    public long? PowerCycles { get; set; }
    public long? StartStop { get; set; }
    public long? ReadErrorsTotal { get; set; }
    public long? ReadErrorsUncorrected { get; set; }
    public long? WriteErrorsTotal { get; set; }
    public long? WriteErrorsUncorrected { get; set; }
    public bool Ok { get; set; }
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
    public int? Activation { get; set; }   // 1 = licensed
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
        try { $phys = @(Get-PhysicalDisk -ErrorAction Stop | Select-Object FriendlyName,MediaType,HealthStatus,DeviceId,@{n='SizeGB';e={[math]::Round($_.Size/1GB,0)}}) } catch {}
        $act = $null
        try { $act = (Get-CimInstance SoftwareLicensingProduct -Filter "ApplicationID='55c92734-d682-4d71-983e-d6ec3f16059f' AND PartialProductKey IS NOT NULL" -ErrorAction Stop | Select-Object -First 1).LicenseStatus } catch {}
        $uptime = [math]::Round(((Get-Date) - $os.LastBootUpTime).TotalHours, 1)
        [pscustomobject]@{
          OsCaption=$os.Caption; OsVersion=$os.Version; Arch=$os.OSArchitecture;
          FreeMemKb=$os.FreePhysicalMemory; TotalMemKb=$os.TotalVisibleMemorySize; UptimeHours=$uptime;
          Manufacturer=$cs.Manufacturer; Model=$cs.Model; User=$cs.UserName;
          CpuName=$cpu.Name; CpuLoad=$cpu.LoadPercentage; Cores=$cpu.NumberOfCores; Threads=$cpu.NumberOfLogicalProcessors;
          Disks=$disks; BatteryCharge=$(if($bat){$bat.EstimatedChargeRemaining}else{$null});
          PhysicalDisks=$phys; Activation=$act
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

            foreach (var d in Items(e, "Disks"))
                snap.Disks.Add(new DiskInfo(Str(d, "DeviceID") ?? "?", Num(d, "Size"), Num(d, "FreeSpace"), Str(d, "VolumeName")));
            foreach (var p in Items(e, "PhysicalDisks"))
                snap.PhysicalDisks.Add(new PhysDiskInfo(Str(p, "FriendlyName") ?? "?", Str(p, "MediaType"), Str(p, "HealthStatus"), Num(p, "SizeGB"), IdStr(p, "DeviceId")));
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

    // Get-PhysicalDisk's DeviceId serializes as a number; read it as a string either way.
    private const string SmartPs = """
        [Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false)
        $d = Get-PhysicalDisk -ErrorAction SilentlyContinue | Where-Object { "$($_.DeviceId)" -eq '__DEV__' } | Select-Object -First 1
        if ($null -eq $d) { '{}'; exit }
        $r = $d | Get-StorageReliabilityCounter -ErrorAction SilentlyContinue
        [pscustomobject]@{
          Friendly="$($d.FriendlyName)"; Health="$($d.HealthStatus)"; Media="$($d.MediaType)"; Bus="$($d.BusType)";
          Temperature=$r.Temperature; TemperatureMax=$r.TemperatureMax; Wear=$r.Wear;
          PowerOnHours=$r.PowerOnHours; StartStop=$r.StartStopCycleCount;
          ReadErrorsTotal=$r.ReadErrorsTotal; ReadErrorsUncorrected=$r.ReadErrorsUncorrected;
          WriteErrorsTotal=$r.WriteErrorsTotal; WriteErrorsUncorrected=$r.WriteErrorsUncorrected
        } | ConvertTo-Json -Compress
        """;

    /// <summary>Read simplified SMART / reliability counters for one physical disk (by Get-PhysicalDisk DeviceId).</summary>
    public static async Task<SmartInfo> GetSmartAsync(string? deviceId, CancellationToken ct = default)
    {
        var info = new SmartInfo();
        if (string.IsNullOrWhiteSpace(deviceId)) return info;
        ProcResult r;
        try { r = await Proc.RunAsync("powershell", new[] { "-NoProfile", "-NonInteractive", "-Command", SmartPs.Replace("__DEV__", deviceId) }, ct: ct); }
        catch { return info; }
        if (string.IsNullOrWhiteSpace(r.StdOut)) return info;

        try
        {
            using var doc = JsonDocument.Parse(r.StdOut.Trim());
            var e = doc.RootElement;
            if (e.ValueKind != JsonValueKind.Object) return info;
            info.Friendly = Str(e, "Friendly") ?? "";
            info.Health = Str(e, "Health");
            info.Media = Str(e, "Media");
            info.Bus = Str(e, "Bus");
            info.Temperature = NumI(e, "Temperature");
            info.TemperatureMax = NumI(e, "TemperatureMax");
            info.Wear = NumI(e, "Wear");
            info.PowerOnHours = NumL(e, "PowerOnHours");
            info.StartStop = NumL(e, "StartStop");
            info.ReadErrorsTotal = NumL(e, "ReadErrorsTotal");
            info.ReadErrorsUncorrected = NumL(e, "ReadErrorsUncorrected");
            info.WriteErrorsTotal = NumL(e, "WriteErrorsTotal");
            info.WriteErrorsUncorrected = NumL(e, "WriteErrorsUncorrected");
            info.Ok = !string.IsNullOrEmpty(info.Friendly) || !string.IsNullOrEmpty(info.Health);
        }
        catch { /* unparseable */ }
        return info;
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
