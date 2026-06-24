using System.Net;
using System.Net.Http;
using System.Net.Sockets;

namespace WinDeploy.App.Services.Net;

/// <summary>Detects the device's current public IP — the value a DDNS record must track. Tries ipify first,
/// then Cloudflare's own <c>cdn-cgi/trace</c> endpoint as a fallback, validating the returned address matches
/// the requested family (IPv4 for A records, IPv6 for AAAA). Returns null when offline / unreachable.</summary>
public static class PublicIp
{
    public static async Task<string?> GetAsync(bool ipv6, CancellationToken ct = default)
    {
        var urls = ipv6
            ? new[] { "https://api6.ipify.org", "https://[2606:4700:4700::1111]/cdn-cgi/trace" }
            : new[] { "https://api.ipify.org", "https://1.1.1.1/cdn-cgi/trace" };

        foreach (var url in urls)
        {
            try
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(12) };
                http.DefaultRequestHeaders.UserAgent.ParseAdd("OwO-Win-Deployer");
                var text = await http.GetStringAsync(url, ct);
                var ip = url.Contains("cdn-cgi/trace", StringComparison.OrdinalIgnoreCase) ? ParseTrace(text) : text.Trim();
                if (IsValid(ip, ipv6)) return ip;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return null; }
            catch { /* try the next provider */ }
        }
        return null;
    }

    private static string? ParseTrace(string body)
    {
        foreach (var line in body.Split('\n'))
            if (line.StartsWith("ip=", StringComparison.OrdinalIgnoreCase))
                return line[3..].Trim();
        return null;
    }

    private static bool IsValid(string? ip, bool ipv6)
    {
        if (string.IsNullOrWhiteSpace(ip) || !IPAddress.TryParse(ip, out var a)) return false;
        return ipv6
            ? a.AddressFamily == AddressFamily.InterNetworkV6
            : a.AddressFamily == AddressFamily.InterNetwork;
    }
}
