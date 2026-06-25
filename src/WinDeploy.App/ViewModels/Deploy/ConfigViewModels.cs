using System.Collections.ObjectModel;
using System.Windows;
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
    public RelayCommand ApplyEnvCommand { get; }
    public RelayCommand RemoteDeployCommand { get; }

    private bool _includeAsk;
    public bool IncludeAsk { get => _includeAsk; set => Set(ref _includeAsk, value); }

    private bool _registerSsh;
    public bool RegisterSsh { get => _registerSsh; set => Set(ref _registerSsh, value); }

    public ConfigSyncViewModel()
    {
        ApplyCommand = new RelayCommand(async _ => await ApplyAsync(), _ => !IsBusy);
        SshCommand = new RelayCommand(async _ => await SshAsync(), _ => !IsBusy);
        ApplyEnvCommand = new RelayCommand(async _ => await ApplyEnvAsync(), _ => !IsBusy);
        RemoteDeployCommand = new RelayCommand(_ =>
            new RemoteDeployDialog(RepoRoot) { Owner = System.Windows.Application.Current.MainWindow }.ShowDialog());
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

    /// <summary>Restore captured environment / agent configs (SSH, GnuPG, git credentials, Codex, Claude,
    /// OpenSSH) from the repo back onto this machine — the new-machine direction of "采集本机配置".</summary>
    private async Task ApplyEnvAsync()
    {
        var root = RepoRoot;

        // Drift preview: show exactly what a restore would create / overwrite before clobbering local config.
        var drift = await Task.Run(() => EnvCapture.PreviewApply(root));
        if (drift.Count == 0)
        {
            Dialogs.Show(Localizer.T("cfgsync.env.noCaptured"), Localizer.T("cfgsync.env.title"),
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var added = drift.Count(e => e.Status == EnvCapture.DriftStatus.New);
        var changed = drift.Count(e => e.Status == EnvCapture.DriftStatus.Changed);
        var same = drift.Count(e => e.Status == EnvCapture.DriftStatus.Same);
        if (added == 0 && changed == 0)
        {
            Dialogs.Show(Localizer.T("cfgsync.env.upToDate"), Localizer.T("cfgsync.env.title"),
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var body = new System.Text.StringBuilder();
        body.AppendLine(Localizer.Format("cfgsync.env.confirmDiff", added, changed, same)).AppendLine();
        foreach (var e in drift.Where(e => e.Status != EnvCapture.DriftStatus.Same)
                                .OrderBy(e => e.Status).ThenBy(e => e.RelPath).Take(25))
        {
            var tag = e.Status == EnvCapture.DriftStatus.New
                ? Localizer.T("cfgsync.env.tagNew") : Localizer.T("cfgsync.env.tagChanged");
            body.AppendLine($"  [{tag}] {e.Source} · {e.RelPath}");
        }
        if (added + changed > 25) body.AppendLine(Localizer.Format("cfgsync.env.diffMore", added + changed - 25));
        body.AppendLine().Append(Localizer.T("cfgsync.env.confirm"));

        if (Dialogs.Show(body.ToString(), Localizer.T("cfgsync.env.title"),
                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;

        IsBusy = true;
        Results.Clear();
        var results = await Task.Run(() => EnvCapture.Apply(root));
        foreach (var r in results) Results.Add(ToRow(r));
        AuditLog.Action($"恢复环境配置（新增 {added} · 覆盖 {changed}）");
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
    {
        ExportCommand = new RelayCommand(async _ => await ExportAsync(), _ => !IsBusy);
        _ = LoadScheduleStateAsync();
    }

    // ── 定时采集（Scheduled capture via Task Scheduler）─────────────────────────────
    private bool _loadingSchedule;
    private bool _scheduleEnabled;
    public bool ScheduleEnabled
    {
        get => _scheduleEnabled;
        set { if (Set(ref _scheduleEnabled, value) && !_loadingSchedule) _ = ApplyScheduleAsync(); }
    }

    private int _scheduleFreqIndex;   // 0 daily · 1 weekly · 2 on logon
    public int ScheduleFreqIndex
    {
        get => _scheduleFreqIndex;
        set { if (Set(ref _scheduleFreqIndex, value) && !_loadingSchedule && _scheduleEnabled) _ = ApplyScheduleAsync(); }
    }

    private string _scheduleNote = "";
    public string ScheduleNote { get => _scheduleNote; set => Set(ref _scheduleNote, value); }

    private async Task LoadScheduleStateAsync()
    {
        var on = await ScheduledExport.IsRegisteredAsync();
        _loadingSchedule = true;
        ScheduleEnabled = on;
        _loadingSchedule = false;
    }

    private async Task ApplyScheduleAsync()
    {
        if (_scheduleEnabled)
        {
            var exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe) || string.IsNullOrEmpty(RepoRoot))
            {
                ScheduleNote = Localizer.T("export.schedule.fail");
                return;
            }
            var freq = (ScheduledExport.Frequency)_scheduleFreqIndex;
            var (ok, msg) = await ScheduledExport.RegisterAsync(exe, RepoRoot, freq);
            ScheduleNote = ok ? Localizer.T("export.schedule.on") : Localizer.Format("export.schedule.failMsg", msg);
            if (ok) AuditLog.Action($"已登记定时采集任务（{freq}）");
        }
        else
        {
            var (ok, msg) = await ScheduledExport.UnregisterAsync();
            ScheduleNote = ok ? Localizer.T("export.schedule.off") : Localizer.Format("export.schedule.failMsg", msg);
            if (ok) AuditLog.Action("已移除定时采集任务");
        }
    }

    private async Task ExportAsync()
    {
        if (Catalog is not { } cat) return;

        // Ask up-front whether to include sensitive data. Default (No) keeps the safe behaviour — redact text
        // configs and skip private keys / tokens. Yes exports everything as-is (the user accepts the risk).
        var choice = Dialogs.Show(Localizer.T("export.sensitive.body"), Localizer.T("export.sensitive.title"),
            MessageBoxButton.YesNoCancel, MessageBoxImage.Warning);
        if (choice == MessageBoxResult.Cancel) return;
        var allowSensitive = choice == MessageBoxResult.Yes;

        IsBusy = true;
        Done = false;
        Results.Clear();
        var ctx = Context();
        var root = RepoRoot;
        var results = await Task.Run(async () =>
        {
            // Catalog-app configs (precise files) + well-known environment / agent configs (SSH, GnuPG, git
            // credentials, Codex, Claude, OpenSSH). Both honor the sensitive-export choice.
            var list = await new ConfigEngine().ExportAsync(cat, ctx, IsInstalled, redact: !allowSensitive);
            list.AddRange(EnvCapture.Capture(root, allowSensitive));
            return list;
        });
        if (allowSensitive) AuditLog.Action("导出配置：用户选择包含敏感数据（已忽略脱敏规则）");
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
