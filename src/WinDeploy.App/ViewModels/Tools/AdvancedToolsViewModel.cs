using System.Text;
using System.Windows;
using WinDeploy.App.Services;
using WinDeploy.Core;
using WinDeploy.Core.Config;
using WinDeploy.Core.Engine;
using WinDeploy.Core.Engine.Installers;
using WinDeploy.Core.Export;
using WinDeploy.Core.I18n;
using WinDeploy.Core.Models;

namespace WinDeploy.App.ViewModels.Tools;

/// <summary>The "高级工具" page (开发人员模式): GUI front-ends for the professional Core features — 环境体检
/// (doctor), catalog 校验, 版本锁定 (lock.json), winget DSC 导出, 离线部署包, 迁移工具包. Dev-only.</summary>
public sealed class AdvancedToolsViewModel : LocalizedObject
{
    private Catalog? _catalog;
    private PathResolver _resolver = new(new Dictionary<string, string>());
    private string _repoRoot = "";
    private string _catalogDir = "";

    public RelayCommand DoctorCommand { get; }
    public RelayCommand ValidateCommand { get; }
    public RelayCommand LockCommand { get; }
    public RelayCommand DscCommand { get; }
    public RelayCommand OfflineCommand { get; }
    public RelayCommand MigrateExportCommand { get; }
    public RelayCommand MigrateImportCommand { get; }
    public RelayCommand EditHostsCommand { get; }
    public RelayCommand ClearCommand { get; }

    public AdvancedToolsViewModel()
    {
        DoctorCommand = new RelayCommand(_ => _ = RunDoctorAsync(), _ => !IsBusy);
        ValidateCommand = new RelayCommand(_ => RunValidate(), _ => !IsBusy);
        LockCommand = new RelayCommand(_ => _ = RunLockAsync(), _ => !IsBusy);
        DscCommand = new RelayCommand(_ => RunDsc(), _ => !IsBusy);
        OfflineCommand = new RelayCommand(_ => _ = RunOfflineAsync(), _ => !IsBusy);
        MigrateExportCommand = new RelayCommand(_ => _ = RunMigrateExportAsync(), _ => !IsBusy);
        MigrateImportCommand = new RelayCommand(_ => RunMigrateImport(), _ => !IsBusy);
        EditHostsCommand = new RelayCommand(_ => EditHosts());
        ClearCommand = new RelayCommand(_ => { Output = ""; _pristine = true; });
    }

    /// <summary>True while the output still shows the initial placeholder (no tool has run / output cleared),
    /// so a language switch can re-localize the placeholder without clobbering real results.</summary>
    private bool _pristine = true;

    protected override void OnCultureChanged()
    {
        if (_pristine) Output = Localizer.T("advtools.output.placeholder");
        base.OnCultureChanged();
    }

    public void Initialize(Catalog catalog, PathResolver resolver, string repoRoot, string catalogDir)
    {
        _catalog = catalog;
        _resolver = resolver;
        _repoRoot = repoRoot;
        _catalogDir = catalogDir;
    }

    private bool _isBusy;
    public bool IsBusy { get => _isBusy; set { if (Set(ref _isBusy, value)) System.Windows.Input.CommandManager.InvalidateRequerySuggested(); } }

    private string _output = Localizer.T("advtools.output.placeholder");
    public string Output { get => _output; set => Set(ref _output, value); }

    private void Append(string line) { _pristine = false; Output += (Output.Length == 0 ? "" : "\n") + line; }
    private void Section(string title) => Append((Output.Length == 0 ? "" : "\n") + $"── {title} ──");

    // ③ doctor
    private async Task RunDoctorAsync()
    {
        if (_catalog == null) return;
        IsBusy = true;
        Section(Localizer.T("advtools.doctor.section"));
        var findings = await Doctor.RunAsync(_catalog, _resolver);
        foreach (var f in findings)
        {
            var tag = f.Level switch { HealthLevel.Error => "✗", HealthLevel.Warn => "!", _ => "✓" };
            Append($"{tag} {f.Title} — {f.Detail.Replace("\n", "；")}");
            if (f.Fix != null) Append($"    → {f.Fix}");
        }
        Append(Localizer.Format("advtools.doctor.done", findings.Count(f => f.Level == HealthLevel.Error), findings.Count(f => f.Level == HealthLevel.Warn)));
        IsBusy = false;
    }

    // ④ validate
    private void RunValidate()
    {
        if (_catalog == null) return;
        Section(Localizer.T("advtools.validate.section"));
        var issues = CatalogValidator.Validate(_catalog, _repoRoot);
        if (issues.Count == 0) Append(Localizer.T("advtools.validate.passed"));
        foreach (var i in issues.OrderBy(i => i.Level))
            Append($"{(i.Level == IssueLevel.Error ? "✗" : "!")} [{i.ItemId}] {i.Message}");
        Append(Localizer.Format("advtools.validate.summary", issues.Count(i => i.Level == IssueLevel.Error), issues.Count(i => i.Level == IssueLevel.Warn)));
    }

