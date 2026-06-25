using System.Net;
using System.Text.RegularExpressions;

namespace WinDeploy.Core.Net;

/// <summary>
/// Process-wide download proxy. When enabled it routes outbound HTTP(S) through the configured proxy by setting
/// <see cref="HttpClient.DefaultProxy"/>, so every <see cref="HttpClient"/> that uses the default handler — the
/// engine's file downloads, GitHub release lookups, icon fetches, etc. — goes through it without each call site
/// knowing about the proxy. Disabling restores whatever proxy the system had at startup (env / IE settings).
///
/// Accepts <c>http(s)://</c> and <c>socks5 / socks4://</c> proxies, optionally with <c>user:pass@</c> auth.
/// Input is validated against a strict regex (<see cref="IsValid"/>); the App additionally verifies live
/// connectivity (<see cref="TestAsync"/>) before persisting.
/// </summary>
public static class HttpProxy
{
    // scheme://[user:pass@]host:port  — host is a DNS name or IPv4; port checked numerically against 1..65535.
    private static readonly Regex Rx = new(
        @"^(?:https?|socks5|socks4)://(?:[^\s:@/]+:[^\s:@/]+@)?[A-Za-z0-9.\-]+:(\d{1,5})$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static IWebProxy? _systemDefault;
    private static bool _captured;

    /// <summary>Strict format check: a supported scheme, optional credentials, a host, and a 1–65535 port.</summary>
    public static bool IsValid(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;
        var m = Rx.Match(url.Trim());
        return m.Success && int.TryParse(m.Groups[1].Value, out var port) && port is >= 1 and <= 65535;
    }

    /// <summary>Apply the proxy globally when <paramref name="enabled"/> and the URL is valid; otherwise restore
    /// the system default captured at first call. Affects HttpClients created afterwards (our downloads are
    /// created per-request, so this takes effect immediately for them).</summary>
    public static void Apply(bool enabled, string? url)
    {
        try
        {
            if (!_captured) { _systemDefault = HttpClient.DefaultProxy; _captured = true; }
            HttpClient.DefaultProxy = enabled && IsValid(url)
                ? Build(url!.Trim())
                : _systemDefault ?? new WebProxy();
        }
        catch { /* keep the current proxy on any failure */ }
    }

    /// <summary>Build a WebProxy from a validated URL, lifting any <c>user:pass@</c> userinfo into
    /// <see cref="WebProxy.Credentials"/> (WebProxy ignores credentials embedded in the address otherwise).</summary>
    private static WebProxy Build(string url)
    {
        var uri = new Uri(url);
        var proxy = new WebProxy($"{uri.Scheme}://{uri.Host}:{uri.Port}") { BypassProxyOnLocal = true };
        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            var parts = uri.UserInfo.Split(':', 2);
            proxy.Credentials = new NetworkCredential(
                Uri.UnescapeDataString(parts[0]), parts.Length > 1 ? Uri.UnescapeDataString(parts[1]) : "");
        }
        return proxy;
    }

    /// <summary>Verify the proxy is actually usable: connect out through it to a couple of highly-available probes.
    /// Returns (false, reason) on bad format or no reachable probe. Used to gate saving.</summary>
    public static async Task<(bool Ok, string Detail)> TestAsync(string? url, CancellationToken ct = default)
    {
        if (!IsValid(url)) return (false, "format");
        try
        {
            using var handler = new HttpClientHandler { Proxy = Build(url!.Trim()), UseProxy = true };
            using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(8) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("WinDeploy/1.0");
            foreach (var probe in new[] { "https://www.gstatic.com/generate_204", "https://github.com" })
            {
                try
                {
                    using var resp = await http.GetAsync(probe, HttpCompletionOption.ResponseHeadersRead, ct);
                    if ((int)resp.StatusCode < 500) return (true, $"HTTP {(int)resp.StatusCode}");
                }
                catch { /* try the next probe */ }
            }
            return (false, "unreachable");
        }
        catch (Exception ex) { return (false, ex.Message); }
    }
}
