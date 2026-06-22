namespace WinDeploy.Core.Models;

/// <summary>Root of catalog.json — the single source of truth for what can be installed.</summary>
public sealed class Catalog
{
    public int SchemaVersion { get; set; } = 1;
    public Dictionary<string, string> PathVars { get; set; } = new();
    public List<string> Categories { get; set; } = new();
    public List<CatalogItem> Items { get; set; } = new();
}

/// <summary>One installable software / toolchain / environment entry.</summary>
public sealed class CatalogItem
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Summary { get; set; }
    public string? Icon { get; set; }
    public string? Homepage { get; set; }
    public string Category { get; set; } = "";

    /// <summary>Pre-checked by default (forced tier). Optional items are false.</summary>
    public bool Default { get; set; }

    /// <summary>Pinned version (portable packages). Null = latest.</summary>
    public string? Version { get; set; }

    public List<string>? Depends { get; set; }

    public InstallSpec Install { get; set; } = new();
    public DetectSpec? Detect { get; set; }
    public ConfigSpec? Config { get; set; }

    /// <summary>User-chosen install path for this run (winget --location / portable extractTo / git dest). Not persisted.</summary>
    public string? InstallPathOverride { get; set; }
}

/// <summary>How to install an item. Fields used depend on <see cref="Method"/>.</summary>
public sealed class InstallSpec
{
    /// <summary>winget | winget-bundle | portable | git | conda | vscode-ext | script</summary>
    public string Method { get; set; } = "";

    // winget
    public string? Id { get; set; }
    public string? Scope { get; set; }

    // winget-bundle
    public List<string>? Ids { get; set; }

    // portable
    public string? Url { get; set; }
    public string? Sha256 { get; set; }
    public string? ExtractTo { get; set; }
    public int? Strip { get; set; }
    public List<string>? Path { get; set; }

    // git
    public string? Repo { get; set; }
    public string? Branch { get; set; }
    public string? Dest { get; set; }

    // exe (download an installer and run it); Args = optional silent flags
    public string? Args { get; set; }

    // conda
    public string? EnvFile { get; set; }
    public string? EnvName { get; set; }

    // vscode-ext
    public string? Extensions { get; set; }

    // script
    public string? Run { get; set; }
}

/// <summary>Idempotency probe — how to tell an item is already installed.</summary>
public sealed class DetectSpec
{
    public string? Cmd { get; set; }
    public string? Path { get; set; }
    public string? WingetId { get; set; }

    /// <summary>Display-name prefix to match in ARP (`winget list`) — for apps winget doesn't track by id.</summary>
    public string? Arp { get; set; }
}

/// <summary>Config payload that travels in the repo regardless of install state.</summary>
public sealed class ConfigSpec
{
    /// <summary>Repo dir holding the config payload (default configs/&lt;id&gt;).</summary>
    public string? Source { get; set; }

    /// <summary>Machine base dir the files live in (e.g. %APPDATA%/Code/User).</summary>
    public string? Target { get; set; }

    /// <summary>Specific files to sync (relative). Required for precise export/capture.</summary>
    public List<string>? Files { get; set; }

    /// <summary>VS Code extension list path (for the vscode-ext flow).</summary>
    public string? Extensions { get; set; }

    /// <summary>always | ifInstalled | ask</summary>
    public string ApplyWhen { get; set; } = "ifInstalled";
}

/// <summary>A named preset that selects a subset of the catalog.</summary>
public sealed class Profile
{
    public string Name { get; set; } = "";
    public string? Extends { get; set; }

    /// <summary>Tokens: an item id, "@category:&lt;cat&gt;", "@all", or "@default".</summary>
    public List<string> Select { get; set; } = new();
    public List<string> Deselect { get; set; } = new();
}
