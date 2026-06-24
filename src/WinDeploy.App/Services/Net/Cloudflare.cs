using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using WinDeploy.Core.I18n;

namespace WinDeploy.App.Services.Net;

/// <summary>One Cloudflare zone (a managed domain).</summary>
public sealed record CfZone(string Id, string Name, string Status);

/// <summary>One DNS record under a zone.</summary>
public sealed record CfDnsRecord(string Id, string ZoneId, string Type, string Name, string Content, bool Proxied, int Ttl);

/// <summary>Result of verifying an API token: validity + the token's reported status.</summary>
public sealed record CfVerifyResult(bool Valid, string Status, string? Error);

/// <summary>Thin client over the Cloudflare API v4 (<c>https://api.cloudflare.com/client/v4</c>), authenticated
/// with a scoped API token (Bearer). Used by the Cloudflare DDNS page to list zones / DNS records and to
/// create / update A·AAAA records so a device's public IP can be bound dynamically. Zero NuGet — System.Net.Http
/// + System.Text.Json only. API-level failures (incl. 401 / 403 permission errors) are surfaced as readable
/// messages rather than thrown, so the UI can explain what the token is missing.</summary>
public sealed class CloudflareClient
{
    private const string Base = "https://api.cloudflare.com/client/v4/";
    private readonly string _token;
    private readonly string? _email;

    /// <summary>Create a client. With no <paramref name="email"/> the credential is treated as a scoped
    /// <b>API Token</b> (Bearer auth — recommended). When an <paramref name="email"/> is given the credential
    /// is treated as a legacy <b>Global API Key</b> (X-Auth-Email / X-Auth-Key headers).</summary>
    public CloudflareClient(string token, string? email = null)
    {
        _token = Sanitize(token);
        _email = string.IsNullOrWhiteSpace(email) ? null : email.Trim();
    }

    public bool HasToken => _token.Length > 0;
    private bool GlobalKey => !string.IsNullOrWhiteSpace(_email);

