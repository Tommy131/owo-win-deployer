using WinDeploy.Core.Models;
using WinDeploy.Core.Util;

namespace WinDeploy.Core.Engine;

/// <summary>Removes an installed item. <paramref name="purgeData"/> also deletes its config/data dirs.</summary>
public static class Uninstaller
{
    public static async Task<StepOutcome> UninstallAsync(CatalogItem item, PathResolver pr, bool purgeData,
        CancellationToken ct = default, Action<string>? report = null)
    {
        var ins = item.Install;
        switch (ins.Method)
        {
            case "winget" when ins.Id != null:
                report?.Invoke($"winget 卸载 {ins.Id} …");
                var r = await Proc.RunAsync("winget", new[]
                {
                    "uninstall", "--id", ins.Id, "-e", "--disable-interactivity", "--accept-source-agreements",
                }, ct: ct);
                if (!r.Ok) return StepOutcome.Fail($"winget uninstall 退出码 {r.ExitCode}");
                break;

            case "winget-bundle" when ins.Ids is { Count: > 0 } ids:
                foreach (var id in ids)
                {
                    report?.Invoke($"winget 卸载 {id} …");
                    await Proc.RunAsync("winget", new[] { "uninstall", "--id", id, "-e", "--disable-interactivity" }, ct: ct);
                }
                break;

            case "portable" when ins.ExtractTo != null:
                report?.Invoke("删除安装目录 …");
                if (!TryDeleteDir(pr.Resolve(item.InstallPathOverride ?? ins.ExtractTo)))
                    return StepOutcome.Fail("删除安装目录失败");
                break;

            case "git" when ins.Dest != null:
                report?.Invoke("删除克隆目录 …");
                if (!TryDeleteDir(pr.Resolve(item.InstallPathOverride ?? ins.Dest)))
                    return StepOutcome.Fail("删除目录失败");
                break;

            default:
                return StepOutcome.Fail("该类型暂不支持自动卸载");
        }

        if (purgeData && item.Config?.Target is { } target)
        {
            report?.Invoke("清除用户数据 …");
            TryDeleteDir(pr.Resolve(target));
        }

        return StepOutcome.Done(purgeData ? "已卸载并清除数据" : "已卸载（保留数据）");
    }

    private static bool TryDeleteDir(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, true); return true; }
        catch { return false; }
    }
}
