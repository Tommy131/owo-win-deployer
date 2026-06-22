# OwO! Win Deployer (owo-win-deployer)

一键在任意 Windows 设备上复刻开发环境、应用与个人配置。完整设计见 [`docs/DESIGN.md`](docs/DESIGN.md)。

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

## 进度

| 里程碑 | 内容 | 状态 |
|---|---|---|
| M1 | 安装引擎（winget/winget-bundle/便携包/git/conda/vscode-ext/script）+ 幂等 + 路径变量 + CLI | ✅ |
| M2 | 配置套用 / 采集 + 导出脱敏 + env + SSH 每台新生成 | ✅ |
| M3 | WPF 软件安装中心（图标卡片逐项勾选）+ 运行进度 + 配置/导出页 | ✅ |
| M4 | 自包含单文件发布 + Release CI + 多机同步（sync/save） | ✅ |

## 构建与运行（需 .NET SDK 10）

```powershell
dotnet build WinDeploy.sln

# GUI（软件安装中心）
dotnet run --project src/WinDeploy.App

# CLI
dotnet run --project src/WinDeploy.Cli -- list
dotnet run --project src/WinDeploy.Cli -- plan  --profile dev
dotnet run --project src/WinDeploy.Cli -- apply --profile dev --yes
```

## CLI 命令

| 命令 | 说明 |
|---|---|
| `list` | 列出 catalog 全部软件 |
| `plan` | 显示安装/已装计划（不执行） |
| `apply` | 执行安装 |
| `apply-config` | 套用配置（VS Code/Git/env…，按 applyWhen） |
| `export` | 采集本机配置回写仓库（自动脱敏 token/密钥） |
| `ssh-setup [--register]` | 生成本机 SSH 密钥并套用 ssh 配置（`--register` 登记到 GitHub） |
| `sync` | `git pull` → 套用配置 + 显示安装计划 |
| `save [--message m] [--push]` | 提交 configs 改动（`--push` 推送远程） |

## 发布（自包含单文件，目标机免装 .NET）

```powershell
pwsh -File scripts/publish.ps1   # 产出 artifacts/app/WinDeploy.exe 与 artifacts/cli/windeploy.exe
```

打 `v*` tag 推送后，`.github/workflows/release.yml` 会自动构建并挂到 GitHub Release。

## 裸机引导

```powershell
irm https://raw.githubusercontent.com/Tommy131/owo-win-deployer/main/bootstrap/bootstrap.ps1 | iex
```

## 结构

```
catalog/        软件主清单 catalog.json + profiles/
configs/        配置仓库（与是否安装解耦）：vscode / git / ssh / env / lmstudio …
src/            WinDeploy.Core（引擎）+ WinDeploy.Cli + WinDeploy.App（WPF）
scripts/        publish.ps1
bootstrap/      bootstrap.ps1
.github/        release CI
docs/DESIGN.md  设计文档
```

> 安全：SSH 私钥每台设备新生成、永不入库；导出时 token/密钥自动脱敏。
>
> 联网 `apply` 前请补实 `catalog.json` 中 `mingw` 的占位 URL 及几个待核 winget ID。

## 被 Windows 拦截 / 提示「未知发布者」怎么办

本程序目前**未做代码签名**，且属于「自动装软件」类工具，Windows SmartScreen / Defender 有可能拦截或提示「未知发布者」。这是无签名安装类软件的常见现象，**并非病毒**（源码与构建流水线全部公开）。处理方式：

1. **SmartScreen 蓝色弹窗**：点「更多信息 → 仍要运行」。
2. **优先下载 ZIP 版**：ZIP 内是文件夹版 GUI（exe + 运行库），比单文件自解压 exe 触发杀软启发式的概率低很多；解压后运行其中的 `WinDeploy.exe`。
3. **被 Defender 删除 / 隔离**：到「Windows 安全中心 → 病毒和威胁防护 → 保护历史记录」恢复文件；并可在 <https://www.microsoft.com/wdsi/filesubmission> 提交误报。
4. **彻底解决（开发者）**：使用 Authenticode 代码签名证书（EV 证书可即时获得 SmartScreen 信誉）。发布工作流已内置签名步骤——在仓库 *Settings → Secrets and variables → Actions* 配置 `SIGN_PFX_BASE64`（证书 base64）与 `SIGN_PFX_PASSWORD` 后，每次发布会自动签名。

## 许可证 License

本项目对外采用 **CC BY-NC-SA 4.0**（署名 - 非商业性使用 - 相同方式共享 4.0 国际）许可，完整条款见 [`LICENSE`](LICENSE)：

- **署名（BY）**：使用须保留作者版权声明，并注明是否有改动。
- **非商业（NC）**：禁止他人将本项目或其衍生作品用于商业用途。
- **相同方式共享（SA）**：修改 / 二次开发后再发布，必须以相同许可证开源。

作者 **Tommy131** 为唯一版权持有人，**不受上述「非商业」限制**，保留包括商业使用与另行授权（双重许可）在内的全部权利。商业授权请联系 hanskijay@owoblog.com。

Copyright © 2026 Tommy131 · <https://github.com/Tommy131>
