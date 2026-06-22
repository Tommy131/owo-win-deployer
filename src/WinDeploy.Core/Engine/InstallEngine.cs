using System.Diagnostics;
using WinDeploy.Core.Engine.Installers;
using WinDeploy.Core.Models;

namespace WinDeploy.Core.Engine;

/// <summary>Builds a plan (with idempotency probing) and applies it, continuing on failure.</summary>
public sealed class InstallEngine
{
    private readonly Dictionary<string, IInstaller> _installers;

    public InstallEngine()
    {
        _installers = new IInstaller[]
        {
            new WingetInstaller(), new WingetBundleInstaller(), new PortableInstaller(),
            new GitInstaller(), new ExeInstaller(), new CondaInstaller(), new VscodeExtInstaller(), new ScriptInstaller(),
        }.ToDictionary(i => i.Method, StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyCollection<string> Methods => _installers.Keys;

    /// <summary>Run a single item's installer directly (used for one-off install / update reruns).</summary>
    public async Task<StepOutcome> RunOneAsync(CatalogItem item, EngineContext ctx)
    {
        if (!_installers.TryGetValue(item.Install.Method, out var inst))
            return StepOutcome.Fail($"unknown method '{item.Install.Method}'");
        return await inst.RunAsync(item, ctx);
    }

    public async Task<List<PlanItem>> BuildPlanAsync(IEnumerable<CatalogItem> selection, PathResolver pr)
    {
        var plan = new List<PlanItem>();
        foreach (var item in Order(selection))
        {
            var installed = await Detection.IsInstalledAsync(item, pr);
            plan.Add(new PlanItem
            {
                Item = item,
                Status = installed ? PlanStatus.Installed : PlanStatus.ToInstall,
            });
        }
        return plan;
    }

    public async Task<RunSummary> ApplyAsync(IEnumerable<PlanItem> plan, EngineContext ctx,
        bool dryRun, Action<PlanItem>? onStart = null, Action<RunResult>? onDone = null)
    {
        var summary = new RunSummary();
        void Record(RunResult r) { summary.Results.Add(r); onDone?.Invoke(r); }
        foreach (var pi in plan)
        {
            if (ctx.Ct.IsCancellationRequested) break;   // stop the batch on cancel
            var res = new RunResult { Item = pi.Item };

            if (pi.Status == PlanStatus.Installed)
            {
                res.Status = StepStatus.Skipped;
                res.Message = "already installed";
                Record(res);
                continue;
            }

            onStart?.Invoke(pi);

            if (dryRun)
            {
                res.Status = StepStatus.Skipped;
                res.Message = "dry-run";
                Record(res);
                continue;
            }

            if (!_installers.TryGetValue(pi.Item.Install.Method, out var inst))
            {
                res.Status = StepStatus.Failed;
                res.Message = $"unknown method '{pi.Item.Install.Method}'";
                Record(res);
                continue;
            }

            var sw = Stopwatch.StartNew();
            try
            {
                var outcome = await inst.RunAsync(pi.Item, ctx);
                res.Status = outcome.Status;
                res.Message = outcome.Message;
            }
            catch (Exception ex)
            {
                res.Status = StepStatus.Failed;
                res.Message = ex.Message;
            }
            res.Duration = sw.Elapsed;
            Record(res);
        }
        return summary;
    }

    /// <summary>Depth-first dependency ordering so a dependency installs before its dependents.</summary>
    private static List<CatalogItem> Order(IEnumerable<CatalogItem> items)
    {
        var list = items.ToList();
        var byId = list.ToDictionary(i => i.Id, StringComparer.OrdinalIgnoreCase);
        var result = new List<CatalogItem>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Visit(CatalogItem it)
        {
            if (!visited.Add(it.Id)) return;
            foreach (var dep in it.Depends ?? new List<string>())
                if (byId.TryGetValue(dep, out var d)) Visit(d);
            result.Add(it);
        }

        foreach (var it in list) Visit(it);
        return result;
    }
}
