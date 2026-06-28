# OwO! Win Deployer (owo-win-deployer)

> [!CAUTION]
> # ⛔ 请不要 Fork 本项目！⛔
>
> **当前处于不稳定版本更新阶段，可能会重写 Git 历史提交记录、增删文件。**
>
> - 🚫 **不要提交 Pull Request** —— 本项目**不会处理任何不受信任的 Pull Request**。
> - ✅ 使用过程中遇到问题，请**直接提交 [Issue](../../issues)**。
> - ⚠️ Fork 后随时可能因历史重写而与上游彻底冲突，请勿 Fork。

---

一键在任意 Windows 设备上**复刻开发环境、应用与个人配置**，并集成系统管理、终端、FTP、进程/服务管理、Cloudflare DDNS 等专业工具。支持中文 / English / Deutsch 运行时切换。完整设计文档见 [`docs/DESIGN.md`](docs/DESIGN.md)。

> **当前版本：v1.2.5.2** &nbsp;|&nbsp; 🌐 [English README](README.md)

## 目录

- [界面预览](#界面预览-screenshots)
- [功能概览](#功能概览)
  - [多语言支持](#多语言支持v120)
  - [Cloudflare DDNS](#cloudflare-ddns-管理开发人员模式)
  - [安全加固](#安全加固v121)
- [项目架构](#项目架构)
- [快速开始](#快速开始)
- [构建与开发](#构建与开发需要-net-sdk-10)
- [CLI 命令参考](#cli-命令参考)
- [软件目录](#软件目录-catalog)
- [使用须知](#使用须知)
- [版权许可](#版权许可-license)

---

## 界面预览 Screenshots

| 软件安装中心（卡片勾选 · 右键菜单） | 运行进度（实时下载 / 安装日志） |
| :---: | :---: |
| ![软件安装中心](assets/images/install-center.png) | ![运行进度](assets/images/progress.png) |
| **进程管理（按软件分组）** | **进程管理（展开进程树 · CPU / 内存）** |
| ![进程管理](assets/images/process-manager.png) | ![进程管理展开](assets/images/process-manager-expanded.png) |
| **启动项管理** | **配置导出（自动脱敏）** |
| ![启动项](assets/images/startup-items.png) | ![配置导出](assets/images/export.png) |
| **设置 · 主题 / 路径变量 / 仓库** | **关于 / 开发者** |
| ![设置](assets/images/settings.png) | ![关于开发者](assets/images/about.png) |
| **运行日志** | |
| ![运行日志](assets/images/logs.png) | |

---

## 功能概览

所有里程碑均已完成：

| 里程碑 | 核心内容 | 状态 |
|:---:|---|:---:|
| M1 | 安装引擎（winget / portable / git / conda / vscode-ext / script）+ 幂等检测 + 路径变量 + CLI | ✅ |
| M2 | 配置套用 / 采集 + 导出脱敏 + 环境变量管理 + SSH 每台独立生成 | ✅ |
| M3 | WPF 软件安装中心（图标卡片逐项勾选）+ 实时进度 + 配置 / 导出页面 | ✅ |
| M4 | 自包含单文件发布 + Release CI + 多机同步（sync / save）+ 版本锁定 | ✅ |
| M5 | 系统管理与专业工具（终端 · FTP · 进程 · 服务 · 概览 · 维护 · WSL · 调优）+ 开发人员模式 | ✅ |

### 软件安装中心（M1 · M3）

- **126 个软件**（15 类别）以图标卡片展示，逐项勾选或整组选择
- **搜索**过滤 + **预设 Profile** 一键切换（`dev` / `full` / `ai-station`）
- **安装方式**：winget · winget-bundle · portable · git · conda · vscode-ext · exe · script · github-release · local · manual
- **幂等安装**：检测已装则自动跳过，支持 dry-run 预演
- 右键菜单：安装 / 卸载 / 启动 / 停止 / 重启 / 更新 / 查看日志
- 实时进度：下载速度、ETA、单项状态、滚动日志尾

### 配置同步（M2）

- **套用 / 采集**双向：VS Code · Git · SSH · 环境变量 · Windows Terminal · PowerShell · npm · conda · pip
- 导出自动**脱敏**（token / 密钥 / API key），自定义脱敏关键词
- SSH 密钥**每台设备独立生成**（ed25519），支持一键登记到 GitHub，私钥永不入库
- 套用前自动备份（`.bak.yyyyMMdd-HHmmss`）

### 多机同步（M4）

- `catalog/lock.json` 锁定版本，跨机可复现
- `git pull` → 套用配置 + 显示安装计划（`sync` 命令）
- 主机名 → 预设映射（`catalog/hosts.json`），多机自动识别角色
- 迁移工具包：一键打包 configs + 软件清单，迁移到新机器

### 终端（M5）

- **ConPTY 伪控制台**：完整支持 PowerShell / cmd / ssh / vim 等交互式程序与密码输入
- **VT100/ANSI 模拟器**：16 色 / 256 色 / 24 位真彩色，滚动缓冲 5000 行
- **多会话**：标签式多终端，内置 Shell 目录（可配置 PowerShell / cmd / WSL 等），会话可命名编辑
- **视觉特效**：Hacker-FX（绿磷光 · 辉光 · 扫描线）+ CodeRain 数字雨，可在设置中独立切换并持久化
- Shell 切换、调整终端尺寸（自动通知 PTY resize）；关闭前确认弹窗防止误关

### FTP 服务器 & 客户端（M5）

- **自托管 FTP/FTPS 服务器**：自定义端口、主被动模式、TLS 加密、并发数限制
- **用户 / 权限组管理**：精细化读写权限，MLSD 权限位预告知客户端（自动禁用无权限操作）
- **自动签发 TLS 证书**（自签名 X.509 RSA 2048-bit，无需手动 openssl）
- **FTP 客户端**：双栏文件浏览，本地 / 远程双侧右键菜单（打开 / 上传 / 下载 / 重命名 / 删除）
- **多选批量传输**：Ctrl/Shift 多选文件夹与文件，批量上传 / 下载 / 删除；资源管理器风格右键保持多选
- **实时传输速度 + ETA**：预扫描总大小，每 0.5 秒采样刷新进度
- **站点管理器（Saved Logins）**：保存常用登录信息，密码经 DPAPI 加密存储，快速重连
- **FTPS 证书信任锁定（TOFU）**：首次连接记录证书 SHA-256 指纹（`ftp_trust.json`），后续证书变更（MITM 迹象）自动拒绝；自签名证书照常工作
- 15 秒连接 / TLS 超时保护；配置持久化（端口 / TLS / 编码 / 限速等）

### 进程管理（M5）

- **按软件分组树状视图**：CPU / 内存实时显示
- 支持过滤、批量操作（启动 / 杀进程 / 重启）
- 从运行中进程提取图标，分组归类

### 系统概览（M5）

- CPU · 内存 · 磁盘 · 电池 · Windows 激活状态一览
- **SMART 全面增强**：内置捆绑 `smartctl`（随安装包附带），支持内部 NVMe / SATA 及外部 USB 硬盘健康读取；自动识别 ASMedia / JMicron / Realtek USB 桥接芯片；NVMe 健康日志 + ATA 属性详细表；SMART 权限不足时提示 Admin 重启
- 磁盘类型徽章识别：NVMe / SSD / HDD / USB
- **USB 设备安全弹出**：每个可移动磁盘卡片附弹出按钮；弹出被系统拒绝时提供强制弹出（先卸载卷再重试），成功后立即从页面移除该卡片
- 一键导出本机软件清单（CSV / HTML / JSON）

### 系统维护（M5）

- **修复工具**：一键 SFC · DISM /RestoreHealth · chkdsk（按需 UAC 提权）
- **网络重置**：重置 TCP/IP 栈 · Winsock · DNS 缓存
- **缓存清理**：Temp · Windows Update 缓存 · 缩略图 · Windows.old
- **图标缓存重建**
- **事件速诊**：最近 7 天严重 / 错误事件摘要

### WSL（M5 · 开发人员模式）

- 发行版列表 · 在线安装 · 设为默认 · 启动 / 停止
- 导出备份（TAR）· 注销

### 系统调优（M5 · 开发人员模式）

- 可逆注册表调整，实时读取当前状态，一键开关：
  - 显示文件扩展名 / 显示隐藏文件
  - 深色 / 浅色模式
  - 经典右键菜单（Win11）
  - 关闭遥测 · 其他常用 Explorer 选项

### 服务管理（M5）

- Windows 服务启动类型配置（自动 / 手动 / 禁用）
- 启动 · 停止 · 重启 · 状态实时监控

### 启动项管理（M5）

- 读写注册表 `Run` / `RunOnce`（HKCU + HKLM）
- 启用 / 禁用，审计记录

### 高级工具（M5 · 开发人员模式）

- 环境体检（PATH 重复 / 失效 · `*_HOME` 变量验证）
- catalog 校验（CI 友好，异常退出码 1）
- 生成 `lock.json`（版本快照）
- 导出 `winget configure` DSC YAML（供 Intune / GPO / SCCM）
- 离线部署包（预下载 + 打包）
- 迁移工具包导出 / 还原

### 系统托盘（M5）

- 最小化到托盘后台运行，不占任务栏
- 托盘右键快捷菜单：一键启动 / 停止 / 重启 FTP 服务器，显示缓存的服务运行状态（异步探测）
- 快速导航到主窗口各功能页

### 应用稳定性

- **全局崩溃处理器**：捕获 UI 线程、AppDomain 及 Task 中的未处理异常，写入 `crash.log` 并弹出错误对话框，确保程序不无故退出到桌面

### 设置与外观

- 浅色 / 深色主题，实时切换
- 路径变量配置（`${DevRoot}` / `${ToolsDir}`）
- 配置仓库地址、镜像源
- 脱敏关键词自定义
- **语言切换**：在设置页即时切换界面语言（中文 / English / Deutsch），无需重启
- **开发人员模式**门控（解锁 WSL · 调优 · 高级工具 · Cloudflare DDNS · 终端高级功能）

### 多语言支持（v1.2.0+）

- **运行时切换**：中文 / English / Deutsch，无需重启，所有界面文本立即生效
- **首次启动跟随系统语言**（简体中文 / 德语 → 自动选中，其余默认英文）
- **CLI 支持**：`--lang zh/en/de` 参数 或 `WINDEPLOY_LANG` 环境变量
- 软件目录摘要（`catalog/i18n/{en,de}.json`）已提供英/德翻译；1450 个 UI 键 × 3 语言
- 确认弹窗按钮随语言变化（不再固定显示 Windows 系统语言的 OK/Cancel）
- `scripts/check-i18n.ps1` 校验三语键集与占位符完全一致

### Cloudflare DDNS 管理（开发人员模式）

- 使用 Cloudflare API Token 管理 DNS 记录（A / AAAA / CNAME / MX / TXT 等）
- **自动 DDNS**：定时探测本机公网 IP，IP 变化时自动更新指定 A 记录，适合家庭宽带动态 IP 场景
- 支持多域 / 多记录，支持代理模式（橙色云）开关
- 配置（Token · Zone · 记录）本地加密持久化；托盘菜单显示 DDNS 状态

### 安全加固（v1.2.1）

- **FTPS 信任锁定（TOFU）**：首次连接记录证书指纹，后续指纹不匹配则拒绝并报警
- **WSL 命令注入防护**：所有 WSL 操作通过 `ArgumentList` 直接调用 `wsl.exe`（不经 cmd shell），发行版名称在严格白名单内验证
- **Web 服务器配置注入防护**：nginx / Apache 虚拟主机 `server_name` / 根目录 / 证书路径禁止包含 `;{}#"` 及换行符，防止指令注入
- **EXE 安装 SHA-256 校验**：catalog 提供 sha256 时验证安装包完整性，与 portable 安装器行为一致
- **事件内存泄漏修复**：瞬态 ViewModel（软件详情、服务详情）在导航离开时正确 Dispose，消除对静态 `CultureChanged` 事件的订阅泄漏

---

## 项目架构

```
owo-win-deployer/
├── src/
│   ├── WinDeploy.Core/          # 纯库：安装引擎、配置同步、数据模型
│   │   ├── Catalog/             # JSON 解析、Profile 解析
│   │   ├── Engine/              # 安装编排、检测、方法派发
│   │   ├── Config/              # 配置套用 / 采集 / 脱敏
│   │   ├── Export/              # DSC 导出、软件清单、迁移包
│   │   ├── Models/              # 数据模型
│   │   └── Util/                # 日志、进程、路径工具
│   ├── WinDeploy.Cli/           # CLI 入口（薄包装，转发到 Core）
│   └── WinDeploy.App/           # WPF GUI（自包含单文件）
│       ├── Views/               # 20+ 页面 + 对话框
│       ├── ViewModels/          # 对应 ViewModel（MVVM）
│       ├── Services/            # 40+ 系统集成服务
│       └── Behaviors/           # 自定义 UI 行为
├── catalog/
│   ├── catalog.json             # 软件主清单（450+ 条目）
│   ├── profiles/                # 安装预设（dev · full · ai-station）
│   └── hosts.example.json       # 主机名→预设映射模板
├── configs/                     # 配置仓库（与安装状态解耦）
│   ├── vscode/                  # settings.json · keybindings.json
│   ├── git/                     # .gitconfig
│   ├── ssh/                     # config · known_hosts（无私钥）
│   ├── env/                     # env.json（自定义 PATH / 环境变量）
│   ├── pwsh/                    # PowerShell Profile
│   ├── windows-terminal/        # settings.json
│   ├── nodejs/                  # .npmrc
│   ├── python/                  # pip.ini
│   └── miniconda/               # .condarc
├── bootstrap/
│   └── bootstrap.ps1            # 裸机引导脚本
├── scripts/
│   └── publish.ps1              # 构建发布（自包含单 EXE）
├── docs/
│   └── DESIGN.md                # 完整设计文档（权威来源）
├── assets/                      # 图片 · 图标资源
├── .github/
│   └── workflows/release.yml   # Release CI（打 tag 自动构建）
└── WinDeploy.sln
```

### 分层说明

| 层 | 项目 | 说明 |
|---|---|---|
| 引擎层 | `WinDeploy.Core` | 纯 .NET 10 库，**不依赖注册表 / WMI**（CLI 和 GUI 均可调用） |
| CLI 层 | `WinDeploy.Cli` | 薄包装，暴露 13 条命令，适合脚本 / CI 场景 |
| GUI 层 | `WinDeploy.App` | WPF MVVM，系统集成（WMI · ConPTY · P/Invoke）仅在此层 |

### 关键设计原则

- **数据 / 引擎分离**：`catalog.json` 只是数据，加软件只改 JSON 不动引擎
- **幂等安装**：先检测再安装，重复运行安全
- **路径变量化**：`${DevRoot}` / `${ToolsDir}` 首次设定，跨机通用
- **安全优先**：SSH 私钥每台独立生成永不入库；导出 token / 密钥自动脱敏
- **开发人员模式门控**：注册表 / WSL / 高级功能通过确认弹窗二次验证

---

## 快速开始

### 裸机引导（目标机无需预装 .NET）

```powershell
irm https://raw.githubusercontent.com/Tommy131/owo-win-deployer/main/bootstrap/bootstrap.ps1 | iex
```

脚本会自动确认 winget 可用、从 GitHub Release 下载最新版 `WinDeploy.exe` 并启动。

### 直接下载

前往 [Releases](../../releases) 页面，下载 `WinDeploy.exe`（GUI）或 `windeploy.exe`（CLI）。

> **推荐下载 ZIP 版**：ZIP 内为文件夹版（exe + 运行库），触发杀软启发式的概率低于单文件自解压版。

---

## 构建与开发（需要 .NET SDK 10）

```powershell
# 构建整个解决方案
dotnet build WinDeploy.sln

# 运行 GUI（软件安装中心）
dotnet run --project src/WinDeploy.App

# 运行 CLI
dotnet run --project src/WinDeploy.Cli -- list
dotnet run --project src/WinDeploy.Cli -- plan  --profile dev
dotnet run --project src/WinDeploy.Cli -- apply --profile dev --yes
```

### 发布（自包含单文件，目标机免装 .NET）

```powershell
pwsh -File scripts/publish.ps1
# 产出：artifacts/app/WinDeploy.exe（GUI）
#       artifacts/cli/windeploy.exe（CLI）
```

推送 `v*` tag 后，`.github/workflows/release.yml` 自动构建并挂到 GitHub Release。

---

## CLI 命令参考

```
windeploy <命令> [选项]
```

| 命令 | 说明 |
|---|---|
| `list` | 列出 catalog 全部软件（可加 `--category` 筛选） |
| `plan` | 显示安装 / 已装计划（不执行，dry-run） |
| `apply` | 执行安装（`--silent` 无人值守 · `--locked` 按 lock.json 钉版本 · `--log <文件>` 落日志） |
| `apply-config` | 套用配置（VS Code / Git / env… 按 `applyWhen` 策略） |
| `export` | 采集本机配置回写仓库（自动脱敏 token / 密钥） |
| `ssh-setup [--register]` | 生成本机 ed25519 SSH 密钥并套用 ssh 配置（`--register` 自动登记到 GitHub） |
| `sync` | `git pull` → 套用配置 + 显示安装计划 |
| `save [--message m] [--push]` | 提交 configs 改动（`--push` 推送远程） |
| `doctor` | 环境体检：PATH 重复 / 失效、`*_HOME` 失效、已装但不在 PATH |
| `validate` | 校验 catalog.json（CI 友好，有错误时退出码 1） |
| `lock` | 采集已装版本写入 `catalog/lock.json`（跨机可复现） |
| `export-dsc [--out f]` | 导出为 `winget configure` DSC YAML，供 Intune / GPO / SCCM 无人值守 |
| `inventory [--format csv\|json\|html] [--out f]` | 导出本机已装软件清单 |
| `download-only [--out d]` | 仅预下载所选软件安装包（离线 / U 盘部署） |
| `migrate --export <目录> \| --import <目录>` | 迁移工具包导出 / 还原（configs + 软件清单） |

### 常用选项

| 选项 | 说明 |
|---|---|
| `--profile <名称>` | 指定预设（`dev` / `full` / `ai-station`） |
| `--select <id,…>` | 追加选择软件（支持 `@category:dev`） |
| `--deselect <id,…>` | 排除软件 |
| `--yes` / `-y` | 跳过确认提示 |
| `--silent` | 静默安装（传 `--silent` 给 winget） |
| `--locked` | 按 `lock.json` 中的固定版本安装 |

---

## 软件目录 Catalog

`catalog/catalog.json` 包含 **126 个软件条目**，覆盖 15 个类别。标注 `★` 的条目在 `dev` / `full` 预设中默认选中。

### 安装方式分布

| 方式 | 说明 | 数量 |
|---|---|:---:|
| `winget` | Windows 包管理器（自动静默） | 94 |
| `manual` | 官网手动下载（无自动安装源） | 11 |
| `portable` | 下载 ZIP 自动解压 + 加入 PATH | 9 |
| `github-release` | 从 GitHub Release 自动选版下载 | 6 |
| `winget-bundle` | 批量 winget（多个 ID） | 1 |
| `vscode-ext` | VS Code 扩展批量安装 | 1 |
| `git` | git clone + 加入 PATH | 1 |
| `exe` | 直接下载安装包运行 | 1 |
| `local` | 预置本地安装包 | 1 |

### 完整软件列表

<details>
<summary><b>🛠 dev — 开发工具链（23 项）</b></summary>

| ID | 名称 | 说明 | 方式 |
|---|---|---|---|
| ★ git | Git | 分布式版本控制 | winget |
| ★ gh | GitHub CLI | GitHub 命令行工具 | winget |
| ★ nodejs | Node.js 24 | JavaScript 运行时 | winget |
| ★ python | Python 3.10 | Python 解释器 | winget |
| ★ miniconda | Miniconda3 | Python 环境与包管理 | winget |
| pwsh | PowerShell 7 | 跨平台 PowerShell（同步 $PROFILE 配置） | winget |
| ★ jdk17 | Oracle JDK 17 | Java 开发套件 | winget |
| ★ go | Go | Go 语言工具链 | winget |
| ★ dotnet-sdk | .NET SDK 10 | .NET 软件开发工具包 | winget |
| ★ cmake | CMake | 跨平台构建系统 | winget |
| ★ ffmpeg | FFmpeg | 音视频处理工具 | winget |
| ★ pandoc | Pandoc | 文档格式转换 | winget |
| mingw | MinGW-w64 (WinLibs) | GCC/G++ 工具链（自动选版下载） | portable |
| mingw-builds | MinGW-builds (niXman) | GCC/G++ 工具链 niXman 构建（需 7-Zip） | portable |
| flutter | Flutter + Dart | 跨平台 UI SDK（移动开发） | git |
| docker-desktop | Docker Desktop | 容器开发环境 | winget |
| rust | Rust | Rust 工具链 (rustup) | winget |
| php | PHP | PHP 解释器（多版本，自动设置 PHP_HOME） | portable |
| lua | Lua | Lua 解释器（自动设置 LUA_HOME） | winget |

> ★ = dev / full 预设默认选中
</details>

<details>
<summary><b>⚙️ system — 系统基础（3 项）</b></summary>

| ID | 名称 | 说明 | 方式 |
|---|---|---|---|
| ★ vcredist | VC++ 运行库合集 | Visual C++ 运行库 2010–2015+ | winget-bundle |
| ★ windows-terminal | Windows Terminal | 现代终端（同步 settings.json） | winget |
| huorong | 火绒安全 | 安全 / 杀毒软件（官网手动下载） | manual |
</details>

<details>
<summary><b>💻 ide — 编辑器 & IDE（10 项）</b></summary>

| ID | 名称 | 说明 | 方式 |
|---|---|---|---|
| ★ vscode | VS Code | 轻量代码编辑器 | winget |
| ★ vscode-ext | VS Code 扩展 | 批量安装约 80 个扩展 | vscode-ext |
| cursor | Cursor | AI 代码编辑器 | winget |
| visual-studio | Visual Studio 2022 | 重型 C++/.NET IDE（社区版） | winget |
| vs2026 | Visual Studio 2026 | 重型 C++/.NET IDE（预览版） | winget |
| android-studio | Android Studio | Android 开发 IDE | winget |
| arduino | Arduino IDE | Arduino 开发环境 | winget |
| unity-hub | Unity Hub | Unity 游戏引擎管理器 | winget |
| sublime-merge | Sublime Merge | Git 图形客户端 | winget |
| sublime-text | Sublime Text | 文本 / 代码编辑器 | winget |
</details>

<details>
<summary><b>🤖 ai — AI 工具（10 项）</b></summary>

| ID | 名称 | 说明 | 方式 |
|---|---|---|---|
| comfyui | ComfyUI Desktop | AI 绘画工作流 | winget |
| lmstudio | LM Studio | 本地大模型运行（含 GUI） | winget |
| ollama | Ollama | 本地大模型运行 | winget |
| llama-cpp | llama.cpp | C++ 大模型推理引擎（llama-cli / llama-server） | winget |
| claude | Claude | Anthropic Claude AI 助手桌面端 | winget |
| windsurf | Windsurf | AI 代码编辑器 | winget |
| hermes-agent | Hermes Agent | Nous Research AI 智能体桌面端 | exe |
| codex | Codex CLI | OpenAI Codex 命令行编码助手 | winget |
| codex-app | Codex (GUI) | OpenAI Codex 图形界面（官网手动下载） | manual |
| ima-copilot | ima.copilot | 腾讯 ima 智能工作台（官网手动下载） | manual |
</details>

<details>
<summary><b>💬 office — 办公 & 通讯（14 项）</b></summary>

| ID | 名称 | 说明 | 方式 |
|---|---|---|---|
| wechat | 微信 | 即时通讯 | winget |
| qq | QQ | 腾讯 QQ 即时通讯 | winget |
| tim | TIM | QQ 办公简洁版 | winget |
| discord | Discord | 语音 / 文字社群 | winget |
| telegram | Telegram | 即时通讯 | winget |
| whatsapp | WhatsApp | 即时通讯 | winget |
| zoom | Zoom | 视频会议 | winget |
| wecom | 企业微信 | 企业通讯协作 | winget |
| feishu | 飞书 | 协作办公（字节跳动） | winget |
| tencent-meeting | 腾讯会议 | 视频会议 | winget |
| dingtalk | 钉钉 | 阿里钉钉 办公协作 | winget |
| wps | WPS Office | 金山办公套件（文字 / 表格 / 演示） | winget |
| foxmail | Foxmail | 邮件客户端 | winget |
</details>

<details>
<summary><b>🎬 media — 媒体 & 娱乐（13 项）</b></summary>

| ID | 名称 | 说明 | 方式 |
|---|---|---|---|
| obs | OBS Studio | 录屏 / 直播 | winget |
| vlc | VLC | 多媒体播放器 | winget |
| potplayer | PotPlayer | 多媒体播放器 | winget |
| irfanview | IrfanView | 轻量看图工具 | winget |
| handbrake | HandBrake | 视频转码 | winget |
| netease-music | 网易云音乐 | 音乐播放 | winget |
| qq-music | QQ音乐 | 音乐播放 | winget |
| foobar2000 | foobar2000 | 轻量音频播放器 | winget |
| spotify | Spotify | 流媒体音乐 | winget |
| itunes | iTunes | 苹果媒体管理 | winget |
| bilibili | 哔哩哔哩 | B 站桌面端 | winget |
| douyin | 抖音 | 短视频桌面版 | winget |
</details>

<details>
<summary><b>🗄 db-api — 数据库 & API（8 项）</b></summary>

| ID | 名称 | 说明 | 方式 |
|---|---|---|---|
| dbgate | DbGate | 通用数据库客户端 | winget |
| dbeaver | DBeaver | 通用数据库客户端（含 ER 图） | winget |
| heidisql | HeidiSQL | MySQL / MariaDB 客户端 | winget |
| redis-insight | Redis Insight | Redis 可视化客户端 | winget |
| mongodb-compass | MongoDB Compass | MongoDB 图形客户端 | winget |
| apifox | Apifox | API 设计 / 调试 / Mock | winget |
| postman | Postman | API 调试 | winget |
| winscp | WinSCP | SFTP / SCP / FTP 客户端 | winget |
</details>

<details>
<summary><b>🌐 server — 服务器组件（3 项）</b></summary>

| ID | 名称 | 说明 | 方式 |
|---|---|---|---|
| nginx | nginx | 高性能 Web 服务器 / 反向代理（便携版） | portable |
| apache | Apache HTTP Server | Apache 2.4 Web 服务器（Apache Lounge Win64 构建） | portable |
| tomcat | Apache Tomcat 10 | Java Servlet 容器（便携版，自动设置 CATALINA_HOME） | portable |
</details>

<details>
<summary><b>🖥 vm — 虚拟化（1 项）</b></summary>

| ID | 名称 | 说明 | 方式 |
|---|---|---|---|
| vmware | VMware Workstation | 虚拟机（有本地安装包则自动安装，否则手动） | local |
</details>

<details>
<summary><b>🎮 games — 游戏平台（9 项）</b></summary>

| ID | 名称 | 说明 | 方式 |
|---|---|---|---|
| steam | Steam | Steam 游戏平台客户端 | winget |
| epic | Epic Games | Epic 游戏平台客户端 | winget |
| ubisoft-connect | Ubisoft Connect | 育碧游戏平台 | winget |
| ea-app | EA app | EA 游戏平台 | winget |
| battlenet | Battle.net | 暴雪游戏平台 | winget |
| gog-galaxy | GOG Galaxy | GOG 游戏平台 | winget |
| watt-toolkit | Watt Toolkit | Steam++ 游戏 / 网络加速（自动选版下载） | github-release |
| openspeedy | OpenSpeedy | 游戏 / 程序变速工具（自动选版下载） | github-release |
| creaminstaller | CreamInstaller | Steam/Epic DLC 解锁工具（自动选版下载） | github-release |
</details>

<details>
<summary><b>🌍 browser — 浏览器（4 项）</b></summary>

| ID | 名称 | 说明 | 方式 |
|---|---|---|---|
| chrome | Google Chrome | 网页浏览器 | winget |
| firefox | Mozilla Firefox | 网页浏览器 | winget |
| edge | Microsoft Edge | 网页浏览器 | winget |
| qq-browser | QQ浏览器 | 网页浏览器 | winget |
</details>

<details>
<summary><b>🔀 proxy — 代理工具（2 项）</b></summary>

| ID | 名称 | 说明 | 方式 |
|---|---|---|---|
| v2rayn | v2rayN | 代理 / 科学上网客户端 | winget |
| cc-switch | CC Switch | AI 服务本地代理 / API 渠道切换（自动选版下载） | github-release |
</details>

<details>
<summary><b>📖 dict — 词典（1 项）</b></summary>

| ID | 名称 | 说明 | 方式 |
|---|---|---|---|
| de-assist | 德语助手 | 德语词典 / 学习工具 | winget |
</details>

<details>
<summary><b>🔧 hwmon — 硬件监控 & 超频（10 项）</b></summary>

| ID | 名称 | 说明 | 方式 |
|---|---|---|---|
| hwinfo | HWiNFO | 硬件信息 / 传感器监控 | winget |
| cpu-z | CPU-Z | CPU / 主板 / 内存详细信息 | winget |
| gpu-z | GPU-Z | 显卡信息 / 传感器 | winget |
| crystaldiskinfo | CrystalDiskInfo | 硬盘 S.M.A.R.T 健康监测 | winget |
| crystaldiskmark | CrystalDiskMark | 磁盘读写跑分 | winget |
| furmark | FurMark 2 | GPU 烤机 / 压力测试 | winget |
| msi-afterburner | MSI Afterburner | 显卡超频 / 风扇 / 监控 | winget |
| rtss | RivaTuner Statistics Server | 帧率显示 / 帧数限制 / 屏幕叠加 | winget |
| ryzen-dram-calc | Ryzen DRAM Calculator | 锐龙内存超频时序计算器（官网手动下载） | manual |
| ntpwedit | NTPWEdit | Windows 本地账户密码重置（自动选版下载） | github-release |
</details>

<details>
<summary><b>🧰 tools — 实用工具（22 项）</b></summary>

| ID | 名称 | 说明 | 方式 |
|---|---|---|---|
| snipaste | Snipaste | 截图 / 贴图工具 | winget |
| screentogif | ScreenToGif | 录屏转 GIF | winget |
| sharex | ShareX | 截图 / 录屏 / 文件分享 | winget |
| 7zip | 7-Zip | 压缩 / 解压工具 | winget |
| winrar | WinRAR | 压缩 / 解压（含 RAR 格式） | winget |
| bandizip | Bandizip | 压缩 / 解压工具 | winget |
| everything | Everything | 极速文件名搜索 | winget |
| powertoys | PowerToys | 微软 Windows 实用工具集（自动选版） | github-release |
| dism-plus | Dism++ | 系统精简 / 清理 / 维护（自动选版） | github-release |
| notepad-plus-plus | Notepad++ | 文本 / 代码编辑器 | winget |
| typora | Typora | Markdown 编辑器（官网手动下载） | manual |
| utools | uTools | 效率工具箱 / 启动器（官网手动下载） | manual |
| thunder | 迅雷 | 下载工具 | winget |
| baidu-netdisk | 百度网盘 | 云存储 | winget |
| aliyundrive | 阿里云盘 | 云存储 | winget |
| anydesk | AnyDesk | 远程桌面 | winget |
| todesk | ToDesk | 远程桌面 | winget |
| teamviewer | TeamViewer | 远程桌面 | winget |
| geek-uninstaller | Geek Uninstaller | 彻底卸载工具 | winget |
| little-navmap | Little Navmap | 飞行模拟导航地图（官网手动下载） | manual |
| m365-e5-renew | Microsoft365 E5 Renew Plus | M365 E5 开发者订阅自动续订 | manual |
</details>

### 内置预设 Profile

| 预设 | 包含 | 典型场景 |
|---|---|---|
| `dev` | dev + system + vscode + vscode-ext + windows-terminal | 精简开发工作站 |
| `full` | 全部条目（排除 steam / epic 等游戏客户端） | 全功能主力机器 |
| `ai-station` | dev + system + ai + vscode + vscode-ext | AI 推理 / 训练工作站 |

### 扩展 catalog

只需编辑 `catalog/catalog.json`，引擎无需修改。每条目示例：

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

## 使用须知

### 运行环境与系统要求

#### 最低要求（运行 GUI / CLI）

| 项目 | 要求 |
|---|---|
| 操作系统 | Windows 10 1809（Build 17763）及以上 |
| 架构 | x64（仅 64 位） |
| 运行时依赖 | **无**（自包含发布，免装 .NET 运行时） |
| 磁盘空间 | GUI EXE ≈ 80 MB；CLI EXE ≈ 20 MB |

#### 功能依赖

| 功能 | 依赖 / 条件 |
|---|---|
| **winget 安装** | Windows 10 21H1+ 或手动安装 [App Installer](https://aka.ms/getwinget) |
| **ConPTY 终端** | Windows 10 Build 17763+（1809 Redstone 5，自动在旧版上禁用此页） |
| **WSL 管理** | 已启用 WSL 2（`wsl --install`），需开发人员模式 |
| **SSH 登记到 GitHub** | 已登录 `gh` CLI（`gh auth login`） |
| **conda 环境** | Miniconda / Anaconda 已安装并在 PATH 中 |
| **vscode-ext 安装** | VS Code 已安装（`code` 命令可用） |
| **SFC / DISM / chkdsk** | 管理员权限（程序按需弹出 UAC） |
| **SMART 详情（NVMe）** | 管理员权限（调用 NVMe 驱动 IOCTL） |
| **FTP TLS 自动证书** | 无额外依赖（程序内置 X.509 生成） |
| **DPAPI 凭据加密** | Windows 内置，无需额外依赖 |

#### 构建要求（仅开发者）

| 项目 | 要求 |
|---|---|
| SDK | [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) |
| IDE（可选） | Visual Studio 2022+ 或 VS Code + C# Dev Kit |
| 发布脚本 | PowerShell 7（`pwsh`）或 Windows PowerShell 5.1 |

### 被 Windows 拦截 / 提示「未知发布者」怎么办

本程序目前**未做代码签名**，属于「自动装软件」类工具，Windows SmartScreen / Defender 可能拦截或提示「未知发布者」。**这不是病毒**（源码与构建流水线全部公开）。

| 情况 | 处理方式 |
|---|---|
| SmartScreen 蓝色弹窗 | 点「更多信息 → 仍要运行」 |
| 优先下载 ZIP 版 | 解压后运行文件夹内 `WinDeploy.exe`，比单文件版触发杀软的概率低 |
| Defender 删除 / 隔离 | 「Windows 安全中心 → 病毒和威胁防护 → 保护历史记录」恢复文件；可在 [Microsoft 误报提交](https://www.microsoft.com/wdsi/filesubmission) 报告 |
| 彻底解决（开发者） | 配置仓库 Secrets：`SIGN_PFX_BASE64` + `SIGN_PFX_PASSWORD`，Release CI 已内置 Authenticode 签名步骤 |

### 开发人员模式

WSL、系统调优、高级工具等专业功能通过**开发人员模式**门控。在「设置 → 开发者选项」启用后，左侧导航立即出现对应页面。启用时会弹出二次确认弹窗，请仔细阅读提示后再操作。

### 安全说明

- **SSH 私钥**：每台设备独立生成，绝不写入 `configs/` 仓库
- **配置脱敏**：导出时自动识别并删除 token、password、api_key、secret 等字段，可在「设置 → 脱敏关键词」自定义
- **注册表操作**：系统调优页面所有改动均可在同一页面**一键复原**
- **UAC 提权**：仅在需要时（SFC / DISM / 系统级 PATH 写入）发起，不长期持有管理员权限

### 已知限制

- `mingw` 条目含占位 URL，使用前请在 `catalog.json` 中补全实际下载地址
- ConPTY 终端功能需要 Windows 10 Build 17763+，更旧系统终端页面不可用
- 部分 winget ID 以 `TODO:` 标注，安装前请先 `validate` 校验

---

## 版权许可 License

本项目对外采用 **CC BY-NC-SA 4.0**（署名 - 非商业性使用 - 相同方式共享 4.0 国际）许可，完整条款见 [`LICENSE`](LICENSE)。

### 许可摘要

| 权利 | 说明 |
|---|---|
| **署名（BY）** | 使用须保留作者版权声明，并注明是否有改动 |
| **非商业（NC）** | 禁止将本项目或其衍生作品用于商业用途 |
| **相同方式共享（SA）** | 修改 / 二次开发后再发布，必须以相同许可证开源 |

### 允许的使用

- 个人学习、研究、非商业项目
- 在遵守上述三项条件的前提下复制、修改、再分发

### 禁止的使用

- 将本项目或其衍生版本用于任何商业产品、服务或盈利目的
- 以闭源方式发布基于本项目的修改版本
- 移除或篡改版权声明

### 作者特权

作者 **Tommy131** 为唯一版权持有人，**不受「非商业」限制约束**，保留包括商业使用与另行授权（双重许可）在内的全部权利。

**商业授权咨询**：hanskijay@owoblog.com

---

Copyright © 2026 Tommy131 · <https://github.com/Tommy131>
