using LibreHardwareMonitor.Hardware;

namespace WinDeploy.App.Services.Sys;

public sealed record PowerReading(string Name, double Watts, string Kind);

/// <summary>A live hardware sample: CPU/GPU load + temperature, memory use, and per-device power draw.</summary>
public sealed class HwSample
{
    public double? CpuLoad { get; set; }
    public double? CpuTemp { get; set; }
    public double? GpuTemp { get; set; }
    public double? GpuLoad { get; set; }
    /// <summary>Short display name of the GPU whose temperature was captured (prefers a discrete card).</summary>
    public string? GpuName { get; set; }
    /// <summary>Whether the captured GPU is a discrete NVIDIA/AMD card (so it wins over an iGPU reading).</summary>
    internal bool GpuDiscrete { get; set; }
    public double? MemUsedGb { get; set; }
    public double? MemAvailGb { get; set; }
    /// <summary>NVIDIA GPU board power limit (W) for the GPU bar's 100% reference, or null (non-NVIDIA).</summary>
    public double? GpuPowerLimitW { get; set; }
    public List<PowerReading> Powers { get; } = new();
    public double TotalPower => Powers.Sum(p => p.Watts);
    public bool HasPower => Powers.Count > 0;
}

/// <summary>Reads live hardware sensors (CPU/GPU power &amp; temperature, memory) via LibreHardwareMonitor.
/// Power/temperature sensors that read MSRs (CPU package power) require the app to run as administrator;
/// without it those sensors are simply absent (GPU power via vendor APIs often still works).</summary>
public static class HardwareMonitor
{
    private static readonly object Gate = new();
    private static Computer? _computer;
    private static bool _failed;

    public static bool Available { get { lock (Gate) return _computer != null; } }

    public static bool TryInit()
    {
        lock (Gate)
        {
            if (_computer != null) return true;
            if (_failed) return false;
            try
            {
                var c = new Computer
                {
                    IsCpuEnabled = true,
                    IsGpuEnabled = true,
                    IsMemoryEnabled = true,
                    // Storage is intentionally DISABLED: LibreHardwareMonitor keeps an open handle on every
                    // physical disk (incl. USB ones) to read storage SMART, and that handle — living inside our
                    // own process — vetoes "安全弹出"/eject of removable drives (and can't be reliably released
                    // via Close()). We read disk SMART/health ourselves (smartctl + WMI), so the only thing lost
                    // is per-disk power readings, which most drives don't expose anyway.
                    IsStorageEnabled = false,
                    IsMotherboardEnabled = true,
                };
                c.Open();
                _computer = c;
                return true;
            }
            catch { _failed = true; return false; }
        }
    }

    public static HwSample Sample()
    {
        var s = new HwSample();
        lock (Gate)
        {
            if (_computer == null) return s;
            foreach (var hw in _computer.Hardware)
            {
                try { hw.Update(); foreach (var sub in hw.SubHardware) sub.Update(); } catch { continue; }
                Read(hw, s);
                foreach (var sub in hw.SubHardware) Read(sub, s);
            }
        }
        // Real GPU power limit (cached after the first nvidia-smi call) — used as the GPU bar's 100% reference.
        s.GpuPowerLimitW = PowerLimits.NvidiaGpuLimitW();
        return s;
    }

    private static void Read(IHardware hw, HwSample s)
    {
        var kind = hw.HardwareType switch
        {
            HardwareType.Cpu => "CPU",
            HardwareType.GpuNvidia or HardwareType.GpuAmd or HardwareType.GpuIntel => "GPU",
            HardwareType.Storage => "硬盘",
            HardwareType.Memory => "内存",
            _ => "其他",
        };

        foreach (var sensor in hw.Sensors)
        {
            if (sensor.Value is not float val) continue;
            switch (sensor.SensorType)
            {
                case SensorType.Power when val > 0.05f:
                    s.Powers.Add(new PowerReading($"{Short(hw.Name)} · {sensor.Name}", val, kind));
                    break;
                case SensorType.Load when hw.HardwareType == HardwareType.Cpu && sensor.Name.Contains("Total"):
                    s.CpuLoad = val;
                    break;
                case SensorType.Temperature when hw.HardwareType == HardwareType.Cpu && s.CpuTemp == null
                                                 && (sensor.Name.Contains("Package") || sensor.Name.Contains("Tdie") || sensor.Name.Contains("Tctl") || sensor.Name.Contains("Core")):
                    s.CpuTemp = val;
                    break;
                // GPU core temperature — works without admin (vendor APIs). Prefer a discrete card (NVIDIA/AMD)
                // over an Intel iGPU when both are present.
                case SensorType.Temperature when kind == "GPU" && sensor.Name.Contains("Core")
                                                 && (s.GpuTemp == null || (IsDiscreteGpu(hw) && !s.GpuDiscrete)):
                    s.GpuTemp = val;
                    s.GpuName = Short(hw.Name);
                    s.GpuDiscrete = IsDiscreteGpu(hw);
                    break;
                case SensorType.Load when kind == "GPU" && sensor.Name.Contains("GPU Core")
                                          && (s.GpuLoad == null || IsDiscreteGpu(hw)):
                    s.GpuLoad = val;
                    s.GpuName ??= Short(hw.Name);
                    break;
                case SensorType.Data when hw.HardwareType == HardwareType.Memory && sensor.Name == "Memory Used":
                    s.MemUsedGb = val;
                    break;
                case SensorType.Data when hw.HardwareType == HardwareType.Memory && sensor.Name == "Memory Available":
                    s.MemAvailGb = val;
                    break;
            }
        }
    }

    private static bool IsDiscreteGpu(IHardware hw)
        => hw.HardwareType is HardwareType.GpuNvidia or HardwareType.GpuAmd;

    private static string Short(string name) => name.Length > 28 ? name[..28] + "…" : name;

    public static void Close()
    {
        lock (Gate) { try { _computer?.Close(); } catch { } _computer = null; }
    }
}
