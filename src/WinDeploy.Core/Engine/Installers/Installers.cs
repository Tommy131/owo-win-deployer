using System.IO.Compression;
using System.Security.Cryptography;
using WinDeploy.Core.Models;
using WinDeploy.Core.Util;

namespace WinDeploy.Core.Engine.Installers;

public sealed class WingetInstaller : IInstaller
{
    public string Method => "winget";

    public async Task<StepOutcome> RunAsync(CatalogItem item, EngineContext ctx)
    {
        var ins = item.Install;
        if (ins.Id is null) return StepOutcome.Fail("winget id missing");
        var args = new List<string>
        {
            "install", "--id", ins.Id, "-e",
            "--accept-source-agreements", "--accept-package-agreements", "--disable-interactivity",
        };
        if (ins.Scope != null) { args.Add("--scope"); args.Add(ins.Scope); }
        if (item.Version != null) { args.Add("--version"); args.Add(item.Version); }
        if (item.InstallPathOverride != null) { args.Add("--location"); args.Add(ctx.Path.Resolve(item.InstallPathOverride)); }
        ctx.Step($"winget 安装 {ins.Id}{(item.Version != null ? " " + item.Version : "")} …");
        var r = await Proc.RunAsync("winget", args, ct: ctx.Ct);
        return r.Ok ? StepOutcome.Done() : StepOutcome.Fail($"winget exit {r.ExitCode}");
    }
}

public sealed class WingetBundleInstaller : IInstaller
{
    public string Method => "winget-bundle";

    public async Task<StepOutcome> RunAsync(CatalogItem item, EngineContext ctx)
    {
        var ids = item.Install.Ids;
        if (ids is null || ids.Count == 0) return StepOutcome.Fail("winget-bundle ids missing");
        var failed = new List<string>();
        foreach (var id in ids)
        {
            var r = await Proc.RunAsync("winget", new[]
            {
                "install", "--id", id, "-e",
                "--accept-source-agreements", "--accept-package-agreements", "--disable-interactivity",
            }, ct: ctx.Ct);
            if (!r.Ok) failed.Add(id);
        }
        return failed.Count == 0 ? StepOutcome.Done() : StepOutcome.Fail("failed: " + string.Join(", ", failed));
    }
}

public sealed class PortableInstaller : IInstaller
{
    public string Method => "portable";

    public async Task<StepOutcome> RunAsync(CatalogItem item, EngineContext ctx)
    {
        var ins = item.Install;
        if (ins.Url is null || ins.ExtractTo is null) return StepOutcome.Fail("portable needs url + extractTo");

        var dest = ctx.Path.Resolve(item.InstallPathOverride ?? ins.ExtractTo);
        var tmpZip = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"windeploy_{item.Id}.zip");
        var tmpEx = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"windeploy_{item.Id}_x");

        ctx.Step($"开始下载 {ins.Url} …");
        using (var http = new HttpClient { Timeout = TimeSpan.FromMinutes(30) })
        await using (var src = await http.GetStreamAsync(ins.Url, ctx.Ct))
        await using (var f = File.Create(tmpZip))
            await src.CopyToAsync(f, ctx.Ct);

        if (ins.Sha256 is { Length: > 0 } sha && sha != "…")
        {
            ctx.Step("校验 SHA256 …");
            var actual = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(tmpZip, ctx.Ct)));
            if (!actual.Equals(sha, StringComparison.OrdinalIgnoreCase))
                return StepOutcome.Fail($"sha256 mismatch ({actual[..12]}…)");
        }
        else Log.Warn($"{item.Id}: no sha256 set — skipping integrity check");

        ctx.Step("解压 …");
        if (Directory.Exists(tmpEx)) Directory.Delete(tmpEx, true);
        ZipFile.ExtractToDirectory(tmpZip, tmpEx);

        var srcRoot = tmpEx;
        for (var i = 0; i < (ins.Strip ?? 0); i++)
        {
            var subs = Directory.GetDirectories(srcRoot);
            var files = Directory.GetFiles(srcRoot);
            if (subs.Length == 1 && files.Length == 0) srcRoot = subs[0];
            else break;
        }

        ctx.Step($"写入安装目录 {dest} …");
        Directory.CreateDirectory(dest);
        CopyDir(srcRoot, dest);

        foreach (var p in ins.Path ?? new List<string>())
            EnvPath.AddToUserPath(ctx.Path.Resolve(p));

        try { File.Delete(tmpZip); Directory.Delete(tmpEx, true); } catch { /* best effort */ }
        return StepOutcome.Done();
    }

    private static void CopyDir(string src, string dst)
    {
        Directory.CreateDirectory(dst);
        foreach (var dir in Directory.GetDirectories(src, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(dir.Replace(src, dst));
        foreach (var file in Directory.GetFiles(src, "*", SearchOption.AllDirectories))
            File.Copy(file, file.Replace(src, dst), overwrite: true);
    }
}

public sealed class GitInstaller : IInstaller
{
    public string Method => "git";

    public async Task<StepOutcome> RunAsync(CatalogItem item, EngineContext ctx)
    {
        var ins = item.Install;
        if (ins.Repo is null || ins.Dest is null) return StepOutcome.Fail("git needs repo + dest");
        var dest = ctx.Path.Resolve(item.InstallPathOverride ?? ins.Dest);

        ProcResult r;
        if (Directory.Exists(System.IO.Path.Combine(dest, ".git")))
        {
            ctx.Step($"git 拉取更新 {dest} …");
            r = await Proc.RunAsync("git", new[] { "-C", dest, "pull", "--ff-only" }, ct: ctx.Ct);
        }
        else
        {
            ctx.Step($"git 克隆 {ins.Repo} …");
            var a = new List<string> { "clone", "--depth", "1" };
            if (ins.Branch != null) { a.Add("--branch"); a.Add(ins.Branch); }
            a.Add(ins.Repo);
            a.Add(dest);
            r = await Proc.RunAsync("git", a, ct: ctx.Ct);
        }
        if (!r.Ok) return StepOutcome.Fail($"git exit {r.ExitCode}");

        foreach (var p in ins.Path ?? new List<string>())
            EnvPath.AddToUserPath(ctx.Path.Resolve(p));
        return StepOutcome.Done();
    }
}

public sealed class ExeInstaller : IInstaller
{
    public string Method => "exe";

    public async Task<StepOutcome> RunAsync(CatalogItem item, EngineContext ctx)
    {
        var ins = item.Install;
        if (ins.Url is null) return StepOutcome.Fail("exe 需要 url");

        var tmp = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"windeploy_{item.Id}_setup.exe");
        ctx.Step($"下载安装包 {ins.Url} …");
        using (var http = new HttpClient { Timeout = TimeSpan.FromMinutes(30) })
        await using (var src = await http.GetStreamAsync(ins.Url, ctx.Ct))
        await using (var f = File.Create(tmp))
            await src.CopyToAsync(f, ctx.Ct);

        ctx.Step("运行安装程序 …");
        var args = string.IsNullOrWhiteSpace(ins.Args)
            ? Array.Empty<string>()
            : ins.Args.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var r = await Proc.RunAsync(tmp, args, ct: ctx.Ct);

        try { File.Delete(tmp); } catch { /* best effort */ }
        return r.Ok ? StepOutcome.Done() : StepOutcome.Fail($"安装程序退出码 {r.ExitCode}");
    }
}

