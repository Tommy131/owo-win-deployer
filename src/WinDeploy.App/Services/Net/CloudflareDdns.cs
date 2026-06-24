using System.Globalization;
using WinDeploy.Core.I18n;

namespace WinDeploy.App.Services.Net;

/// <summary>Background DDNS monitor: every interval it fetches the device's public IP and, for each enabled
/// binding, overwrites its Cloudflare A / AAAA record when the IP changed. Token + bindings + interval are
/// re-read from <see cref="CloudflareConfigStore"/> on every cycle, so UI edits take effect without a restart.
/// Resident — the loop runs on a background task and keeps working while the app is minimized to the tray.</summary>
public sealed class CloudflareDdnsMonitor
{
    private readonly object _gate = new();
    private readonly SemaphoreSlim _runGate = new(1, 1);   // serialize manual + periodic passes
    private CancellationTokenSource? _cts;

    public bool Running { get; private set; }
    public string? CurrentIpv4 { get; private set; }
    public string? CurrentIpv6 { get; private set; }
    public DateTime? LastCheck { get; private set; }
    public string LastResult { get; private set; } = Localizer.T("cloud.monitor.notRunYet");

    /// <summary>Raised after each cycle / state change. Handlers must marshal to the UI thread themselves.</summary>
    public event Action? Changed;

    /// <summary>Raised (title, body) only when a record was actually updated — caller shows a toast.</summary>
    public event Action<string, string>? Updated;

    public void Start()
    {
        lock (_gate)
        {
            if (Running) return;
            _cts = new CancellationTokenSource();
            Running = true;
            _ = Task.Run(() => LoopAsync(_cts.Token));
        }
        LastResult = Localizer.T("cloud.monitor.starting");
        Raise();
    }

    public void Stop()
    {
        lock (_gate)
        {
            if (!Running) return;
            try { _cts?.Cancel(); } catch { /* ignore */ }
            Running = false;
        }
        LastResult = Localizer.T("cloud.monitor.stoppedResult");
        Raise();
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await RunOnceAsync(ct);
                var interval = Math.Clamp(CloudflareConfigStore.Load().IntervalSeconds, 30, 86400);
                using var timer = new PeriodicTimer(TimeSpan.FromSeconds(interval));
                if (!await timer.WaitForNextTickAsync(ct)) break;
            }
        }
        catch (OperationCanceledException) { /* stopped */ }
        catch (Exception ex) { LastResult = Localizer.Format("cloud.monitor.unexpected", ex.Message); Raise(); }
    }

    /// <summary>Run one DDNS pass. Public so the page / tray can trigger an immediate check. Returns a summary
    /// string (also stored in <see cref="LastResult"/>).</summary>
    public async Task<string> RunOnceAsync(CancellationToken ct = default)
    {
        await _runGate.WaitAsync(ct);
        try { return await RunOnceCoreAsync(ct); }
        finally { _runGate.Release(); }
    }

    private async Task<string> RunOnceCoreAsync(CancellationToken ct)
    {
        var cfg = CloudflareConfigStore.Load();
        var token = string.IsNullOrEmpty(cfg.TokenProtected) ? "" : Dpapi.Unprotect(cfg.TokenProtected);
        var enabled = cfg.Bindings.Where(b => b.Enabled).ToList();

        LastCheck = DateTime.Now;
        if (token.Length == 0) return Finish(Localizer.T("cloud.monitor.noToken"));
        if (enabled.Count == 0) return Finish(Localizer.T("cloud.monitor.noEnabledBinding"));

        if (enabled.Any(b => b.Type == "A"))
        {
            var ip = await PublicIp.GetAsync(ipv6: false, ct);
            if (ip != null) CurrentIpv4 = ip;
        }
        if (enabled.Any(b => b.Type == "AAAA"))
        {
            var ip = await PublicIp.GetAsync(ipv6: true, ct);
            if (ip != null) CurrentIpv6 = ip;
        }

        var client = new CloudflareClient(token, cfg.Email);
        var applied = new List<(string RecordId, string Ip, string When)>();
        int updated = 0, unchanged = 0, failed = 0;

        foreach (var b in enabled)
        {
            ct.ThrowIfCancellationRequested();
            var ip = b.Type == "AAAA" ? CurrentIpv6 : CurrentIpv4;
            if (string.IsNullOrEmpty(ip)) { failed++; continue; }
            if (string.Equals(ip, b.LastIp, StringComparison.OrdinalIgnoreCase)) { unchanged++; continue; }

            var (ok, msg) = await client.UpdateRecordAsync(b.ZoneId, b.RecordId, b.Type, b.RecordName, ip, b.Proxied, b.Ttl);
            if (ok)
            {
                updated++;
                applied.Add((b.RecordId, ip, DateTime.Now.ToString("s", CultureInfo.InvariantCulture)));
                AuditLog.Action($"Cloudflare DDNS：{b.RecordName} ({b.Type}) → {ip}");
                Updated?.Invoke(Localizer.T("cloud.ddns.toastTitle"), $"{b.RecordName} → {ip}");
            }
            else
            {
                failed++;
                AuditLog.Warn($"Cloudflare DDNS 更新失败：{b.RecordName} — {msg}");
            }
        }

        if (applied.Count > 0) CloudflareConfigStore.ApplyResults(applied);
        return Finish(Localizer.Format("cloud.monitor.summary", updated, unchanged, failed));
    }

    private string Finish(string summary)
    {
        LastResult = $"{summary}（{LastCheck:HH:mm:ss}）";
        Raise();
        return LastResult;
    }

    private void Raise() { try { Changed?.Invoke(); } catch { /* ignore subscriber faults */ } }
}
