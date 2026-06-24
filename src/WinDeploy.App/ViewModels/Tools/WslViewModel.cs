using System.Collections.ObjectModel;
using System.Windows;
using WinDeploy.App.Services;
using WinDeploy.Core.I18n;

namespace WinDeploy.App.ViewModels.Tools;

public sealed class WslDistroRowViewModel
{
    public WslDistro D { get; }
    public WslDistroRowViewModel(WslDistro d) { D = d; }
    public string Name => D.Name;
    public string State => D.State;
    public string Version => $"WSL{D.Version}";
    public bool IsDefault => D.Default;
    public bool Running => string.Equals(D.State, "Running", StringComparison.OrdinalIgnoreCase);
}

/// <summary>The "WSL" page (开发人员模式): list installed distros, install from the online catalog, set
/// default, launch, terminate, export (backup .tar) and unregister. Dev-only.</summary>
public sealed class WslViewModel : LocalizedObject
{
    public ObservableCollection<WslDistroRowViewModel> Distros { get; } = new();
    public ObservableCollection<WslOnline> Online { get; } = new();

    public RelayCommand RefreshCommand { get; }
    public RelayCommand InstallCommand { get; }
    public RelayCommand ShutdownCommand { get; }
    public RelayCommand SetDefaultCommand { get; }
    public RelayCommand TerminateCommand { get; }
    public RelayCommand ExportCommand { get; }
    public RelayCommand UnregisterCommand { get; }
    public RelayCommand LaunchCommand { get; }
    public RelayCommand OpenFeaturesCommand { get; }
    public RelayCommand EnableFeatureCommand { get; }

    public WslViewModel()
    {
        RefreshCommand = new RelayCommand(_ => _ = LoadAsync());
        InstallCommand = new RelayCommand(_ => Install(), _ => FeatureEnabled && SelectedOnline != null);
        ShutdownCommand = new RelayCommand(_ => _ = ActAsync(Wsl.ShutdownAsync(), Localizer.T("wsl.msg.shutdownAll")));
        SetDefaultCommand = new RelayCommand(p => { if (p is WslDistroRowViewModel r) _ = ActAsync(Wsl.SetDefaultAsync(r.Name), Localizer.Format("wsl.msg.setDefault", r.Name)); });
        TerminateCommand = new RelayCommand(p => { if (p is WslDistroRowViewModel r) _ = ActAsync(Wsl.TerminateAsync(r.Name), Localizer.Format("wsl.msg.terminated", r.Name)); });
        ExportCommand = new RelayCommand(p => { if (p is WslDistroRowViewModel r) _ = ExportAsync(r); });
        UnregisterCommand = new RelayCommand(p => { if (p is WslDistroRowViewModel r) _ = UnregisterAsync(r); });
        LaunchCommand = new RelayCommand(p => { if (p is WslDistroRowViewModel r) Wsl.LaunchVisible($"-d \"{r.Name}\""); });
        OpenFeaturesCommand = new RelayCommand(_ => { var (_, m) = Wsl.OpenWindowsFeatures(); Note = Localizer.Format("wsl.msg.featuresOpenedHint", m); });
        EnableFeatureCommand = new RelayCommand(_ => { var (ok, m) = Wsl.EnableFeatureVisible(); Note = ok ? Localizer.T("wsl.msg.enabling") : m; });
        _ = LoadAsync();
    }

    protected override void OnCultureChanged()
    {
        base.OnCultureChanged();
        // Reload so the status note is rebuilt in the new language.
        _ = LoadAsync();
    }

    /// <summary>True only when the WSL optional feature is actually enabled (not just wsl.exe present).</summary>
    private bool _featureEnabled = true;
    public bool FeatureEnabled
    {
        get => _featureEnabled;
        set { if (Set(ref _featureEnabled, value)) { OnPropertyChanged(nameof(FeatureDisabled)); System.Windows.Input.CommandManager.InvalidateRequerySuggested(); } }
    }
    public bool FeatureDisabled => !_featureEnabled;

    private string _note = "";
    public string Note { get => _note; set => Set(ref _note, value); }

    private WslOnline? _selectedOnline;
    public WslOnline? SelectedOnline { get => _selectedOnline; set { if (Set(ref _selectedOnline, value)) System.Windows.Input.CommandManager.InvalidateRequerySuggested(); } }

    private async Task LoadAsync()
    {
        FeatureEnabled = Wsl.IsFeatureEnabled();
        if (!FeatureEnabled)
        {
            Distros.Clear();
            Online.Clear();
            Note = Localizer.T("wsl.msg.featureDisabled");
            return;
        }

        Note = Localizer.T("wsl.msg.loading");
        var distros = await Wsl.ListAsync();
        Distros.Clear();
        foreach (var d in distros) Distros.Add(new WslDistroRowViewModel(d));

        if (Online.Count == 0)
        {
            try { foreach (var o in await Wsl.ListOnlineAsync()) Online.Add(o); } catch { /* offline */ }
        }
        var wsl2 = Wsl.IsVmPlatformEnabled() ? "" : Localizer.T("wsl.msg.noVmPlatform");
        Note = Localizer.Format("wsl.msg.installed", Distros.Count, wsl2);
    }

    private void Install()
    {
        var name = SelectedOnline?.Name;
        if (name == null) return;
        var (ok, msg) = Wsl.InstallVisible(name);
        Note = ok ? Localizer.Format("wsl.msg.installing", name) : msg;
    }

    private async Task ActAsync(Task<(bool Ok, string Msg)> op, string okMsg)
    {
        var (ok, msg) = await op;
        Note = ok ? okMsg : msg;
        await LoadAsync();
    }

    private async Task ExportAsync(WslDistroRowViewModel r)
    {
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = Localizer.Format("wsl.export.saveTitle", r.Name), FileName = $"{r.Name}.tar", Filter = Localizer.T("wsl.export.filter"),
        };
        if (dlg.ShowDialog() != true) return;
        Note = Localizer.Format("wsl.msg.exporting", r.Name);
        var (ok, msg) = await Wsl.ExportAsync(r.Name, dlg.FileName);
        Note = msg;
        if (ok) AuditLog.Action($"WSL 导出：{r.Name} → {dlg.FileName}");
    }

    private async Task UnregisterAsync(WslDistroRowViewModel r)
    {
        if (Dialogs.Show(Localizer.Format("wsl.unregister.confirm", r.Name),
                Localizer.T("wsl.unregister.title"), MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        var (ok, msg) = await Wsl.UnregisterAsync(r.Name);
        Note = ok ? Localizer.Format("wsl.msg.unregistered", r.Name) : msg;
        if (ok) AuditLog.Action($"WSL 注销：{r.Name}");
        await LoadAsync();
    }
}
