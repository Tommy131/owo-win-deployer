using WinDeploy.Core;
using WinDeploy.Core.Config;
using WinDeploy.Core.Engine;
using WinDeploy.Core.Engine.Installers;
using WinDeploy.Core.Models;
using WinDeploy.Core.Util;

var argList = args.ToList();
if (argList.Count == 0 || argList[0] is "help" or "-h" or "--help")
{
    PrintHelp();
    return 0;
}

string command = argList[0];
var opts = Opts.Parse(argList.Skip(1).ToList());

// Locate catalog/ (explicit --catalog, else walk up from cwd, else from the exe location).
string? catalogDir = opts.Get("catalog") is string cp
    ? Path.GetDirectoryName(Path.GetFullPath(cp))
    : CatalogLoader.FindCatalogDir(Directory.GetCurrentDirectory())
      ?? CatalogLoader.FindCatalogDir(AppContext.BaseDirectory);

if (catalogDir is null)
{
    Log.Err("找不到 catalog/catalog.json（用 --catalog <path> 指定）");
    return 1;
}

string catalogPath = Path.Combine(catalogDir, "catalog.json");
string repoRoot = Path.GetDirectoryName(catalogDir)!;

Catalog catalog;
try { catalog = CatalogLoader.Load(catalogPath); }
catch (Exception ex) { Log.Err($"加载 catalog 失败: {ex.Message}"); return 1; }

var resolver = new PathResolver(catalog.PathVars);
Profile? profile = opts.Get("profile") is string pn ? CatalogLoader.LoadProfile(catalogDir, pn) : null;
var only = opts.Get("only")?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
bool all = opts.Has("all");
string? category = opts.Get("category");

return command switch
{
    "list" => CmdList(catalog),
    "plan" => await CmdPlan(catalog, resolver, profile, only, all, category),
    "apply" => await CmdApply(catalog, resolver, repoRoot, profile, only, all, category, opts.Has("yes")),
    "apply-config" => await CmdApplyConfig(catalog, resolver, repoRoot, opts.Has("yes")),
    "export" => await CmdExport(catalog, resolver, repoRoot),
    "ssh-setup" => await CmdSshSetup(repoRoot, opts.Has("register")),
    "sync" => await CmdSync(catalog, resolver, repoRoot, profile, only, all, category),
    "save" => await CmdSave(repoRoot, opts.Get("message"), opts.Has("push")),
    _ => Unknown(command),
};

async Task<int> CmdApplyConfig(Catalog cat, PathResolver pr, string root, bool includeAsk)
{
    var ctx = new EngineContext { Path = pr, RepoRoot = root, Ct = CancellationToken.None };
    var results = await new ConfigEngine().ApplyAsync(cat, ctx, it => Detection.IsInstalledAsync(it, pr), includeAsk);
    PrintConfig(results);
    return results.Any(r => r.Status == StepStatus.Failed) ? 1 : 0;
}

async Task<int> CmdExport(Catalog cat, PathResolver pr, string root)
{
    var ctx = new EngineContext { Path = pr, RepoRoot = root, Ct = CancellationToken.None };
    var results = await new ConfigEngine().ExportAsync(cat, ctx, it => Detection.IsInstalledAsync(it, pr));
    PrintConfig(results);
    Log.Info("已写回 configs/，记得 git commit");
    return 0;
}

async Task<int> CmdSshSetup(string root, bool register)
{
    var results = await SshSetup.RunAsync(root, register, CancellationToken.None);
    PrintConfig(results);
    return results.Any(r => r.Status == StepStatus.Failed) ? 1 : 0;
}

void PrintConfig(List<ConfigResult> results)
{
    Console.WriteLine();
    foreach (var r in results)
    {
        var tag = r.Status switch { StepStatus.Ok => "✓", StepStatus.Failed => "✗", _ => "·" };
        Console.WriteLine($"    {tag} {r.Name}  {r.Message}");
    }
    Console.WriteLine();
}

int Unknown(string c)
{
    Log.Err($"未知命令: {c}");
    PrintHelp();
    return 1;
}

int CmdList(Catalog cat)
{
    foreach (var grp in cat.Items.GroupBy(i => i.Category))
    {
        Console.WriteLine();
        Log.Step(grp.Key);
        foreach (var i in grp)
            Console.WriteLine($"    {(i.Default ? "●" : "○")} {i.Id,-18} {i.Summary ?? i.Name}");
    }
    Console.WriteLine();
    Log.Info("● = 默认强制安装   ○ = 可选");
    return 0;
}

async Task<int> CmdPlan(Catalog cat, PathResolver pr, Profile? prof,
    IReadOnlyCollection<string>? sel, bool selAll, string? cat2)
{
    var items = Selection.Resolve(cat, prof, sel, selAll, cat2);
    if (items.Count == 0) { Log.Warn("没有匹配的软件"); return 0; }

    var engine = new InstallEngine();
    var plan = await engine.BuildPlanAsync(items, pr);

    Console.WriteLine();
    foreach (var pi in plan)
    {
        var tag = pi.Status == PlanStatus.Installed ? "已装" : "待装";
        Console.WriteLine($"    [{tag}] {pi.Item.Install.Method,-13} {pi.Item.Name}");
    }
    var todo = plan.Count(p => p.Status == PlanStatus.ToInstall);
    Console.WriteLine();
    Log.Info($"共 {plan.Count} 项 · 待装 {todo} · 已装 {plan.Count - todo}");
    return 0;
}

