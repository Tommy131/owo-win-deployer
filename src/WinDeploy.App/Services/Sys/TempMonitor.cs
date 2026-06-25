using System.Threading;
using WinDeploy.App.Services.Infra;
using WinDeploy.Core.I18n;

namespace WinDeploy.App.Services.Sys;

/// <summary>Immutable snapshot of the temperature-monitor configuration.</summary>
public sealed class TempMonitorConfig
{
    public bool Enabled { get; init; }
    public bool Tts { get; init; } = true;
    public bool CpuOn { get; init; } = true;
    public bool GpuOn { get; init; } = true;
    public bool DiskOn { get; init; } = true;
    public int CpuThreshold { get; init; } = 90;
    public int GpuThreshold { get; init; } = 85;
    public int DiskThreshold { get; init; } = 65;
    /// <summary>Repeat-reminder interval (seconds) while a device stays over its threshold.</summary>
    public int ReminderSeconds { get; init; } = 60;

    public static TempMonitorConfig From(AppSettings s) => new()
    {
        Enabled = s.TempMonitorEnabled,
        Tts = s.TempTtsEnabled,
        CpuOn = s.TempCpuEnabled,
        GpuOn = s.TempGpuEnabled,
        DiskOn = s.TempDiskEnabled,
        CpuThreshold = s.CpuTempThreshold,
        GpuThreshold = s.GpuTempThreshold,
        DiskThreshold = s.DiskTempThreshold,
        ReminderSeconds = s.TempReminderSeconds <= 0 ? 60 : s.TempReminderSeconds,
    };
}

/// <summary>Identifies one overheating device for the UI prompt.</summary>
public sealed record OverheatInfo(string Key, string Device, int Temp, int Threshold);

