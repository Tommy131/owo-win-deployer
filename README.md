# OwO! Win Deployer (owo-win-deployer)

一键在任意 Windows 设备上复刻开发环境、应用与个人配置。完整设计见 [`docs/DESIGN.md`](docs/DESIGN.md)。

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

## 许可证 License

本项目对外采用 **CC BY-NC-SA 4.0**（署名 - 非商业性使用 - 相同方式共享 4.0 国际）许可，完整条款见 [`LICENSE`](LICENSE)：

- **署名（BY）**：使用须保留作者版权声明，并注明是否有改动。
- **非商业（NC）**：禁止他人将本项目或其衍生作品用于商业用途。
- **相同方式共享（SA）**：修改 / 二次开发后再发布，必须以相同许可证开源。

作者 **Tommy131** 为唯一版权持有人，**不受上述「非商业」限制**，保留包括商业使用与另行授权（双重许可）在内的全部权利。商业授权请联系 hanskijay@owoblog.com。

Copyright © 2026 Tommy131 · <https://github.com/Tommy131>
