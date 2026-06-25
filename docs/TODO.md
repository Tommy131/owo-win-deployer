# TODO — 待开发功能清单

> 下一轮集中开发的候选功能，按「投入产出比」分三档。完成后请勾选并补一句实现说明。
> 现状：安装中心 / 配置同步 / 环境采集恢复 / 系统概览维护 / WSL / 调优 / FTP / Cloudflare / 终端 均已实现（M1–M5）。

## 🥇 第一档：补全已经开了头的能力（顺手、闭环）

- [ ] **智能体会话备份（Option A：只采小而关键的部分）**
  - 给 `EnvCapture` 扩展一个 `agent-sessions` 源，复用现有「采集本机配置 → 写回新机」管线，几乎零新 UI。
  - 采：Claude 的 `projects/**/memory/`、`settings.json`；Codex 的 `config.toml` / `AGENTS.md` / memories（**敏感开关下**）。
  - **排除**：transcripts（~493MB）、缓存、`auth.json`（密钥）、sqlite/`*.sqlite-wal`。
  - 写入 `configs/<id>/`，并加进 `.gitignore` 敏感块（个人专属，绝不入公共库）。
  - 背景：`~/.claude` ≈ 494MB（493MB 为 transcripts），`~/.codex` ≈ 1.4GB（含 auth.json/locked sqlite/caches）。全量 git 同步不可行，故只取小配置。

- [ ] **配置漂移 / Diff 视图**
  - 「采集本机配置」时对比仓库已有版本 vs 本机当前版本，列出新增/改动文件，让用户决定是否覆盖。
  - 现状是直接覆盖 + `.bak.<stamp>` 备份，缺预览。
  - 直接痛点：`configs/windows-terminal/settings.json`（跟踪的模板）会被采集反复覆盖。

- [ ] **「把当前勾选保存为 Profile」**
  - 安装中心目前只能套用 `catalog/profiles/` 里的预设清单。
  - 让用户把当前手动勾选的组合一键存为新 profile（写到 `catalog/profiles/<name>.json`），新机直接套用。

## 🥈 第二档：可靠性 / 可观测

- [ ] **部署报告（Deployment Report）**
  - apply 完成后生成 HTML/Markdown 报告：每项成功/失败/跳过、耗时、版本号、失败原因。
  - 复用已有 AuditLog + inventory HTML 导出的渲染。

- [ ] **SHA256 回填工具**
  - catalog 里很多 portable 项缺 sha256（`engine.validate.portableNoSha256` 会警告）。
  - 工具：下载一次 → 算 hash → 写回 `catalog.json`，提升完整性校验覆盖率。

- [ ] **自动化测试基线**
  - 新增 `WinDeploy.Core.Tests`，先覆盖纯逻辑：`Secrets.Redact`、`EnvCapture.Glob()`、catalog 校验、i18n 键对齐（把 `scripts/check-i18n.ps1` 也做成测试）。
  - 作为后续重构的安全网。

## 🥉 第三档：进阶 / 锦上添花

- [ ] **还原点（Restore Point）** — apply 配置前调 `Checkpoint-Computer` 建系统还原点，出错可回滚。
- [ ] **定时导出** — 用 Windows 计划任务定期把本机配置采集进仓库（与「智能体会话备份」搭配）。
- [ ] **远程 apply** — 复用已有 SSH/FTP 能力，把部署套件推到另一台机器执行。

## 杂项 / 已知待办

- [ ] **彻底取消跟踪 `configs/windows-terminal/settings.json`**（像 vscode 那样改为不入库），否则采集会反复覆盖该模板。
- [ ] **打包发布 v1.2.4**：把当前已提交但未发布的工作（`7f7eda2` 修复 + `e7017d6` 环境采集/恢复）连同本清单中完成的功能一起，按发布流程（改 csproj `<Version>` + README ×2 + CHANGELOG）发版。

---

### 建议开发顺序
先做 **第一档 #1（会话备份）+ #2（Diff 视图）**：复用现有 EnvCapture/ConfigSync 管线、闭合「环境复刻」主线，且 #2 直接解决 windows-terminal 反复被覆盖的痛点。然后把这些与已提交未发布的工作一起打包成 **v1.2.4**。