public sealed class CondaInstaller : IInstaller
{
    public string Method => "conda";

    public async Task<StepOutcome> RunAsync(CatalogItem item, EngineContext ctx)
    {
        var ins = item.Install;
        if (ins.EnvFile is null) return StepOutcome.Fail("conda needs envFile");
        var conda = CommandFinder.Find("conda") ?? CommandFinder.Find("mamba");
        if (conda is null) return StepOutcome.Fail("conda not found on PATH");

        var a = new List<string> { "env", "create", "-f", ctx.ResolveRepo(ins.EnvFile) };
        if (ins.EnvName != null) { a.Add("-n"); a.Add(ins.EnvName); }
        var r = await Proc.RunAsync(conda, a, ct: ctx.Ct);
        return r.Ok ? StepOutcome.Done() : StepOutcome.Fail($"conda exit {r.ExitCode}");
    }
}

public sealed class VscodeExtInstaller : IInstaller
{
    public string Method => "vscode-ext";

    public async Task<StepOutcome> RunAsync(CatalogItem item, EngineContext ctx)
    {
        var rel = item.Install.Extensions;
        if (rel is null) return StepOutcome.Fail("vscode-ext needs extensions file");
        var file = ctx.ResolveRepo(rel);
        if (!File.Exists(file)) return StepOutcome.Skip("extensions list missing");
        var code = CommandFinder.Find("code");
        if (code is null) return StepOutcome.Skip("code CLI not found");

        var exts = (await File.ReadAllLinesAsync(file, ctx.Ct))
            .Select(l => l.Trim())
            .Where(l => l.Length > 0 && !l.StartsWith('#'))
            .ToList();

        var failed = 0;
        foreach (var ext in exts)
        {
            var r = await Proc.RunAsync(code, new[] { "--install-extension", ext, "--force" }, ct: ctx.Ct);
            if (!r.Ok) failed++;
        }
        return failed == 0
            ? StepOutcome.Done($"{exts.Count} extensions")
            : StepOutcome.Fail($"{failed}/{exts.Count} failed");
    }
}

public sealed class ScriptInstaller : IInstaller
{
    public string Method => "script";

    public async Task<StepOutcome> RunAsync(CatalogItem item, EngineContext ctx)
    {
        var rel = item.Install.Run;
        if (rel is null) return StepOutcome.Fail("script needs run");
        var file = ctx.ResolveRepo(rel);
        if (!File.Exists(file)) return StepOutcome.Fail("script missing");
        var r = await Proc.RunAsync(file, Array.Empty<string>(), ct: ctx.Ct); // Proc wraps .ps1
        return r.Ok ? StepOutcome.Done() : StepOutcome.Fail($"script exit {r.ExitCode}");
    }
}
