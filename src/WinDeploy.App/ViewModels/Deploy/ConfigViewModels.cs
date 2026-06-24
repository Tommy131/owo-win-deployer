using System.Collections.ObjectModel;
using System.Windows.Input;
using WinDeploy.Core;
using WinDeploy.Core.Config;
using WinDeploy.Core.Engine;
using WinDeploy.Core.Engine.Installers;
using WinDeploy.Core.I18n;
using WinDeploy.Core.Models;

namespace WinDeploy.App.ViewModels.Deploy;

public sealed class ResultRowViewModel
{
    public string Name { get; init; } = "";
    public string Message { get; init; } = "";
    public string Kind { get; init; } = "ok"; // ok | failed | skip
}

public sealed class ConfigRowViewModel
{
    public string Name { get; init; } = "";
    public string ApplyWhen { get; init; } = "";
    public string Target { get; init; } = "";
}

public sealed class ExportFileViewModel
{
    public string Path { get; init; } = "";
    public string SizeText { get; init; } = "";
    public string Preview { get; init; } = "";
}

public sealed class ExportRowViewModel
{
    public string Name { get; init; } = "";
    public string Message { get; init; } = "";
    public string Kind { get; init; } = "ok";
    public List<ExportFileViewModel> Files { get; init; } = new();
    public bool HasFiles => Files.Count > 0;
}

/// <summary>Shared plumbing for the config-sync and export pages.</summary>
public abstract class ConfigPageBase : ObservableObject
{
    protected Catalog? Catalog;
    protected PathResolver Resolver = new(new Dictionary<string, string>());
    protected string RepoRoot = "";

    public void Initialize(Catalog catalog, PathResolver resolver, string repoRoot)
    {
        Catalog = catalog;
        Resolver = resolver;
        RepoRoot = repoRoot;
        OnInitialized();
    }

    protected virtual void OnInitialized() { }

    private bool _isBusy;
    public bool IsBusy
    {
        get => _isBusy;
        set { if (Set(ref _isBusy, value)) CommandManager.InvalidateRequerySuggested(); }
    }

    protected EngineContext Context() => new() { Path = Resolver, RepoRoot = RepoRoot, Ct = CancellationToken.None };
    protected Task<bool> IsInstalled(CatalogItem item) => Detection.IsInstalledAsync(item, Resolver);

    protected static ResultRowViewModel ToRow(ConfigResult r) => new()
    {
        Name = r.Name,
        Message = r.Message ?? "",
        Kind = r.Status switch { StepStatus.Ok => "ok", StepStatus.Failed => "failed", _ => "skip" },
    };
}

public sealed class ConfigSyncViewModel : ConfigPageBase
{
    public ObservableCollection<ConfigRowViewModel> Items { get; } = new();
    public ObservableCollection<ResultRowViewModel> Results { get; } = new();
    public RelayCommand ApplyCommand { get; }
    public RelayCommand SshCommand { get; }

    private bool _includeAsk;
    public bool IncludeAsk { get => _includeAsk; set => Set(ref _includeAsk, value); }

    private bool _registerSsh;
    public bool RegisterSsh { get => _registerSsh; set => Set(ref _registerSsh, value); }

    public ConfigSyncViewModel()
    {
        ApplyCommand = new RelayCommand(async _ => await ApplyAsync(), _ => !IsBusy);
        SshCommand = new RelayCommand(async _ => await SshAsync(), _ => !IsBusy);
    }

    protected override void OnInitialized()
    {
        Items.Clear();
        if (Catalog == null) return;
        foreach (var it in Catalog.Items.Where(i => i.Config != null))
            Items.Add(new ConfigRowViewModel { Name = it.Name, ApplyWhen = it.Config!.ApplyWhen, Target = it.Config.Target ?? "—" });
        Items.Add(new ConfigRowViewModel { Name = Localizer.T("cfgsync.envVars"), ApplyWhen = "always", Target = Localizer.T("cfgsync.envVars.target") });
    }

    private async Task ApplyAsync()
    {
        if (Catalog is not { } cat) return;
        IsBusy = true;
        Results.Clear();
        var ctx = Context();
        var ask = IncludeAsk;
        var results = await Task.Run(() => new ConfigEngine().ApplyAsync(cat, ctx, IsInstalled, ask));
        foreach (var r in results) Results.Add(ToRow(r));
        IsBusy = false;
    }

    private async Task SshAsync()
    {
        IsBusy = true;
        Results.Clear();
        var root = RepoRoot;
        var reg = RegisterSsh;
        var results = await Task.Run(() => SshSetup.RunAsync(root, reg, CancellationToken.None));
        foreach (var r in results) Results.Add(ToRow(r));
        IsBusy = false;
    }
}

public sealed class ExportViewModel : ConfigPageBase
{
    public ObservableCollection<ExportRowViewModel> Results { get; } = new();
    public RelayCommand ExportCommand { get; }

    private bool _done;
    public bool Done { get => _done; set => Set(ref _done, value); }

    public ExportViewModel()
        => ExportCommand = new RelayCommand(async _ => await ExportAsync(), _ => !IsBusy);

    private async Task ExportAsync()
    {
        if (Catalog is not { } cat) return;
        IsBusy = true;
        Done = false;
        Results.Clear();
        var ctx = Context();
        var results = await Task.Run(() => new ConfigEngine().ExportAsync(cat, ctx, IsInstalled));
        foreach (var r in results)
        {
            Results.Add(new ExportRowViewModel
            {
                Name = r.Name,
                Message = r.Message ?? "",
                Kind = r.Status switch { StepStatus.Ok => "ok", StepStatus.Failed => "failed", _ => "skip" },
                Files = (r.Files ?? new()).Select(f => new ExportFileViewModel
                {
                    Path = f.Path,
                    SizeText = FormatSize(f.Size),
                    Preview = f.Preview,
                }).ToList(),
            });
        }
        Done = true;
        IsBusy = false;
    }

    private static string FormatSize(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:0.0} KB";
        return $"{bytes / (1024.0 * 1024):0.0} MB";
    }
}
