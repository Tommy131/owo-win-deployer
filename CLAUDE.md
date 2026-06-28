# owo-win-deployer (OwO! Win Deployer)

Windows 环境复刻器：一键在新设备安装软件/工具链/环境并同步个人配置。**完整设计见 `docs/DESIGN.md`（权威来源）。**

## 关键决定
- 技术栈：C# / .NET 10；引擎 `WinDeploy.Core`（纯库），CLI `WinDeploy.Cli`；GUI 为 WPF 自包含单 exe（M3）。
- 数据/引擎分离：`catalog/catalog.json`（软件主清单）+ `catalog/profiles/` + `configs/`（配置仓库，与是否安装解耦）。
- 安装方式：winget / winget-bundle / portable / git / conda / vscode-ext / script。
- 逐项可选：每项 `default` 决定是否预选（开发+系统=强制，其余可选）。
- 跨设备：路径用 `${DevRoot}` / `${ToolsDir}` 变量，首次设定（不假设 D 盘）。
- 安全：SSH 私钥每台新生成、永不入库；配置内 secrets 默认排除。
- 多语言（zh/en/de）：`WinDeploy.Core/I18n/Localizer`（共享，内嵌 `Resources/<lang>/*.json`，回退 当前→en→key）。XAML 用 `{DynamicResource S.<key>}`（镜像 `ThemeManager` 即时切换），代码用 `Localizer.T/Format`。首启按系统语言，设置页可切。**审计日志（AuditLog 正文）有意保留中文，作为诊断记录。**

## 路线图
M1 引擎打通（CLI）→ M2 配置同步+导出 → M3 WPF GUI（软件安装中心）→ M4 多机同步+发布 → **M5 系统管理与专业工具（系统概览/维护、WSL、系统调优、高级工具；开发人员模式门控）**。均已实现。

## 目录结构（App，folder = namespace）
- `WinDeploy.App` 按功能分子文件夹，**文件夹名 = 子命名空间**：`Services/{Sys,Net,Software,Terminal,Infra,Ftp}`、`ViewModels/{Deploy,Sys,Ftp,Net,Terminal,Tools,Shell}`（`ObservableObject/LocalizedObject/MainViewModel/ItemViewModels` 留根）、`Views/{Deploy,Sys,Ftp,Cloudflare,Server,Terminal,Tools,Shell,Common}`。
- 子命名空间在 `WinDeploy.App.csproj` 里 **global `<Using>`**，故 C# 引用可不带前缀；**XAML 必须显式 `xmlns`**（见 App.xaml 的 `vmX`/`vX` 前缀 + 每个 View 的 `x:Class`）。
- **命名避坑**：子命名空间不要叫会遮蔽 BCL/类型的名字 —— 用 `Sys`（非 System，否则遮蔽 `global::System`）、`Infra`（非 App，遮蔽 App 类）、`Common`（非 Dialogs，遮蔽 `Dialogs` 主题对话框类）。
- 弹窗用 `Dialogs.Show(body, title, MessageBoxButton, MessageBoxImage)`（`Views/Common/MessageDialog.cs`，按钮跟随应用语言）替代系统 `MessageBox`；崩溃弹窗仍用系统 MessageBox。

## 约定
- 加软件 = 改 `catalog.json`（主清单）+ `catalog/i18n/{en,de}.json`（软件 `summary` 译文，zh 用 catalog.json 原文），不动引擎。
- 加界面文案 = 在 `src/WinDeploy.Core/I18n/Resources/{en,zh,de}/<area>.json` 三语同步加 key（保持键集一致），XAML 用 `{DynamicResource S.<key>}`，代码用 `Localizer.T/Format`。改后跑 `scripts/check-i18n.ps1` 校验三语键对齐。
- Core 中匹配外部工具输出的中文（如 winget stdout 的 `Contains("已是最新")`）**绝不本地化**（已加 `// MATCHED:` 注释）。
- 提交信息不要加 AI 署名。

## 发布（release）
- 改版本 = 改根 `Directory.Build.props` 的 `<Version>`（App / CLI / Core 全部产物继承的跨产物单一版本源；App 运行时经 `AppInfo.Version` 从 assembly 读取，不要再在各 csproj 里写死版本），并同步 `README.md` / `README_CH.md` 顶部版本行；改后跑 `scripts/check-version-sync.ps1` 校验三者一致。
- **每次发布前必须更新 `docs/CHANGELOG.md`**：在顶部新增 `## v<版本> — <YYYY-MM-DD>` 段落，**写清楚本次发行「新增 / 删除 / 调整」了什么功能**（按 `### 新增`、`### 调整`、`### 删除` 分类，无对应项写「无」）。这是硬性约束，不能省略。
- 发行流程：推送 `v<版本>` tag 触发 `.github/workflows/release.yml`，CI 构建三种形态并创建 GitHub Release。Release notes 由 CI **从 `docs/CHANGELOG.md` 对应版本段落自动生成**（+ 静态下载表 `.github/release-body.md` + GitHub 自动提交列表），所以 CHANGELOG 段落写得清楚 = release 说明就清楚。