    // ① lock
    private async Task RunLockAsync()
    {
        if (_catalog == null) return;
        IsBusy = true;
        Section(Localizer.T("advtools.lock.section"));
        Append(Localizer.T("advtools.lock.capturing"));
        var lf = await Lockfile.CaptureAsync(_catalog, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
        lf.Save(_catalogDir);
        AuditLog.Action($"生成 lock.json：{lf.Versions.Count} 个版本");
        Append(Localizer.Format("advtools.lock.done", Lockfile.DefaultPath(_catalogDir), lf.Versions.Count));
        Append(Localizer.T("advtools.lock.hint"));
        IsBusy = false;
    }

    // ⑥ DSC export
    private void RunDsc()
    {
        if (_catalog == null) return;
        var dlg = new Microsoft.Win32.SaveFileDialog { Title = Localizer.T("advtools.dsc.saveTitle"), FileName = "windeploy.dsc.yaml", Filter = "YAML (*.yaml)|*.yaml" };
        if (dlg.ShowDialog() != true) return;
        var items = Selection.Resolve(_catalog, null, null, all: true, null);
        System.IO.File.WriteAllText(dlg.FileName, DscExport.Build(items));
        Section(Localizer.T("advtools.dsc.section"));
        Append(Localizer.Format("advtools.dsc.exported", dlg.FileName));
        Append(Localizer.Format("advtools.dsc.cmd", dlg.FileName));
    }

    // ⑩ offline kit
    private async Task RunOfflineAsync()
    {
        if (_catalog == null) return;
        var fd = new Microsoft.Win32.OpenFolderDialog { Title = Localizer.T("advtools.offline.folderTitle") };
        if (fd.ShowDialog() != true) return;
        IsBusy = true;
        Section(Localizer.T("advtools.offline.section"));
        var items = Selection.Resolve(_catalog, null, null, false, null);   // 默认预选项
        Append(Localizer.Format("advtools.offline.predownload", items.Count, fd.FolderName));
        var ctx = new EngineContext
        {
            Path = _resolver, RepoRoot = _repoRoot, Ct = System.Threading.CancellationToken.None,
            Report = msg => Application.Current.Dispatcher.Invoke(() => Append("  " + msg)),
        };
        var results = await OfflineKit.DownloadAsync(items, fd.FolderName, ctx);
        foreach (var r in results)
            Append($"{(r.Status == StepStatus.Failed ? "✗" : r.Status == StepStatus.Skipped ? "·" : "✓")} {r.Name} — {r.Message}");
        Append(Localizer.Format("advtools.offline.done", fd.FolderName));
        IsBusy = false;
    }

    // ⑯ migration kit export
    private async Task RunMigrateExportAsync()
    {
        if (_catalog == null) return;
        var fd = new Microsoft.Win32.OpenFolderDialog { Title = Localizer.T("advtools.migrateExport.folderTitle") };
        if (fd.ShowDialog() != true) return;
        IsBusy = true;
        Section(Localizer.T("advtools.migrateExport.section"));
        var ctx = new EngineContext { Path = _resolver, RepoRoot = _repoRoot, Ct = System.Threading.CancellationToken.None };
        var results = await MigrationKit.ExportAsync(_catalog, ctx, fd.FolderName, it => Detection.IsInstalledAsync(it, _resolver));
        foreach (var r in results) Append($"{(r.Status == StepStatus.Ok ? "✓" : "·")} {r.Name} — {r.Message}");
        AuditLog.Action($"导出迁移工具包 → {fd.FolderName}");
        Append(Localizer.Format("advtools.migrateExport.done", fd.FolderName));
        IsBusy = false;
    }

    // ⑯ migration kit import
    private void RunMigrateImport()
    {
        var fd = new Microsoft.Win32.OpenFolderDialog { Title = Localizer.T("advtools.migrateImport.folderTitle") };
        if (fd.ShowDialog() != true) return;
        Section(Localizer.T("advtools.migrateImport.section"));
        var (results, manifest) = MigrationKit.Import(fd.FolderName, _repoRoot);
        foreach (var r in results) Append($"{(r.Status == StepStatus.Ok ? "✓" : "·")} {r.Name} — {r.Message}");
        if (manifest is { InstalledIds.Count: > 0 })
            Append(Localizer.Format("advtools.migrateImport.restoreHint", string.Join(",", manifest.InstalledIds)));
        AuditLog.Action($"还原迁移工具包 ← {fd.FolderName}");
    }

    // ⑪ hosts.json editor
    private void EditHosts()
    {
        try
        {
            var path = HostProfiles.FilePath(_catalogDir);
            if (!System.IO.File.Exists(path))
            {
                var example = System.IO.Path.Combine(_catalogDir, "hosts.example.json");
                if (System.IO.File.Exists(example)) System.IO.File.Copy(example, path);
                else System.IO.File.WriteAllText(path, "{\n  \"" + Environment.MachineName + "\": \"dev\",\n  \"*\": \"dev\"\n}\n");
                Section(Localizer.T("advtools.hosts.section"));
                Append(Localizer.Format("advtools.hosts.created", path));
            }
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex) { Append(Localizer.Format("advtools.hosts.openFailed", ex.Message)); }
    }
}
