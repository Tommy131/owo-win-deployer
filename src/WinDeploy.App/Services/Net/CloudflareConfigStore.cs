using System.IO;
using System.Text.Json;

namespace WinDeploy.App.Services.Net;

/// <summary>One DDNS binding: a Cloudflare A / AAAA record this device keeps pointed at its current public IP.</summary>
public sealed class DdnsBinding
{
    public string ZoneId { get; set; } = "";
    public string ZoneName { get; set; } = "";
    public string RecordId { get; set; } = "";
    public string RecordName { get; set; } = "";
    public string Type { get; set; } = "A";    // A | AAAA
    public bool Proxied { get; set; }
    public int Ttl { get; set; } = 1;            // 1 = automatic
    public bool Enabled { get; set; } = true;
    /// <summary>Last public IP successfully written to this record (drives change detection + display).</summary>
    public string? LastIp { get; set; }
    /// <summary>ISO-8601 timestamp of the last successful update.</summary>
    public string? LastUpdate { get; set; }
}

public sealed class CloudflareConfig
{
    /// <summary>DPAPI-encrypted (CurrentUser) API token / Global API Key, base64. The plaintext is never persisted.</summary>
    public string? TokenProtected { get; set; }
    /// <summary>Account email — set only when the credential above is a legacy Global API Key (not a secret).</summary>
    public string? Email { get; set; }
    /// <summary>DDNS check interval in seconds (clamped 30 … 86400 at use sites).</summary>
    public int IntervalSeconds { get; set; } = 300;
    /// <summary>Start DDNS monitoring automatically when the app launches.</summary>
    public bool AutoStart { get; set; }
    public List<DdnsBinding> Bindings { get; set; } = new();
}

/// <summary>Persists the Cloudflare DDNS configuration to %LOCALAPPDATA%/WinDeploy/cloudflare.json. The API
/// token is stored DPAPI-encrypted (recoverable only by the same Windows user on this machine); it never
/// touches the repo or any plaintext file. All mutations are load-modify-save under a lock so the background
/// monitor and the UI never clobber each other.</summary>
public static class CloudflareConfigStore
{
    private static readonly string DirPath =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinDeploy");
    private static readonly string FilePathValue = Path.Combine(DirPath, "cloudflare.json");
    private static readonly JsonSerializerOptions Opt = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };
    private static readonly object Gate = new();

    public static string FilePath => FilePathValue;

    public static CloudflareConfig Load()
    {
        try
        {
            lock (Gate)
                if (File.Exists(FilePathValue))
                    return JsonSerializer.Deserialize<CloudflareConfig>(File.ReadAllText(FilePathValue), Opt) ?? new();
        }
        catch { /* fall through to defaults */ }
        return new();
    }

    public static void Save(CloudflareConfig cfg)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(DirPath);
                File.WriteAllText(FilePathValue, JsonSerializer.Serialize(cfg, Opt));
            }
        }
        catch { /* best effort */ }
    }

    /// <summary>The decrypted API token, or "" if none / undecryptable.</summary>
    public static string LoadToken()
    {
        var c = Load();
        return string.IsNullOrEmpty(c.TokenProtected) ? "" : Dpapi.Unprotect(c.TokenProtected);
    }

    public static void SaveToken(string? plain)
        => Mutate(c => c.TokenProtected = string.IsNullOrWhiteSpace(plain) ? null : Dpapi.Protect(plain.Trim()));

    /// <summary>Persist the credential and (optional) Global-API-Key email together.</summary>
    public static void SaveCredential(string? token, string? email) => Mutate(c =>
    {
        c.TokenProtected = string.IsNullOrWhiteSpace(token) ? null : Dpapi.Protect(token.Trim());
        c.Email = string.IsNullOrWhiteSpace(email) ? null : email.Trim();
    });

    public static void AddBinding(DdnsBinding b) => Mutate(c =>
    {
        c.Bindings.RemoveAll(x => x.RecordId == b.RecordId);
        c.Bindings.Add(b);
    });

    public static void RemoveBinding(string recordId) => Mutate(c => c.Bindings.RemoveAll(x => x.RecordId == recordId));

    public static void SetBindingEnabled(string recordId, bool enabled) => Mutate(c =>
    {
        var b = c.Bindings.FirstOrDefault(x => x.RecordId == recordId);
        if (b != null) b.Enabled = enabled;
    });

    public static void SetInterval(int seconds) => Mutate(c => c.IntervalSeconds = Math.Clamp(seconds, 30, 86400));

    public static void SetAutoStart(bool on) => Mutate(c => c.AutoStart = on);

    /// <summary>Record the IP / timestamp the monitor just applied to one or more bindings. Fresh load-merge so
    /// concurrent UI edits (adding / removing bindings) aren't lost.</summary>
    public static void ApplyResults(IEnumerable<(string RecordId, string Ip, string When)> results) => Mutate(c =>
    {
        foreach (var (recordId, ip, when) in results)
        {
            var b = c.Bindings.FirstOrDefault(x => x.RecordId == recordId);
            if (b == null) continue;
            b.LastIp = ip;
            b.LastUpdate = when;
        }
    });

    private static void Mutate(Action<CloudflareConfig> change)
    {
        lock (Gate)
        {
            var cfg = LoadNoLock();
            change(cfg);
            try
            {
                Directory.CreateDirectory(DirPath);
                File.WriteAllText(FilePathValue, JsonSerializer.Serialize(cfg, Opt));
            }
            catch { /* best effort */ }
        }
    }

    private static CloudflareConfig LoadNoLock()
    {
        try
        {
            if (File.Exists(FilePathValue))
                return JsonSerializer.Deserialize<CloudflareConfig>(File.ReadAllText(FilePathValue), Opt) ?? new();
        }
        catch { /* defaults */ }
        return new();
    }
}
