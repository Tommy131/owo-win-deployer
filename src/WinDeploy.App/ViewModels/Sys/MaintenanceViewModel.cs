using System.Collections.ObjectModel;
using System.Windows;
using WinDeploy.App.Services;
using WinDeploy.Core.I18n;

namespace WinDeploy.App.ViewModels.Sys;

public sealed class RepairRowViewModel
{
    public RepairAction Action { get; }
    public RepairRowViewModel(RepairAction a) { Action = a; }
    public string Title => Action.Title;
    public string Detail => Action.Detail;
    public bool NeedsAdmin => Action.Elevate;
    public bool Risky => Action.Risky;
}

public sealed class JunkRowViewModel : ObservableObject
{
    public JunkTarget Target { get; }
    public JunkRowViewModel(JunkTarget t) { Target = t; }
    public string Name => Target.Name;
    public string Detail => Target.Detail;
    public string SizeText => Target.SizeText;
    private bool _selected = true;
    public bool IsSelected { get => _selected; set { if (Set(ref _selected, value)) SelectionChanged?.Invoke(); } }
    public event Action? SelectionChanged;
}

/// <summary>The "系统维护" page: one-click Windows repair commands (SFC/DISM/chkdsk/network/…),
/// a junk/cache cleaner, and a recent-error / crash log triage — the repair technician's bench.</summary>
public sealed class MaintenanceViewModel : LocalizedObject
{
    public ObservableCollection<RepairRowViewModel> Repairs { get; } = new();
    public ObservableCollection<JunkRowViewModel> Junk { get; } = new();
    public ObservableCollection<EventRow> Events { get; } = new();

    public RelayCommand RunRepairCommand { get; }
    public RelayCommand ScanJunkCommand { get; }
    public RelayCommand CleanJunkCommand { get; }
    public RelayCommand RefreshEventsCommand { get; }

    public MaintenanceViewModel()
    {
        foreach (var a in RepairCommands.All) Repairs.Add(new RepairRowViewModel(a));
        RunRepairCommand = new RelayCommand(p => { if (p is RepairRowViewModel r) RunRepair(r); });
        ScanJunkCommand = new RelayCommand(_ => _ = ScanJunkAsync(), _ => !IsScanning);
        CleanJunkCommand = new RelayCommand(_ => _ = CleanJunkAsync(), _ => Junk.Count > 0);
        RefreshEventsCommand = new RelayCommand(_ => _ = LoadEventsAsync(), _ => !IsLoadingEvents);
    }

    protected override void OnCultureChanged()
    {
        base.OnCultureChanged();
        // Re-localize live list rows (Name / Detail resolve through the localizer by id).
        foreach (var row in Junk) row.RaiseAllPropertiesChanged();
    }

    // ── Repair ────────────────────────────────────────────────────────────
    private void RunRepair(RepairRowViewModel r)
    {
        if (r.Risky && Dialogs.Show(Localizer.Format("maint.repair.riskyConfirm", r.Title, r.Detail),
                Localizer.T("maint.titleBox"), MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        var (ok, msg) = RepairCommands.Run(r.Action);
        if (!ok) Dialogs.Show(msg, r.Title, MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    // ── Junk cleaner ──────────────────────────────────────────────────────
    private bool _isScanning;
    public bool IsScanning { get => _isScanning; set => Set(ref _isScanning, value); }

    private string _junkNote = Localizer.T("maint.junk.note");
    public string JunkNote { get => _junkNote; set => Set(ref _junkNote, value); }

    private async Task ScanJunkAsync()
    {
        IsScanning = true;
        JunkNote = Localizer.T("maint.junk.scanning");
        var targets = JunkScanner.BuildTargets();
        var found = await Task.Run(() => JunkScanner.Scan(targets));
        Junk.Clear();
        foreach (var t in found.OrderByDescending(t => t.Bytes))
        {
            var row = new JunkRowViewModel(t);
            row.SelectionChanged += UpdateJunkNote;
            Junk.Add(row);
        }
        IsScanning = false;
        UpdateJunkNote();
        if (Junk.Count == 0) JunkNote = Localizer.T("maint.junk.empty");
    }

    private void UpdateJunkNote()
    {
        if (Junk.Count == 0) return;
        var sel = Junk.Where(j => j.IsSelected).ToList();
        var bytes = sel.Sum(j => j.Target.Bytes);
        JunkNote = Localizer.Format("maint.junk.selected", sel.Count, Size(bytes));
    }

    private async Task CleanJunkAsync()
    {
        var sel = Junk.Where(j => j.IsSelected).Select(j => j.Target).ToList();
        if (sel.Count == 0) return;
        var bytes = sel.Sum(t => t.Bytes);
        if (Dialogs.Show(Localizer.Format("maint.clean.confirm", sel.Count, Size(bytes)),
                Localizer.T("maint.clean.confirmTitle"), MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;

        // Run the (potentially slow) delete off the UI thread, behind a non-closable busy dialog so the
        // window can't be killed mid-clean and never looks frozen.
        var owner = Application.Current.MainWindow;
        var (count, freed) = await BusyDialog.RunAsync(owner, Localizer.T("maint.clean.busyTitle"),
            Localizer.Format("maint.clean.busyBody", sel.Count),
            () => Task.Run(() => JunkScanner.Clean(sel)));

        AuditLog.Action($"垃圾清理：处理 {count} 处，释放 {Size(freed)}");
        Dialogs.Show(Localizer.Format("maint.clean.done", count, Size(freed)), Localizer.T("maint.clean.confirmTitle"), MessageBoxButton.OK, MessageBoxImage.Information);
        await ScanJunkAsync();
    }

    // ── Event log ─────────────────────────────────────────────────────────
    private bool _isLoadingEvents;
    public bool IsLoadingEvents { get => _isLoadingEvents; set => Set(ref _isLoadingEvents, value); }

    private string _eventNote = Localizer.T("maint.event.note");
    public string EventNote { get => _eventNote; set => Set(ref _eventNote, value); }

    private async Task LoadEventsAsync()
    {
        IsLoadingEvents = true;
        EventNote = Localizer.T("maint.event.reading");
        var rows = await EventLogReader.RecentAsync();
        Events.Clear();
        foreach (var e in rows) Events.Add(e);
        IsLoadingEvents = false;
        var crashes = rows.Count(r => r.IsCrash);
        EventNote = rows.Count == 0
            ? Localizer.T("maint.event.none")
            : Localizer.Format("maint.event.summary", rows.Count) + (crashes > 0 ? Localizer.Format("maint.event.summaryCrash", crashes) : "");
    }

    private static string Size(long bytes) => bytes >= 1024L * 1024 * 1024
        ? $"{bytes / 1024.0 / 1024 / 1024:0.0} GB" : $"{bytes / 1024.0 / 1024:0.0} MB";
}