/// <summary>
/// Background hardware-temperature watchdog. Every <see cref="Interval"/> it samples CPU / GPU temperature
/// (via <see cref="HardwareMonitor"/>) and the temperature of EVERY connected physical disk — NVMe internal
/// and USB-NVMe enclosures via fast <see cref="NvmeSmart"/> IOCTLs (no admin, no lingering handles that would
/// block eject), SATA / USB-SATA via the bundled smartctl SMART path (needs admin). Each device is tracked
/// independently.
///
/// When a device exceeds its threshold it alerts — a Windows toast (<see cref="ToastService"/>) plus an optional
/// localized TTS voice warning naming the specific device and its temperature (<see cref="Tts"/>) — and KEEPS
/// re-alerting every <see cref="TempMonitorConfig.ReminderSeconds"/> for as long as it stays hot, until the user
/// either cools it or chooses to ignore it. The UI subscribes to <see cref="Overheat"/> to offer an advanced
/// prompt (ignore this device / change the reminder frequency); <see cref="IgnoreDevice"/> silences one device
/// for the rest of the session, and <see cref="Resolved"/> fires when a device drops back below its threshold.
/// </summary>
public static class TempMonitor
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(20);
    private static readonly long DiskTtlMs = (long)TimeSpan.FromSeconds(60).TotalMilliseconds;
    // A disk whose temperature can't be read (e.g. SATA without admin) is retried far less often so we don't
    // re-spawn smartctl every minute for a drive that will keep returning nothing.
    private static readonly long DiskMissTtlMs = (long)TimeSpan.FromMinutes(10).TotalMilliseconds;
    private static readonly long DiskListTtlMs = (long)TimeSpan.FromMinutes(10).TotalMilliseconds;

    private static readonly object Gate = new();
    private static TempMonitorConfig _cfg = new();
    private static Timer? _timer;
    private static volatile bool _sampling;

    // Alert state (touched from the timer thread and from IgnoreDevice on the UI thread) — guard with Gate.
    private static readonly Dictionary<string, long> _lastAlert = new();   // device key → last toast/TTS tick
    private static readonly HashSet<string> _over = new();                 // currently over threshold
    private static readonly HashSet<string> _ignored = new();              // user-muted for the session

    // Disk enumeration + per-disk temperature cache (timer thread only).
    private static List<(int Index, string Name, string? Bus)> _disks = new();
    private static long _disksAge;
    private static readonly Dictionary<int, (long Tick, int? Temp)> _diskTemp = new();

    /// <summary>Raised (on a background thread) each time a device alert fires — drives the UI's advanced prompt.</summary>
    public static event Action<OverheatInfo>? Overheat;
    /// <summary>Raised (on a background thread) when a device drops back below its threshold.</summary>
    public static event Action<string>? Resolved;

    /// <summary>Apply a new configuration and start/stop the watchdog accordingly. Safe to call repeatedly.</summary>
    public static void Configure(TempMonitorConfig cfg)
    {
        lock (Gate) _cfg = cfg;
        if (cfg.Enabled) Start(); else Stop();
    }

    public static void Start()
    {
        lock (Gate)
        {
            HardwareMonitor.TryInit();
            _timer ??= new Timer(_ => Tick(), null, TimeSpan.FromSeconds(3), Interval);
        }
    }

    public static void Stop()
    {
        lock (Gate) { _timer?.Dispose(); _timer = null; }
    }

    /// <summary>Mute one device's alerts for the rest of this session (user chose "ignore"). Cleared on restart.</summary>
    public static void IgnoreDevice(string key)
    {
        lock (Gate) { _ignored.Add(key); _lastAlert.Remove(key); _over.Remove(key); }
        AuditLog.App($"温度监控：本次运行忽略设备 {key}");
    }

    private static void Tick()
    {
        if (_sampling) return;
        _sampling = true;
        try
        {
            TempMonitorConfig cfg;
            lock (Gate) cfg = _cfg;
            if (!cfg.Enabled) return;
            var reminderMs = Math.Max(10, cfg.ReminderSeconds) * 1000L;

            var hw = HardwareMonitor.Sample();
            if (cfg.CpuOn)
                Evaluate("cpu", Localizer.T("tempmon.device.cpu"), TempOrNull(hw.CpuTemp), cfg.CpuThreshold, cfg.Tts, reminderMs);
            if (cfg.GpuOn)
                Evaluate("gpu", string.IsNullOrWhiteSpace(hw.GpuName) ? Localizer.T("tempmon.device.gpu") : hw.GpuName!,
                         TempOrNull(hw.GpuTemp), cfg.GpuThreshold, cfg.Tts, reminderMs);
            if (cfg.DiskOn)
                foreach (var (idx, name, bus) in CurrentDisks())
                    Evaluate($"disk{idx}", name, ReadDiskTemp(idx, bus), cfg.DiskThreshold, cfg.Tts, reminderMs);
        }
        catch { /* a transient sensor / IOCTL failure must not kill the watchdog */ }
        finally { _sampling = false; }
    }

    private static int? TempOrNull(double? t) => t is double v && v > 0 ? (int)Math.Round(v) : null;

    /// <summary>Core per-device state machine: detect over→under transitions (fire <see cref="Resolved"/>) and
    /// re-alert every <paramref name="reminderMs"/> while a non-ignored device stays over its threshold.</summary>
    private static void Evaluate(string key, string device, int? temp, int threshold, bool tts, long reminderMs)
    {
        if (temp is not int t) return;   // unreadable (e.g. CPU temp without admin) — skip, don't false-alarm
        var over = threshold > 0 && t >= threshold;
        var now = Environment.TickCount64;

        bool alert = false, resolved = false;
        lock (Gate)
        {
            if (!over)
            {
                if (_over.Remove(key)) { _lastAlert.Remove(key); resolved = true; }
            }
            else if (!_ignored.Contains(key))
            {
                _over.Add(key);
                if (!_lastAlert.TryGetValue(key, out var last) || now - last >= reminderMs)
                {
                    _lastAlert[key] = now;
                    alert = true;
                }
            }
        }

        if (alert) Alert(key, device, t, threshold, tts);
        if (resolved) { try { Resolved?.Invoke(key); } catch { } }
    }

    private static void Alert(string key, string device, int temp, int threshold, bool tts)
    {
        try
        {
            ToastService.TryShow(Localizer.T("tempmon.alert.title"),
                Localizer.Format("tempmon.alert.body", device, temp, threshold));
            AuditLog.App($"温度告警：{device} {temp}°C（阈值 {threshold}°C）");
            if (tts) Tts.Speak(Localizer.Format("tempmon.tts.alert", device, temp));
            Overheat?.Invoke(new OverheatInfo(key, device, temp, threshold));
        }
        catch { /* notification failure must not break the loop */ }
    }

    // ── disk enumeration + temperature ──────────────────────────────────────────
    /// <summary>Every physical disk (index + friendly name + bus), refreshed at most every 10 min.</summary>
    private static List<(int Index, string Name, string? Bus)> CurrentDisks()
    {
        var now = Environment.TickCount64;
        if (_disks.Count > 0 && now - _disksAge < DiskListTtlMs) return _disks;
        _disksAge = now;
        try
        {
            var snap = SystemInfo.GetAsync().GetAwaiter().GetResult();
            _disks = snap.PhysicalDisks
                .Where(p => int.TryParse(p.DeviceId, out _))
                .Select(p => (int.Parse(p.DeviceId!),
                              string.IsNullOrWhiteSpace(p.Name) ? Localizer.T("tempmon.device.disk") : p.Name,
                              p.Bus))
                .ToList();
        }
        catch { /* keep the previous list on failure */ }
        return _disks;
    }

    /// <summary>Temperature of one physical disk (°C), cached for <see cref="DiskTtlMs"/>. Tries the fast NVMe
    /// IOCTL first (internal + USB-NVMe, no admin); falls back to the smartctl SMART path for SATA / USB-SATA
    /// (needs admin). Returns null when no method can read it.</summary>
    private static int? ReadDiskTemp(int index, string? bus)
    {
        var now = Environment.TickCount64;
        if (_diskTemp.TryGetValue(index, out var c) && now - c.Tick < (c.Temp.HasValue ? DiskTtlMs : DiskMissTtlMs))
            return c.Temp;

        int? temp = null;
        try { temp = NvmeSmart.Read(index)?.TemperatureC; } catch { /* not NVMe / unsupported */ }
        if (temp is null or 0)
        {
            // SATA / USB-SATA — the comprehensive SMART path (smartctl + WMI). Heavy, but throttled by the TTL.
            try { temp = SystemInfo.GetSmartAsync(index.ToString()).GetAwaiter().GetResult().Temperature; }
            catch { /* unreadable without admin / unsupported bridge */ }
        }

        _diskTemp[index] = (now, temp);
        return temp;
    }
}
