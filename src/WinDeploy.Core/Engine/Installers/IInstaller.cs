using WinDeploy.Core.Models;

namespace WinDeploy.Core.Engine.Installers;

/// <summary>Shared state handed to every installer.</summary>
public sealed class EngineContext
{
    public required PathResolver Path { get; init; }
    public required string RepoRoot { get; init; }
    public CancellationToken Ct { get; init; }

    /// <summary>Optional granular step reporter (e.g. "开始下载 …", "解压 …") for the progress page.</summary>
    public Action<string>? Report { get; init; }

    public void Step(string msg) => Report?.Invoke(msg);

    /// <summary>Resolve a repo-relative path (e.g. "configs/vscode/extensions.txt") to absolute.</summary>
    public string ResolveRepo(string relative)
        => System.IO.Path.GetFullPath(System.IO.Path.Combine(RepoRoot, relative));
}

public interface IInstaller
{
    string Method { get; }
    Task<StepOutcome> RunAsync(CatalogItem item, EngineContext ctx);
}