async Task<int> CmdApply(Catalog cat, PathResolver pr, string root, Profile? prof,
    IReadOnlyCollection<string>? sel, bool selAll, string? cat2, bool yes)
{
    var items = Selection.Resolve(cat, prof, sel, selAll, cat2);
    if (items.Count == 0) { Log.Warn("没有匹配的软件"); return 0; }

    var engine = new InstallEngine();
    var plan = await engine.BuildPlanAsync(items, pr);
    var todo = plan.Where(p => p.Status == PlanStatus.ToInstall).ToList();

    Log.Info($"待装 {todo.Count} 项；已装 {plan.Count - todo.Count} 项将跳过");
    foreach (var pi in todo)
        Console.WriteLine($"    + {pi.Item.Name} ({pi.Item.Install.Method})");

    if (todo.Count == 0) { Log.Ok("全部就绪，无需安装"); return 0; }

    if (!yes)
    {
        Console.Write("\n  确认开始安装? [y/N] ");
        var answer = Console.ReadLine();
        if (answer?.Trim().ToLowerInvariant() is not ("y" or "yes")) { Log.Warn("已取消"); return 0; }
    }

    var ctx = new EngineContext { Path = pr, RepoRoot = root, Ct = CancellationToken.None };
    var summary = await engine.ApplyAsync(plan, ctx, dryRun: false,
        onStart: pi => Log.Step($"安装 {pi.Item.Name} …"));

    Console.WriteLine();
    foreach (var r in summary.Results.Where(r => r.Status == StepStatus.Failed))
        Log.Err($"{r.Item.Name}: {r.Message}");
    Log.Info($"完成 · 成功 {summary.Ok} · 跳过 {summary.Skipped} · 失败 {summary.Failed}");
    return summary.Failed > 0 ? 1 : 0;
}

async Task<int> CmdSync(Catalog cat, PathResolver pr, string root, Profile? prof,
    IReadOnlyCollection<string>? sel, bool selAll, string? cat2)
{
    Log.Step("git pull --ff-only");
    var pull = await Proc.RunAsync("git", new[] { "-C", root, "pull", "--ff-only" });
    Log.Info(pull.Ok ? "已拉取最新" : "拉取未成功（可能无远程 / 有冲突）");

    Log.Step("套用配置");
    var ctx = new EngineContext { Path = pr, RepoRoot = root, Ct = CancellationToken.None };
    var cfg = await new ConfigEngine().ApplyAsync(cat, ctx, it => Detection.IsInstalledAsync(it, pr), includeAsk: false);
    PrintConfig(cfg);

    Log.Step("安装计划（如需安装，运行 apply）");
    return await CmdPlan(cat, pr, prof, sel, selAll, cat2);
}

async Task<int> CmdSave(string root, string? message, bool push)
{
    await Proc.RunAsync("git", new[] { "-C", root, "add", "-A" });
    var msg = message ?? $"sync from {Environment.MachineName} {DateTime.Now:yyyy-MM-dd HH:mm}";
    var commit = await Proc.RunAsync("git", new[] { "-C", root, "commit", "-m", msg });
    Log.Info(commit.Ok ? $"已提交：{msg}" : "无改动或提交失败");
    if (push)
    {
        var p = await Proc.RunAsync("git", new[] { "-C", root, "push" });
        Log.Info(p.Ok ? "已 push" : "push 失败（检查远程）");
    }
    return 0;
}

void PrintHelp()
{
    Console.WriteLine("""
    OwO! Win Deployer — Windows 环境复刻器 (M1 / CLI)

      windeploy <命令> [选项]

    命令:
      list                    列出 catalog 中的全部软件
      plan                    显示将安装/已装的计划（不执行）
      apply                   执行安装
      apply-config            套用配置（VS Code/Git/env…，按 applyWhen）
      export                  采集本机配置回写仓库
      ssh-setup [--register]  生成本机 SSH 密钥并套用 ssh 配置
      sync                    git pull → 套用配置 + 显示安装计划
      save [--message m] [--push]   提交 configs 改动（--push 推送到远程）

    选项:
      --profile <名称>        使用预设 (catalog/profiles/<名称>.json)
      --only <id,id>          仅这些 id
      --category <类别>       仅该类别
      --all                   全部
      --yes                   apply 时跳过确认
      --catalog <路径>        指定 catalog.json

    示例:
      windeploy plan  --profile dev
      windeploy apply --profile dev --yes
      windeploy apply --only git,nodejs
    """);
}

sealed class Opts
{
    private readonly Dictionary<string, string?> _d = new(StringComparer.OrdinalIgnoreCase);

    public static Opts Parse(List<string> a)
    {
        var o = new Opts();
        for (var i = 0; i < a.Count; i++)
        {
            var t = a[i];
            if (!t.StartsWith("--")) continue;
            var key = t[2..];
            string? val = null;
            if (i + 1 < a.Count && !a[i + 1].StartsWith("--")) val = a[++i];
            o._d[key] = val;
        }
        return o;
    }

    public bool Has(string k) => _d.ContainsKey(k);
    public string? Get(string k) => _d.TryGetValue(k, out var v) ? v : null;
}
