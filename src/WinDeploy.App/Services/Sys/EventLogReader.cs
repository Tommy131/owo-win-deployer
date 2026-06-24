using System.Text.Json;
using WinDeploy.Core.Util;

namespace WinDeploy.App.Services.Sys;

public sealed record EventRow(string Time, string Level, int Id, string Source, string Message)
{
    public bool IsCritical => Level.StartsWith("Crit", StringComparison.OrdinalIgnoreCase) || Level.Contains("严重");
    /// <summary>Unexpected-shutdown / BSOD heuristics (Kernel-Power 41, BugCheck 1001, WER 1000/1001).</summary>
    public bool IsCrash => (Source.Contains("Kernel-Power") && Id == 41) || Source.Contains("BugCheck") || Id == 1001;
}

/// <summary>Surfaces recent System/Application Critical+Error events (last 7 days) plus crash/BSOD markers,
/// for quick triage of a sick machine. Read-only, via Get-WinEvent (PowerShell) → JSON.</summary>
public static class EventLogReader
{
    private const string Ps = """
        [Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false)
        $since = (Get-Date).AddDays(-7)
        try {
          Get-WinEvent -FilterHashtable @{LogName='System','Application'; Level=1,2; StartTime=$since} -MaxEvents 80 -ErrorAction Stop |
            Select-Object @{n='Time';e={$_.TimeCreated.ToString('yyyy-MM-dd HH:mm')}}, LevelDisplayName, Id, ProviderName,
                          @{n='Message';e={ (($_.Message -split "`r?`n") | Where-Object { $_ -ne '' } | Select-Object -First 1) }} |
            ConvertTo-Json -Depth 3 -Compress
        } catch {}
        """;

    public static async Task<List<EventRow>> RecentAsync(CancellationToken ct = default)
    {
        var rows = new List<EventRow>();
        ProcResult r;
        try { r = await Proc.RunAsync("powershell", new[] { "-NoProfile", "-NonInteractive", "-Command", Ps }, ct: ct); }
        catch { return rows; }
        if (string.IsNullOrWhiteSpace(r.StdOut)) return rows;

        try
        {
            using var doc = JsonDocument.Parse(r.StdOut);
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Array)
                foreach (var e in root.EnumerateArray()) Add(e, rows);
            else if (root.ValueKind == JsonValueKind.Object)
                Add(root, rows);
        }
        catch { /* unparseable */ }
        return rows;
    }

    private static void Add(JsonElement e, List<EventRow> rows)
    {
        rows.Add(new EventRow(
            Str(e, "Time") ?? "",
            Str(e, "LevelDisplayName") ?? "",
            (int)Num(e, "Id"),
            Str(e, "ProviderName") ?? "",
            (Str(e, "Message") ?? "").Trim()));
    }

    private static string? Str(JsonElement e, string p)
        => e.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    private static long Num(JsonElement e, string p)
        => e.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) ? n : 0;
}
