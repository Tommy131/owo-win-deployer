using System.Diagnostics;
using System.Globalization;

namespace WinDeploy.App.Services.Sys;

/// <summary>Best-effort lookup of a device's real power limit, used as the 100% reference for the system-overview
/// power bars. The NVIDIA board power limit (TGP) comes from <c>nvidia-smi</c> — exact, no admin needed; e.g.
/// an RTX 5070 Ti reports 300 W. Other devices fall back to a peak-hold estimate in the view model.</summary>
public static class PowerLimits
{
    private static readonly object Gate = new();
    private static double? _gpuW;
    private static bool _gpuQueried;

    /// <summary>NVIDIA GPU enforced board power limit in watts, or null when nvidia-smi is absent (non-NVIDIA /
    /// no driver). Queried once and cached. Runs nvidia-smi, so call it off the UI thread.</summary>
    public static double? NvidiaGpuLimitW()
    {
        lock (Gate)
        {
            if (_gpuQueried) return _gpuW;
            _gpuQueried = true;
            _gpuW = QueryNvidiaLimit();
            return _gpuW;
        }
    }

    private static double? QueryNvidiaLimit()
    {
        try
        {
            var psi = new ProcessStartInfo("nvidia-smi", "--query-gpu=power.limit --format=csv,noheader,nounits")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var p = Process.Start(psi);
            if (p == null) return null;
            var outp = p.StandardOutput.ReadToEnd();
            if (!p.WaitForExit(2500)) { try { p.Kill(); } catch { } return null; }

            // One line per GPU ("300.00"). Take the highest limit — the discrete card we care about.
            double best = 0;
            foreach (var line in outp.Split('\n'))
                if (double.TryParse(line.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var w) && w > best)
                    best = w;
            return best > 1 ? best : null;
        }
        catch { return null; }   // nvidia-smi not installed (non-NVIDIA system)
    }
}
