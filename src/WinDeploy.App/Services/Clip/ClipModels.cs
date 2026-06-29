using System.Text;
using System.Text.Json.Serialization;

namespace WinDeploy.App.Services.Clip;

/// <summary>Edition gate. The open-source build caps the share at <see cref="MaxPeers"/> devices
/// (this device + N−1 remotes); a future paid build raises it and routes through a relay server.
/// The wire <see cref="Proto"/> guards against pairing across incompatible versions.</summary>
public static class ClipEdition
{
    /// <summary>Total devices allowed in one clipboard share, including this machine. OSS = 2.</summary>
    public const int MaxPeers = 2;

    /// <summary>Max simultaneous remote links = devices − this one.</summary>
    public static int MaxLinks => Math.Max(1, MaxPeers - 1);

    /// <summary>Wire protocol version; bumped on any breaking change to the handshake / message shapes.</summary>
    public const int Proto = 1;

    /// <summary>Magic marker so we never mistake an unrelated TCP/UDP packet for a clip peer.</summary>
    public const string Magic = "OWOCLIP1";
}

/// <summary>The kind of payload one clipboard entry carries (v1: text + image).</summary>
public enum ClipKind { Text, Image }

/// <summary>One shared clipboard item. Pure data — serialized to JSON for the wire and (optionally)
/// for on-disk history. UI-facing display (thumbnails, localized preview) is computed in the row VM.</summary>
public sealed class ClipEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public ClipKind Kind { get; set; }

    /// <summary>Text payload (Kind == Text). Rich text is captured as its plain-text projection.</summary>
    public string? Text { get; set; }

    /// <summary>PNG-encoded bitmap (Kind == Image). System.Text.Json (de)serializes this as base64.</summary>
    public byte[]? Image { get; set; }
    public int ImageW { get; set; }
    public int ImageH { get; set; }

    /// <summary>Instance id of the device that produced this entry.</summary>
    public string OriginId { get; set; } = "";
    /// <summary>Friendly device name that produced this entry (for the board's 来源 column).</summary>
    public string OriginName { get; set; } = "";

    /// <summary>Unix-ms creation time; rendered locally in the viewer.</summary>
    public long CreatedAtUnix { get; set; }

    [JsonIgnore] public DateTime CreatedAt => DateTimeOffset.FromUnixTimeMilliseconds(CreatedAtUnix).LocalDateTime;

    /// <summary>A stable content fingerprint (Kind + payload) used to suppress echo loops when a remote
    /// entry is mirrored onto the local clipboard, and to dedupe identical consecutive copies.</summary>
    public string ContentHash()
    {
        var sb = new StringBuilder();
        sb.Append((int)Kind).Append('|');
        if (Kind == ClipKind.Text) sb.Append(Text);
        else if (Image != null) sb.Append(Convert.ToBase64String(System.Security.Cryptography.SHA256.HashData(Image)));
        return sb.ToString();
    }

    public static long NowUnix() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
}

/// <summary>A device seen on the LAN via the UDP discovery beacon. Not yet trusted — pairing (PIN) is
/// required before any clipboard data flows.</summary>
public sealed class ClipPeer
{
    public string InstanceId { get; init; } = "";
    public string DeviceName { get; set; } = "";
    public string Address { get; set; } = "";   // IPv4 of the beacon sender
    public int Port { get; set; }                 // remote TCP listen port for pairing
    public string Version { get; set; } = "";     // app version (display only)
    public int Proto { get; set; }
    public DateTime LastSeen { get; set; } = DateTime.Now;
}

/// <summary>Persisted clipboard-sync settings (clipsync.json). Clipboard CONTENT is never stored here;
/// on-disk history (when enabled) lives in a separate file and is opt-in.</summary>
public sealed class ClipSyncConfig
{
    /// <summary>Stable per-install identity advertised in beacons and bound into the pairing proof. Generated
    /// once (when blank) and persisted, so this device keeps the same id across restarts.</summary>
    public string InstanceId { get; set; } = "";

    /// <summary>Friendly name advertised to peers and shown as the 来源 of entries from this machine.</summary>
    public string DeviceName { get; set; } = Environment.MachineName;

    /// <summary>TCP port this device listens on for inbound pairing + the encrypted link.</summary>
    public int Port { get; set; } = 47655;

    /// <summary>UDP port for the multicast presence beacon (discovery).</summary>
    public int DiscoveryPort { get; set; } = 47654;

    /// <summary>Bind discovery to one interface IP (the real LAN NIC) instead of all of them; "" = all (auto).
    /// Use when many virtual adapters (VMware / Hyper-V / WSL / VirtualBox) make auto-discovery pick the
    /// wrong NIC. Manual connect-by-IP is unaffected by this.</summary>
    public string DiscoveryInterface { get; set; } = "";

    /// <summary>When true, an arriving remote entry is also written to the local Windows clipboard
    /// (true "sync"); when false, it only enters the shared board for preview/manual copy. Default off.</summary>
    public bool AutoApplyToLocal { get; set; } = false;

    /// <summary>When true, the shared board is persisted to disk and survives a restart; when false it lives
    /// only in memory (safer for sensitive clipboard content). Default off.</summary>
    public bool PersistHistory { get; set; } = false;

    /// <summary>Max entries kept in the board / on-disk history (oldest pruned beyond this).</summary>
    public int HistoryLimit { get; set; } = 100;

    /// <summary>Images larger than this (bytes, PNG-encoded) are dropped rather than shared. Default 4 MB.</summary>
    public int MaxImageBytes { get; set; } = 4 * 1024 * 1024;

    public ClipSyncConfig Clone() => (ClipSyncConfig)MemberwiseClone();
}
