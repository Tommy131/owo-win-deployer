# OwO! Win Deployer (owo-win-deployer)

> [!CAUTION]
> # ⛔ Please Do Not Fork This Repository ⛔
>
> **This project is in an unstable active-development phase. Git history may be rewritten and files may be added or removed at any time.**
>
> - 🚫 **Do NOT submit Pull Requests** — unsolicited PRs will not be accepted.
> - ✅ If you encounter a problem, please [open an Issue](../../issues) instead.
> - ⚠️ Forks may permanently diverge from upstream due to history rewrites.

---

Replicate your entire Windows development environment, applications, and personal dotfiles on any new machine with a single command — plus an integrated suite of system-administration, terminal, FTP, Cloudflare DDNS, LAN clipboard sync, and monitoring tools.

Supports **Chinese (zh) / English (en) / Deutsch (de)** with live in-app language switching.

> **Current version: v1.2.6** &nbsp;|&nbsp; 🌐 [中文说明](README_CH.md)

## Table of Contents

- [Screenshots](#screenshots)
- [Features](#features)
  - [Multilingual Support](#multilingual-support-v120)
  - [Cloudflare DDNS](#cloudflare-ddns-developer-mode)
  - [Security Hardening](#security-hardening-v121)
- [Architecture](#architecture)
- [Quick Start](#quick-start)
- [Build & Development](#build--development-requires-net-sdk-10)
- [CLI Reference](#cli-reference)
- [Software Catalog](#software-catalog)
- [Usage Notes](#usage-notes)
- [License](#license)

---

## Screenshots

| Software Install Center (card grid · right-click menu) | Installation Progress (live download / log) |
| :---: | :---: |
| ![Install Center](assets/images/install-center.png) | ![Progress](assets/images/progress.png) |
| **Process Manager (grouped by app)** | **Process Manager (expanded tree · CPU / RAM)** |
| ![Process Manager](assets/images/process-manager.png) | ![Process Manager expanded](assets/images/process-manager-expanded.png) |
| **Startup Item Manager** | **Config Export (auto-redaction)** |
| ![Startup](assets/images/startup-items.png) | ![Export](assets/images/export.png) |
| **Settings · Theme / Path Vars / Repository** | **About / Developer** |
| ![Settings](assets/images/settings.png) | ![About](assets/images/about.png) |
| **Run Log** | |
| ![Logs](assets/images/logs.png) | |

---

## Features

All milestones are complete:

| Milestone | Summary | Status |
|:---:|---|:---:|
| M1 | Install engine (winget / portable / git / conda / vscode-ext / script) + idempotent detection + path variables + CLI | ✅ |
| M2 | Config apply/capture + secret redaction + env-var management + per-device SSH key generation | ✅ |
| M3 | WPF Software Install Center (icon cards, per-item checkboxes) + live progress + config/export pages | ✅ |
| M4 | Self-contained single-EXE publish + Release CI + multi-machine sync (sync/save) + version locking | ✅ |
| M5 | System management suite (terminal · FTP · processes · services · overview · maintenance · WSL · tweaks · Cloudflare DDNS) + Developer Mode gate | ✅ |
| M6 | LAN clipboard sync — encrypted text + image sharing over the local network (UDP discovery · PIN pairing · AES-256-GCM) | ✅ |

### Software Install Center (M1 · M3)

- **126 software packages** (15 categories) displayed as icon cards — check individually or select by group
- **Search** filter + **Profile** switcher (`dev` / `full` / `ai-station`) in one click
- **Install methods**: winget · winget-bundle · portable · git · conda · vscode-ext · exe · script · github-release · local · manual
- **Idempotent**: detects already-installed items and skips them; supports dry-run preview
- Right-click menu: Install / Uninstall / Launch / Stop / Restart / Update / View log
- Live progress: download speed, ETA, per-item status, scrolling log tail

### Config Sync (M2)

- **Bidirectional apply / capture**: VS Code · Git · SSH · env vars · Windows Terminal · PowerShell · npm · conda · pip
- Export auto-**redacts** tokens, passwords, and API keys; customizable redaction keywords
- SSH keys **generated fresh per device** (ed25519), one-click register to GitHub, private keys never stored in the repo
- Automatic backup before apply (`.bak.yyyyMMdd-HHmmss`)

### Multi-Machine Sync (M4)

- `catalog/lock.json` pins installed versions for reproducible installs across machines
- `git pull` → apply configs + show install plan (`sync` command)
- Hostname → profile mapping (`catalog/hosts.json`), automatically picks the right preset per machine
- Migration kit: pack configs + software list into a ZIP for transfer to a new device

### Terminal (M5)

- **ConPTY pseudo-console**: full interactive support for PowerShell / cmd / ssh / vim, including password prompts
- **VT100/ANSI emulator**: 16-color, 256-color, 24-bit true color, 5,000-line scrollback
- **Multi-session**: tabbed terminals, a configurable shell catalog (PowerShell / cmd / WSL etc.), named sessions with an edit dialog
- **Visual effects**: Hacker-FX (green phosphor · glow · scanlines) + CodeRain matrix rain — toggle each independently in settings, persisted across restarts
- Shell switching, live PTY resize; close-confirmation dialog to prevent accidental tab close

### FTP Server & Client (M5)

- **Self-hosted FTP/FTPS server**: custom port, active/passive mode, TLS encryption, concurrent connection limit
- **User & permission group management**: fine-grained read/write permissions; MLSD perm facts advertised to the client (disables unavailable actions automatically)
- **Auto-generated TLS certificate** (self-signed X.509 RSA 2048-bit, no manual openssl required)
- **FTP client**: dual-pane file browser with local/remote context menus (open / upload / download / rename / delete)
- **Multi-select batch transfer**: Ctrl/Shift-select files and folders; batch upload / download / delete; Explorer-style right-click preserves multi-selection
- **Live transfer speed + ETA**: pre-scans total size, samples progress every 0.5 s
- **Site manager (Saved Logins)**: saved credentials encrypted with DPAPI for quick reconnect
- **FTPS trust-on-first-use (TOFU)**: records the certificate SHA-256 on first connect (`ftp_trust.json`); a changed cert is refused as a potential MITM
- 15-second connect/TLS timeout; persistent config (port / TLS / encoding / rate limit etc.)

### Clipboard Sync (M6 · Developer Mode)

- **Discover & pair**: finds other machines on the LAN that also run OwO! WinDeploy (UDP multicast beacon — presence only), then pairs them with a one-time **6-digit PIN**. The PIN never goes on the wire — both sides prove it via a mutual HMAC challenge/response (PBKDF2), then derive an **AES-256-GCM** session key for an end-to-end encrypted link.
- **Share text + images**: a shared board syncs both ways — preview entries, add text manually, **delete propagates across devices**, and copy any entry back to the local clipboard. Image thumbnails + a fit-to-pane preview; click to open full size in a window.
- **Robust discovery**: per-interface multicast + directed subnet broadcast, a **listen-NIC picker** for machines with many virtual adapters, and a **manual connect-by-IP** fallback for networks that block multicast.
- **Optional, off by default**: *auto-mirror to the local clipboard* (true sync) and *persist history to disk*; otherwise clipboard content stays in memory and clears on exit.
- **Device cap**: the open-source build shares up to **2 devices**; the transport is abstracted for a future relay-server build that lifts the cap beyond the LAN.

### Process Manager (M5)

- **Tree view grouped by application**: real-time CPU / RAM display
- Filter, bulk actions (launch / kill / restart)
- Icons extracted from running processes, automatically grouped

### System Overview (M5)

- CPU · RAM · disk · battery · Windows activation status at a glance
- **Enhanced SMART**: bundled `smartctl` (ships with every release) reads internal NVMe/SATA **and external USB** drives; auto-detects ASMedia / JMicron / Realtek USB bridge chips; NVMe health log + full ATA attribute table; prompts admin relaunch when permissions are insufficient
- Disk type badge: NVMe / SSD / HDD / USB
- **Safe USB eject**: per-card eject button; offers force-eject (dismount volumes + release handles) when the OS rejects the request; removes the card from the UI immediately on success
- One-click export of installed software inventory (CSV / HTML / JSON)

### System Maintenance (M5)

- **Repair tools**: one-click SFC · DISM /RestoreHealth · chkdsk (UAC elevated on demand)
- **Network reset**: TCP/IP stack · Winsock · DNS cache
- **Cache cleanup**: Temp · Windows Update cache · thumbnails · Windows.old
- Icon cache rebuild
- **Event log digest**: last 7 days of critical/error events summarized

### WSL (M5 · Developer Mode)

- Distribution list · online install · set default · start / stop
- Export backup (TAR) · unregister

### System Tweaks (M5 · Developer Mode)

- Reversible registry toggles — live-reads current state before each change:
  - Show file extensions / show hidden files
  - Dark / light mode
  - Classic context menu (Win 11)
  - Disable telemetry · other common Explorer options

### Service Manager (M5)

- Configure Windows service startup type (Automatic / Manual / Disabled)
- Start · stop · restart · real-time status monitoring

### Startup Items (M5)

- Read/write registry `Run` / `RunOnce` (HKCU + HKLM)
- Enable / disable with audit log

### Advanced Tools (M5 · Developer Mode)

- Environment health check (duplicate/broken PATH entries, missing `*_HOME` vars)
- `catalog.json` validation (CI-friendly, exit code 1 on error)
- Generate `lock.json` (version snapshot)
- Export `winget configure` DSC YAML (for Intune / GPO / SCCM)
- Offline deployment kit (pre-download + bundle)
- Migration kit export / restore

### System Tray (M5)

- Minimize to tray for background operation
- Right-click quick menu: start / stop / restart FTP server, cached web-service status (async probed)
- Quick-navigate to any main-window page

### Multilingual Support (v1.2.0+)

- **Live language switching**: Chinese (zh) / English (en) / Deutsch (de) — no restart required, all UI text updates instantly
- **First-run auto-detect**: follows the OS UI language (zh / de preferred; otherwise defaults to English)
- **CLI**: `--lang zh/en/de` flag or `WINDEPLOY_LANG` environment variable
- Software catalog summaries translated via `catalog/i18n/{en,de}.json` sidecars; 1,450 UI keys × 3 languages
- Confirm dialogs use localized button labels (not fixed to the OS language)
- `scripts/check-i18n.ps1` enforces key-set and placeholder parity across all three languages

### Cloudflare DDNS (Developer Mode)

- Manage DNS records (A / AAAA / CNAME / MX / TXT etc.) via the Cloudflare API
- **Auto DDNS**: polls the machine's public IP on a timer and updates the designated A record when the IP changes — ideal for residential dynamic-IP connections
- Multiple domains / records supported; toggle Cloudflare proxy (orange cloud) per record
- Config (API token · Zone · records) stored locally with encryption; tray menu shows current DDNS status

### Security Hardening (v1.2.1)

- **FTPS certificate pinning (TOFU)**: first-connect fingerprint recorded; subsequent mismatch is refused with a clear warning
- **WSL injection protection**: WSL operations invoke `wsl.exe` directly via `ArgumentList` (no cmd shell), distribution names validated against a strict allowlist
- **Web server config injection protection**: vhost `server_name` / document root / cert paths reject `;{}#"` and newline characters, preventing directive injection into nginx/Apache configs
- **EXE installer SHA-256 verification**: when the catalog provides a `sha256` field, the downloaded installer is verified before execution
- **ViewModel event leak fix**: transient VMs (software detail, service detail) are properly disposed on navigate-away, eliminating static `CultureChanged` subscription leaks

### App Reliability

- **Global crash handler**: catches unhandled exceptions on the UI thread, AppDomain, and Tasks; writes `crash.log` and shows an error dialog — the app stays alive instead of silently crashing to the desktop

### Settings & Appearance

- Light / dark theme, live toggle
- Path variable configuration (`${DevRoot}` / `${ToolsDir}`)
- Repository URL and mirror source
- Custom redaction keywords
- **Language picker**: instant switch between zh / en / de in the settings page
- **Developer Mode** gate (unlocks WSL · Tweaks · Advanced Tools · Cloudflare DDNS · terminal FX)

---

## Architecture

```
owo-win-deployer/
├── src/
│   ├── WinDeploy.Core/          # Pure library: install engine, config sync, data models, i18n
│   │   ├── Catalog/             # JSON parsing, profile resolution
│   │   ├── Engine/              # Install orchestration, detection, method dispatch
│   │   ├── Config/              # Config apply / capture / redaction
│   │   ├── Export/              # DSC export, software inventory, migration kit
│   │   ├── I18n/                # Localizer (zh/en/de embedded JSON, runtime switching)
│   │   ├── Models/              # Data models
│   │   └── Util/                # Logging, process helpers, path utilities
│   ├── WinDeploy.Cli/           # CLI entry point (thin wrapper over Core)
│   └── WinDeploy.App/           # WPF GUI (self-contained single EXE)
│       ├── Views/               # 20+ pages + dialogs (folder = sub-namespace)
│       │   ├── Deploy/          # Install center, progress, config sync, export
│       │   ├── Sys/             # System overview, maintenance, WSL, tweaks
│       │   ├── Ftp/             # FTP server, client, config
│       │   ├── Cloudflare/      # Cloudflare DDNS page + dialogs
│       │   ├── Server/          # Web server management
│       │   ├── Terminal/        # ConPTY terminal + session dialogs
│       │   ├── Tools/           # Advanced tools, startup items
│       │   ├── Shell/           # Log view, settings
│       │   └── Common/          # Shared dialogs (MessageDialog, ChoiceDialog, …)
│       ├── ViewModels/          # Matching ViewModels (MVVM)
│       │   ├── Deploy/  Sys/  Ftp/  Net/  Terminal/  Tools/  Shell/
│       │   └── (root)   # MainViewModel, ObservableObject, LocalizedObject
│       ├── Services/            # 40+ system integration services
│       │   ├── Sys/             # System info, SMART, WSL, process, tweaks, eject
│       │   ├── Net/             # Cloudflare, server manager, service config
│       │   ├── Software/        # Icons, ARP, store apps, update checker
│       │   ├── Terminal/        # ConPty, VtScreen, TerminalFx
│       │   ├── Ftp/             # FTP server, client, session, cert, trust store
│       │   └── Infra/           # Theme, toast, tray, DPAPI, audit log, settings
│       └── Behaviors/           # Custom WPF input behaviors (InputFilter, MenuTidy)
├── catalog/
│   ├── catalog.json             # 126-item software manifest (JSONC)
│   ├── i18n/{en,de}.json        # Catalog summary translations
│   ├── profiles/                # Install presets (dev · full · ai-station)
│   └── hosts.example.json       # Hostname → preset mapping template
├── configs/                     # Dotfile repository (decoupled from install state)
├── tools/                       # Bundled binaries (smartctl, drivedb.h)
├── bootstrap/bootstrap.ps1      # Bare-metal bootstrap (one-liner)
├── scripts/
│   ├── publish.ps1              # Build self-contained EXEs
│   └── check-i18n.ps1           # Validate zh/en/de key parity
├── docs/DESIGN.md               # Full design document (authoritative source)
└── WinDeploy.sln
```

### Layer Summary

| Layer | Project | Notes |
|---|---|---|
| Engine | `WinDeploy.Core` | Pure .NET 10 library; no registry/WMI dependency (usable in CLI and GUI) |
| CLI | `WinDeploy.Cli` | Thin wrapper exposing 14 commands; suitable for scripts and CI |
| GUI | `WinDeploy.App` | WPF MVVM; system integration (WMI · ConPTY · P/Invoke) isolated to this layer |

### Key Design Principles

- **Data / engine separation**: `catalog.json` is pure data; adding software requires no engine changes
- **Idempotent installs**: detect-then-install; safe to re-run
- **Path variables**: `${DevRoot}` / `${ToolsDir}` set once on first run, work across machines
- **Security-first**: SSH private keys generated per device, never committed; exports auto-redact secrets
- **Developer Mode gate**: registry / WSL / advanced features require a confirmation dialog

---

## Quick Start

### Bare-Metal Bootstrap (no .NET required on target machine)

```powershell
irm https://raw.githubusercontent.com/Tommy131/owo-win-deployer/main/bootstrap/bootstrap.ps1 | iex
```

The script verifies winget, downloads the latest `WinDeploy.exe` from GitHub Releases, and launches it.

### Direct Download

Visit the [Releases](../../releases) page and download `WinDeploy.exe` (GUI) or `windeploy.exe` (CLI).

> **Prefer the ZIP package**: the folder-based ZIP is less likely to trigger antivirus heuristics than the single-file self-extracting EXE.

---

## Build & Development (requires .NET SDK 10)

```powershell
# Build the full solution
dotnet build WinDeploy.sln

# Run the GUI
dotnet run --project src/WinDeploy.App

# Run the CLI
dotnet run --project src/WinDeploy.Cli -- list
dotnet run --project src/WinDeploy.Cli -- plan  --profile dev
dotnet run --project src/WinDeploy.Cli -- apply --profile dev --yes
```

### Publish (self-contained single EXE, no .NET required on target)

```powershell
pwsh -File scripts/publish.ps1
# Output: artifacts/app/WinDeploy.exe (GUI)
#         artifacts/cli/windeploy.exe (CLI)
```

Pushing a `v*` tag triggers `.github/workflows/release.yml`, which builds and attaches both EXEs to a GitHub Release automatically.

---

## CLI Reference

```
windeploy <command> [options]
```

| Command | Description |
|---|---|
| `list` | List all catalog software (optional `--category` filter) |
| `plan` | Show install / already-installed plan (no execution, dry-run) |
| `apply` | Execute installation (`--silent` unattended · `--locked` pin to lock.json versions · `--log <file>` write log) |
| `apply-config` | Apply configs (VS Code / Git / env… according to `applyWhen` policy) |
| `export` | Capture local configs back to the repository (auto-redacts tokens / keys) |
| `ssh-setup [--register]` | Generate a per-device ed25519 SSH key and apply SSH config (`--register` adds it to GitHub) |
| `sync` | `git pull` → apply configs + show install plan |
| `save [--message m] [--push]` | Commit config changes (`--push` to push to remote) |
| `doctor` | Environment health check: duplicate/broken PATH entries, invalid `*_HOME` vars |
| `validate` | Validate `catalog.json` (CI-friendly, exit code 1 on error) |
| `lock` | Snapshot installed versions to `catalog/lock.json` (reproducible across machines) |
| `export-dsc [--out f]` | Export as `winget configure` DSC YAML for Intune / GPO / SCCM |
| `inventory [--format csv\|json\|html] [--out f]` | Export installed software inventory |
| `download-only [--out d]` | Pre-download installer packages only (offline / USB deployment) |
| `migrate --export <dir> \| --import <dir>` | Export / restore migration kit (configs + software list) |

### Common Options

| Option | Description |
|---|---|
| `--profile <name>` | Select a preset (`dev` / `full` / `ai-station`) |
| `--select <id,…>` | Additional items to select (supports `@category:dev`) |
| `--deselect <id,…>` | Items to exclude |
| `--yes` / `-y` | Skip confirmation prompts |
| `--silent` | Silent install (passes `--silent` to winget) |
| `--locked` | Install versions pinned in `lock.json` |
| `--lang zh\|en\|de` | Override UI / output language |

---

## Software Catalog

`catalog/catalog.json` contains **126 software packages** across 15 categories. Items marked `★` are selected by default in the `dev` / `full` presets.

### Install Method Distribution

| Method | Description | Count |
|---|---|:---:|
| `winget` | Windows Package Manager (silent, automatic) | 94 |
| `manual` | Official website download (no automated source available) | 11 |
| `portable` | Download ZIP, auto-extract, add to PATH | 9 |
| `github-release` | Auto-select version from GitHub Releases | 6 |
| `winget-bundle` | Batch winget (multiple IDs in one pass) | 1 |
| `vscode-ext` | VS Code extension bulk install | 1 |
| `git` | `git clone` + add to PATH | 1 |
| `exe` | Download and run installer directly | 1 |
| `local` | Pre-staged local installer package | 1 |

### Full Software List

<details>
<summary><b>🛠 dev — Development Toolchain (23 items)</b></summary>

| ID | Name | Description | Method |
|---|---|---|---|
| ★ git | Git | Distributed version control | winget |
| ★ gh | GitHub CLI | GitHub command-line tool | winget |
| ★ nodejs | Node.js 24 | JavaScript runtime | winget |
| ★ python | Python 3.10 | Python interpreter | winget |
| ★ miniconda | Miniconda3 | Python environment & package manager | winget |
| pwsh | PowerShell 7 | Cross-platform PowerShell (syncs $PROFILE) | winget |
| ★ jdk17 | Oracle JDK 17 | Java Development Kit | winget |
| ★ go | Go | Go language toolchain | winget |
| ★ dotnet-sdk | .NET SDK 10 | .NET software development kit | winget |
| ★ cmake | CMake | Cross-platform build system | winget |
| ★ ffmpeg | FFmpeg | Audio/video processing | winget |
| ★ pandoc | Pandoc | Document format converter | winget |
| mingw | MinGW-w64 (WinLibs) | GCC/G++ toolchain (auto-select build) | portable |
| mingw-builds | MinGW-builds (niXman) | GCC/G++ toolchain niXman build (needs 7-Zip) | portable |
| flutter | Flutter + Dart | Cross-platform UI SDK (mobile) | git |
| docker-desktop | Docker Desktop | Container development environment | winget |
| rust | Rust | Rust toolchain (rustup) | winget |
| php | PHP | PHP interpreter (multi-version, sets PHP_HOME) | portable |
| lua | Lua | Lua interpreter (sets LUA_HOME) | winget |

> ★ = selected by default in the `dev` / `full` presets
</details>

<details>
<summary><b>⚙️ system — System Essentials (3 items)</b></summary>

| ID | Name | Description | Method |
|---|---|---|---|
| ★ vcredist | VC++ Runtime Pack | Visual C++ runtimes 2010–2015+ | winget-bundle |
| ★ windows-terminal | Windows Terminal | Modern terminal (syncs settings.json) | winget |
| huorong | Huorong Security | Antivirus / security (manual download) | manual |
</details>

<details>
<summary><b>💻 ide — Editors & IDEs (10 items)</b></summary>

| ID | Name | Description | Method |
|---|---|---|---|
| ★ vscode | VS Code | Lightweight code editor | winget |
| ★ vscode-ext | VS Code Extensions | Bulk-install ~80 extensions | vscode-ext |
| cursor | Cursor | AI code editor | winget |
| visual-studio | Visual Studio 2022 | Full C++/.NET IDE (Community Edition) | winget |
| vs2026 | Visual Studio 2026 | Full C++/.NET IDE (Preview) | winget |
| android-studio | Android Studio | Android development IDE | winget |
| arduino | Arduino IDE | Arduino development environment | winget |
| unity-hub | Unity Hub | Unity game engine launcher | winget |
| sublime-merge | Sublime Merge | Git GUI client | winget |
| sublime-text | Sublime Text | Text / code editor | winget |
</details>

<details>
<summary><b>🤖 ai — AI Tools (10 items)</b></summary>

| ID | Name | Description | Method |
|---|---|---|---|
| comfyui | ComfyUI Desktop | AI image generation workflow | winget |
| lmstudio | LM Studio | Local LLM runner (with GUI) | winget |
| ollama | Ollama | Local LLM runner | winget |
| llama-cpp | llama.cpp | C++ LLM inference engine (llama-cli / llama-server) | winget |
| claude | Claude | Anthropic Claude AI assistant desktop app | winget |
| windsurf | Windsurf | AI code editor | winget |
| hermes-agent | Hermes Agent | Nous Research AI agent desktop app | exe |
| codex | Codex CLI | OpenAI Codex command-line coding assistant | winget |
| codex-app | Codex (GUI) | OpenAI Codex GUI (manual download) | manual |
| ima-copilot | ima.copilot | Tencent ima AI workspace (manual download) | manual |
</details>

<details>
<summary><b>💬 office — Office & Messaging (14 items)</b></summary>

| ID | Name | Description | Method |
|---|---|---|---|
| wechat | WeChat | Instant messaging | winget |
| qq | QQ | Tencent QQ instant messaging | winget |
| tim | TIM | QQ lite (office) | winget |
| discord | Discord | Voice / text community platform | winget |
| telegram | Telegram | Instant messaging | winget |
| whatsapp | WhatsApp | Instant messaging | winget |
| zoom | Zoom | Video conferencing | winget |
| wecom | WeCom (Enterprise WeChat) | Enterprise communication | winget |
| feishu | Feishu (Lark) | Collaborative office suite | winget |
| tencent-meeting | Tencent Meeting | Video conferencing | winget |
| dingtalk | DingTalk | Alibaba enterprise collaboration | winget |
| wps | WPS Office | Kingsoft office suite (Writer / Spreadsheet / Presentation) | winget |
| foxmail | Foxmail | Email client | winget |
</details>

<details>
<summary><b>🎬 media — Media & Entertainment (13 items)</b></summary>

| ID | Name | Description | Method |
|---|---|---|---|
| obs | OBS Studio | Screen recording / streaming | winget |
| vlc | VLC | Multimedia player | winget |
| potplayer | PotPlayer | Multimedia player | winget |
| irfanview | IrfanView | Lightweight image viewer | winget |
| handbrake | HandBrake | Video transcoder | winget |
| netease-music | NetEase Cloud Music | Music player | winget |
| qq-music | QQ Music | Music player | winget |
| foobar2000 | foobar2000 | Lightweight audio player | winget |
| spotify | Spotify | Music streaming | winget |
| itunes | iTunes | Apple media manager | winget |
| bilibili | Bilibili | Bilibili desktop client | winget |
| douyin | Douyin (TikTok) | Short-video desktop app | winget |
</details>

<details>
<summary><b>🗄 db-api — Databases & API Tools (8 items)</b></summary>

| ID | Name | Description | Method |
|---|---|---|---|
| dbgate | DbGate | Universal database client | winget |
| dbeaver | DBeaver | Universal database client (with ER diagram) | winget |
| heidisql | HeidiSQL | MySQL / MariaDB client | winget |
| redis-insight | Redis Insight | Redis GUI client | winget |
| mongodb-compass | MongoDB Compass | MongoDB GUI client | winget |
| apifox | Apifox | API design / debug / mock | winget |
| postman | Postman | API debugging | winget |
| winscp | WinSCP | SFTP / SCP / FTP client | winget |
</details>

<details>
<summary><b>🌐 server — Server Components (3 items)</b></summary>

| ID | Name | Description | Method |
|---|---|---|---|
| nginx | nginx | High-performance web server / reverse proxy (portable) | portable |
| apache | Apache HTTP Server | Apache 2.4 web server (Apache Lounge Win64 build) | portable |
| tomcat | Apache Tomcat 10 | Java servlet container (portable, sets CATALINA_HOME) | portable |
</details>

<details>
<summary><b>🖥 vm — Virtualization (1 item)</b></summary>

| ID | Name | Description | Method |
|---|---|---|---|
| vmware | VMware Workstation | VM platform (auto-installs if local package found, else manual) | local |
</details>

<details>
<summary><b>🎮 games — Game Platforms (9 items)</b></summary>

| ID | Name | Description | Method |
|---|---|---|---|
| steam | Steam | Steam game platform client | winget |
| epic | Epic Games | Epic Games Store client | winget |
| ubisoft-connect | Ubisoft Connect | Ubisoft game platform | winget |
| ea-app | EA app | EA game platform | winget |
| battlenet | Battle.net | Blizzard game platform | winget |
| gog-galaxy | GOG Galaxy | GOG game platform | winget |
| watt-toolkit | Watt Toolkit | Steam++ network accelerator (auto-select version) | github-release |
| openspeedy | OpenSpeedy | Game / app speed modifier (auto-select version) | github-release |
| creaminstaller | CreamInstaller | Steam/Epic DLC unlocker (auto-select version) | github-release |
</details>

<details>
<summary><b>🌍 browser — Browsers (4 items)</b></summary>

| ID | Name | Description | Method |
|---|---|---|---|
| chrome | Google Chrome | Web browser | winget |
| firefox | Mozilla Firefox | Web browser | winget |
| edge | Microsoft Edge | Web browser | winget |
| qq-browser | QQ Browser | Web browser | winget |
</details>

<details>
<summary><b>🔀 proxy — Proxy Tools (2 items)</b></summary>

| ID | Name | Description | Method |
|---|---|---|---|
| v2rayn | v2rayN | Proxy client | winget |
| cc-switch | CC Switch | AI service local proxy / API routing (auto-select version) | github-release |
</details>

<details>
<summary><b>📖 dict — Dictionary (1 item)</b></summary>

| ID | Name | Description | Method |
|---|---|---|---|
| de-assist | German Assistant | German dictionary / learning tool | winget |
</details>

<details>
<summary><b>🔧 hwmon — Hardware Monitoring & Overclocking (10 items)</b></summary>

| ID | Name | Description | Method |
|---|---|---|---|
| hwinfo | HWiNFO | Hardware info / sensor monitoring | winget |
| cpu-z | CPU-Z | CPU / motherboard / RAM detailed info | winget |
| gpu-z | GPU-Z | GPU info / sensors | winget |
| crystaldiskinfo | CrystalDiskInfo | Disk S.M.A.R.T. health monitoring | winget |
| crystaldiskmark | CrystalDiskMark | Disk read/write benchmark | winget |
| furmark | FurMark 2 | GPU burn-in / stress test | winget |
| msi-afterburner | MSI Afterburner | GPU overclocking / fan / monitoring | winget |
| rtss | RivaTuner Statistics Server | Frame rate display / limiter / OSD | winget |
| ryzen-dram-calc | Ryzen DRAM Calculator | AMD Ryzen memory OC timing calculator (manual) | manual |
| ntpwedit | NTPWEdit | Windows local account password reset (auto-select version) | github-release |
</details>

<details>
<summary><b>🧰 tools — Utilities (22 items)</b></summary>

| ID | Name | Description | Method |
|---|---|---|---|
| snipaste | Snipaste | Screenshot / pin-to-screen tool | winget |
| screentogif | ScreenToGif | Screen recorder to GIF | winget |
| sharex | ShareX | Screenshot / screen recording / file sharing | winget |
| 7zip | 7-Zip | Archive / extraction | winget |
| winrar | WinRAR | Archive / extraction (RAR support) | winget |
| bandizip | Bandizip | Archive / extraction | winget |
| everything | Everything | Instant file-name search | winget |
| powertoys | PowerToys | Microsoft Windows utility pack (auto-select version) | github-release |
| dism-plus | Dism++ | System slimming / cleanup / maintenance (auto-select) | github-release |
| notepad-plus-plus | Notepad++ | Text / code editor | winget |
| typora | Typora | Markdown editor (manual download) | manual |
| utools | uTools | Productivity launcher (manual download) | manual |
| thunder | Xunlei (Thunder) | Download manager | winget |
| baidu-netdisk | Baidu Netdisk | Cloud storage | winget |
| aliyundrive | Aliyun Drive | Cloud storage | winget |
| anydesk | AnyDesk | Remote desktop | winget |
| todesk | ToDesk | Remote desktop | winget |
| teamviewer | TeamViewer | Remote desktop | winget |
| geek-uninstaller | Geek Uninstaller | Clean uninstall tool | winget |
| little-navmap | Little Navmap | Flight simulator navigation map (manual) | manual |
| m365-e5-renew | Microsoft365 E5 Renew Plus | M365 E5 developer subscription auto-renewal | manual |
</details>

### Built-in Profiles

| Profile | Includes | Typical Use Case |
|---|---|---|
| `dev` | dev + system + vscode + vscode-ext + windows-terminal | Lean developer workstation |
| `full` | All items (excludes steam / epic game clients) | Full-featured primary machine |
| `ai-station` | dev + system + ai + vscode + vscode-ext | AI inference / training workstation |

### Extending the Catalog

Just edit `catalog/catalog.json` — no engine changes required. Example entry:

```jsonc
{
  "id": "nodejs",
  "name": "Node.js 24",
  "category": "dev",
  "default": true,
  "install": { "method": "winget", "id": "OpenJS.NodeJS" },
  "detect": { "cmd": "node" },
  "config": {
    "source": "configs/nodejs",
    "files": [".npmrc"],
    "applyWhen": "ifInstalled"
  }
}
```

---

## Usage Notes

### Runtime & System Requirements

#### Minimum Requirements (GUI / CLI)

| Item | Requirement |
|---|---|
| OS | Windows 10 1809 (Build 17763) or later |
| Architecture | x64 only |
| Runtime dependency | **None** (self-contained publish, no .NET runtime installation needed) |
| Disk space | GUI EXE ≈ 80 MB · CLI EXE ≈ 20 MB |

#### Feature Dependencies

| Feature | Dependency / Condition |
|---|---|
| **winget installation** | Windows 10 21H1+ or [App Installer](https://aka.ms/getwinget) manually installed |
| **ConPTY terminal** | Windows 10 Build 17763+ (1809 Redstone 5; disabled automatically on older builds) |
| **WSL management** | WSL 2 enabled (`wsl --install`), requires Developer Mode |
| **SSH register to GitHub** | `gh` CLI signed in (`gh auth login`) |
| **conda environment** | Miniconda / Anaconda installed and on PATH |
| **vscode-ext install** | VS Code installed (`code` command available) |
| **SFC / DISM / chkdsk** | Administrator privileges (app prompts UAC on demand) |
| **NVMe SMART (internal)** | Administrator privileges (NVMe driver IOCTL) |
| **External drive SMART** | Bundled `smartctl.exe` (ships in `tools/`); admin may be needed |
| **FTP TLS auto-cert** | No extra dependency (X.509 generation is built in) |
| **DPAPI credential encryption** | Windows built-in; no extra dependency |

#### Build Requirements (developers only)

| Item | Requirement |
|---|---|
| SDK | [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) |
| IDE (optional) | Visual Studio 2022+ or VS Code + C# Dev Kit |
| Publish script | PowerShell 7 (`pwsh`) or Windows PowerShell 5.1 |

### Windows Blocked the App / "Unknown Publisher" Warning

This application is **not code-signed**. Windows SmartScreen / Defender may block it or warn "Unknown Publisher". **This is not malware** (full source code and CI pipeline are public).

| Situation | Resolution |
|---|---|
| SmartScreen blue dialog | Click "More info → Run anyway" |
| Prefer the ZIP package | Extract and run `WinDeploy.exe` from the folder — less likely to trigger heuristics than the single-file EXE |
| Defender quarantined the file | Go to "Windows Security → Virus & threat protection → Protection history" and restore the file; you can also submit a false-positive report at [Microsoft WDSI](https://www.microsoft.com/wdsi/filesubmission) |
| Permanent fix (developers) | Set repository secrets `SIGN_PFX_BASE64` + `SIGN_PFX_PASSWORD` — the Release CI already includes an Authenticode signing step |

### Developer Mode

WSL, System Tweaks, Advanced Tools, Cloudflare DDNS, and other advanced features are gated behind **Developer Mode**. Enable it in Settings → Developer Options — the left navigation panel updates instantly. A confirmation dialog is shown before activation; please read it carefully.

### Security Notes

- **SSH private keys**: generated fresh per device, never written to `configs/`
- **Config redaction**: tokens, passwords, `api_key`, `secret` fields auto-removed on export; keywords are customizable
- **Registry tweaks**: all changes on the Tweaks page can be reversed from the same page
- **UAC elevation**: requested only when needed (SFC / DISM / system-level PATH writes); not held persistently
- **FTPS**: trust-on-first-use certificate pinning; changed certs are refused
- **WSL**: distro names validated against an allowlist before use in any shell invocation
- **Server configs**: vhost paths reject shell-injection metacharacters

### Known Limitations

- The `mingw` catalog entry contains a placeholder URL — fill in the real download URL in `catalog.json` before use
- ConPTY terminal requires Windows 10 Build 17763+; the terminal page is disabled on older builds
- Some winget IDs are marked `TODO:` — run `validate` before applying

---

## License

This project is released to the public under the **CC BY-NC-SA 4.0** (Creative Commons Attribution-NonCommercial-ShareAlike 4.0 International) license. Full terms: [`LICENSE`](LICENSE).

### License Summary

| Right | Description |
|---|---|
| **Attribution (BY)** | You must credit the author and indicate whether changes were made |
| **NonCommercial (NC)** | You may not use this project or derivatives for commercial purposes |
| **ShareAlike (SA)** | Modified / derivative works must be released under the same license |

### Permitted Uses

- Personal learning, research, and non-commercial projects
- Copying, modifying, and redistributing under the three conditions above

### Prohibited Uses

- Using this project or any derivative in commercial products, services, or for profit
- Publishing a modified version as closed-source
- Removing or altering copyright notices

### Author's Reserved Rights

**Tommy131** is the sole copyright holder and is **not bound by the NonCommercial restriction**. The author retains all rights, including commercial use and the right to issue the software under different terms (dual-licensing).

**Commercial licensing inquiries**: hanskijay@owoblog.com

---

Copyright © 2026 Tommy131 · <https://github.com/Tommy131>