    private HttpClient New()
    {
        var http = new HttpClient { BaseAddress = new Uri(Base), Timeout = TimeSpan.FromSeconds(25) };
        if (GlobalKey)
        {
            http.DefaultRequestHeaders.Add("X-Auth-Email", _email);
            http.DefaultRequestHeaders.Add("X-Auth-Key", _token);
        }
        else
        {
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _token);
        }
        http.DefaultRequestHeaders.UserAgent.ParseAdd("OwO-Win-Deployer");
        http.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        return http;
    }

    /// <summary>Verify the credential. API Token → <c>GET /user/tokens/verify</c> (checks the token is active);
    /// Global API Key → <c>GET /user</c> (the token-verify endpoint is token-only).</summary>
    public async Task<CfVerifyResult> VerifyAsync()
    {
        if (!HasToken) return new(false, "", Localizer.T("cloud.token.notFilled"));
        try
        {
            using var http = New();
            using var resp = await http.GetAsync(GlobalKey ? "user" : "user/tokens/verify");
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            var root = doc.RootElement;
            if (!Success(root, out var err)) return new(false, "", err);
            if (GlobalKey) return new(true, "active", null);   // /user succeeded → key + email are valid
            var status = root.TryGetProperty("result", out var r) && r.TryGetProperty("status", out var s)
                ? s.GetString() ?? "" : "";
            return new(status == "active", status, status == "active" ? null : Localizer.Format("cloud.token.status", status));
        }
        catch (Exception ex) { return new(false, "", Friendly(ex)); }
    }

    /// <summary>All zones (domains) the token can read, following pagination. Throws on API / network error
    /// with a readable message (the page catches and shows it).</summary>
    public async Task<List<CfZone>> ListZonesAsync()
    {
        var list = new List<CfZone>();
        using var http = New();
        int page = 1, totalPages = 1;
        do
        {
            using var doc = JsonDocument.Parse(await GetAsync(http, $"zones?per_page=50&page={page}"));
            var root = doc.RootElement;
            if (!Success(root, out var err)) throw new InvalidOperationException(err);
            foreach (var z in root.GetProperty("result").EnumerateArray())
                list.Add(new CfZone(
                    z.GetProperty("id").GetString() ?? "",
                    z.GetProperty("name").GetString() ?? "",
                    z.TryGetProperty("status", out var st) ? st.GetString() ?? "" : ""));
            totalPages = root.TryGetProperty("result_info", out var ri) && ri.TryGetProperty("total_pages", out var tp)
                ? tp.GetInt32() : 1;
            page++;
        } while (page <= totalPages && page <= 20);   // safety cap
        return list;
    }

    /// <summary>All DNS records under a zone, following pagination.</summary>
    public async Task<List<CfDnsRecord>> ListRecordsAsync(string zoneId)
    {
        var list = new List<CfDnsRecord>();
        using var http = New();
        int page = 1, totalPages = 1;
        do
        {
            using var doc = JsonDocument.Parse(await GetAsync(http, $"zones/{zoneId}/dns_records?per_page=100&page={page}"));
            var root = doc.RootElement;
            if (!Success(root, out var err)) throw new InvalidOperationException(err);
            foreach (var e in root.GetProperty("result").EnumerateArray())
                list.Add(ParseRecord(e));
            totalPages = root.TryGetProperty("result_info", out var ri) && ri.TryGetProperty("total_pages", out var tp)
                ? tp.GetInt32() : 1;
            page++;
        } while (page <= totalPages && page <= 20);
        return list;
    }

    /// <summary>Create a DNS record. Requires the token to have DNS:Edit on the zone (else a permission error
    /// message is returned).</summary>
    public async Task<(bool Ok, string Msg, CfDnsRecord? Record)> CreateRecordAsync(
        string zoneId, string type, string name, string content, bool proxied, int ttl)
    {
        try
        {
            using var http = New();
            using var resp = await http.PostAsync($"zones/{zoneId}/dns_records", Body(type, name, content, proxied, ttl));
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            var root = doc.RootElement;
            if (!Success(root, out var err)) return (false, err, null);
            return (true, Localizer.T("cloud.record.created"), ParseRecord(root.GetProperty("result")));
        }
        catch (Exception ex) { return (false, Friendly(ex), null); }
    }

    /// <summary>Overwrite a DNS record's content (the DDNS update). Requires DNS:Edit.</summary>
    public async Task<(bool Ok, string Msg)> UpdateRecordAsync(
        string zoneId, string recordId, string type, string name, string content, bool proxied, int ttl)
    {
        try
        {
            using var http = New();
            using var req = new HttpRequestMessage(HttpMethod.Put, $"zones/{zoneId}/dns_records/{recordId}")
            { Content = Body(type, name, content, proxied, ttl) };
            using var resp = await http.SendAsync(req);
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            return Success(doc.RootElement, out var err) ? (true, Localizer.T("cloud.record.updated")) : (false, err);
        }
        catch (Exception ex) { return (false, Friendly(ex)); }
    }

    private static StringContent Body(string type, string name, string content, bool proxied, int ttl)
    {
        var json = JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["type"] = type,
            ["name"] = name,
            ["content"] = content,
            ["ttl"] = ttl,
            ["proxied"] = proxied,
        });
        return new StringContent(json, Encoding.UTF8, "application/json");
    }

    private static async Task<string> GetAsync(HttpClient http, string path)
    {
        using var resp = await http.GetAsync(path);
        return await resp.Content.ReadAsStringAsync();
    }

    /// <summary>Cloudflare wraps every response in <c>{ success, errors[], result }</c>; treat success=false
    /// (including 401/403 permission denials) as a failure and join the error messages for the user.</summary>
    private static bool Success(JsonElement root, out string error)
    {
        error = "";
        if (root.TryGetProperty("success", out var s) && s.ValueKind == JsonValueKind.True) return true;
        if (root.TryGetProperty("errors", out var errs) && errs.ValueKind == JsonValueKind.Array && errs.GetArrayLength() > 0)
        {
            var msgs = new List<string>();
            foreach (var e in errs.EnumerateArray())
            {
                var m = e.TryGetProperty("message", out var mm) ? mm.GetString() : null;
                var c = e.TryGetProperty("code", out var cc) ? cc.GetRawText() : null;
                var part = string.IsNullOrWhiteSpace(m) ? null : (c != null ? $"{m} (#{c})" : m);
                // Cloudflare nests the specific reason (e.g. WHICH header is malformed) under error_chain.
                if (e.TryGetProperty("error_chain", out var chain) && chain.ValueKind == JsonValueKind.Array)
                    foreach (var ce in chain.EnumerateArray())
                    {
                        var cm = ce.TryGetProperty("message", out var cmm) ? cmm.GetString() : null;
                        var cc2 = ce.TryGetProperty("code", out var ccc) ? ccc.GetRawText() : null;
                        if (!string.IsNullOrWhiteSpace(cm)) part = (part ?? "") + $" → {cm}" + (cc2 != null ? $" (#{cc2})" : "");
                    }
                if (!string.IsNullOrWhiteSpace(part)) msgs.Add(part!);
            }
            error = msgs.Count > 0 ? string.Join("；", msgs) : Localizer.T("cloud.api.callFailed");
        }
        else error = Localizer.T("cloud.api.callFailedNoBody");
        return false;
    }

    private static CfDnsRecord ParseRecord(JsonElement e) => new(
        e.GetProperty("id").GetString() ?? "",
        e.TryGetProperty("zone_id", out var z) ? z.GetString() ?? "" : "",
        e.TryGetProperty("type", out var t) ? t.GetString() ?? "" : "",
        e.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "",
        e.TryGetProperty("content", out var c) ? c.GetString() ?? "" : "",
        e.TryGetProperty("proxied", out var p) && p.ValueKind == JsonValueKind.True,
        e.TryGetProperty("ttl", out var ttl) && ttl.TryGetInt32(out var tv) ? tv : 1);

    private static string Friendly(Exception ex) => ex switch
    {
        TaskCanceledException => Localizer.T("cloud.net.timeout"),
        HttpRequestException h => Localizer.Format("cloud.net.error", h.Message),
        _ => ex.Message,
    };

    /// <summary>Strip whitespace + zero-width / control characters that copy-paste from web pages can sneak
    /// into a token. Such an invisible character leaves the Authorization header well-formed (passes
    /// Cloudflare's header-format check) but makes the token value wrong → a confusing "Invalid API Token"
    /// (#1000). Cloudflare credentials are <c>[A-Za-z0-9_-]</c> with no inner whitespace, so this is safe.</summary>
    private static string Sanitize(string? raw)
    {
        if (string.IsNullOrEmpty(raw)) return "";
        var sb = new StringBuilder(raw.Length);
        foreach (var ch in raw)
            if (!char.IsWhiteSpace(ch) && !char.IsControl(ch) && ch is not ('\u200B' or '\u200C' or '\u200D' or '\uFEFF'))
                sb.Append(ch);
        return sb.ToString();
    }
}
