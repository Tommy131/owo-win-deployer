using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media;
using WinDeploy.App.Services;
using WinDeploy.Core.I18n;

namespace WinDeploy.App.ViewModels.Sys;

public sealed class StartupRowViewModel : ObservableObject
{
    public StartupEntry Entry { get; }

    public StartupRowViewModel(StartupEntry e, ImageSource? icon)
    {
        Entry = e;
        Badge = e.Name.Length > 0 ? e.Name[..1].ToUpperInvariant() : "?";
        IconImage = icon;
    }

    public string Name => Entry.Name;
    public string Command => Entry.Command;
    public string Source => Entry.Source;
    public bool NeedsAdmin => Entry.NeedsAdmin;

    public string Badge { get; }
    public ImageSource? IconImage { get; }
    public bool HasIcon => IconImage != null;
    public bool ShowLetter => IconImage == null;

    public bool Enabled => Entry.Enabled;
    public bool Disabled => !Entry.Enabled;
    public string ToggleLabel => Entry.Enabled ? Localizer.T("startup.toggle.disable") : Localizer.T("startup.toggle.enable");

    public void RaiseState()
    {
        OnPropertyChanged(nameof(Enabled));
        OnPropertyChanged(nameof(Disabled));
        OnPropertyChanged(nameof(ToggleLabel));
    }
}

/// <summary>The "启动项" page: manage Windows startup entries (Run keys + Startup folders) — enable /
/// disable (via StartupApproved, like Task Manager), remove, and reveal in Explorer.</summary>
public sealed class StartupViewModel : LocalizedObject
{
    public ObservableCollection<StartupRowViewModel> Items { get; } = new();
    public RelayCommand RefreshCommand { get; }
    public RelayCommand ToggleCommand { get; }
    public RelayCommand RemoveCommand { get; }
    public RelayCommand OpenLocationCommand { get; }

    public StartupViewModel()
    {
        RefreshCommand = new RelayCommand(_ => Refresh());
        ToggleCommand = new RelayCommand(p => { if (p is StartupRowViewModel r) Toggle(r); });
        RemoveCommand = new RelayCommand(p => { if (p is StartupRowViewModel r) Remove(r); });
        OpenLocationCommand = new RelayCommand(p => { if (p is StartupRowViewModel r) OpenLocation(r); });
    }

    protected override void OnCultureChanged()
    {
        base.OnCultureChanged();
        // Re-localize live row toggle labels and the summary line.
        foreach (var row in Items) row.RaiseAllPropertiesChanged();
        if (Items.Count > 0) UpdateSummary();
    }

    private string _summary = Localizer.T("startup.summary.idle");
    public string Summary { get => _summary; private set => Set(ref _summary, value); }

    public void Refresh() => _ = RefreshAsync();

    /// <summary>Scan startup entries and resolve each icon off the UI thread (process enumeration + icon
    /// extraction are slow), then populate. Icon order: bundled catalog → entry exe → live process → letter.</summary>
    private async Task RefreshAsync()
    {
        Summary = Localizer.T("startup.scanning");
        var rows = await Task.Run(() =>
        {
            var entries = StartupService.List().OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ToList();
            var running = RunningIcons.Snapshot();
            return entries.Select(e => new StartupRowViewModel(e, ResolveIcon(e, running))).ToList();
        });

        Items.Clear();
        foreach (var r in rows) Items.Add(r);
        UpdateSummary();
    }

    /// <summary>Best icon for a startup entry: bundled catalog icon / its own exe, else the icon of a
    /// matching running process, else null (the row shows the first-letter badge).</summary>
    private static ImageSource? ResolveIcon(StartupEntry e, RunningIcons running)
    {
        try { if (IconResolver.Resolve(e.Name, e.ExePath) is { } icon) return icon; }
        catch { /* fall through */ }

        // Borrow the icon from the entry's live process (covers entries whose own path is unresolved,
        // missing, or whose launcher exe has no embedded icon).
        try
        {
            var livePath = running.ResolvePath(e.ExePath, e.Name);
            if (livePath != null && IconExtractor.FromExeAnyIcon(livePath) is { } procIcon) return procIcon;
        }
        catch { /* letter fallback */ }
        return null;
    }

    private void UpdateSummary()
    {
        var on = Items.Count(i => i.Enabled);
        Summary = Localizer.Format("startup.summary", Items.Count, on, Items.Count - on);
    }

    private void Toggle(StartupRowViewModel r)
    {
        var (ok, msg) = StartupService.SetEnabled(r.Entry, !r.Entry.Enabled);
        if (!ok)
        {
            Dialogs.Show(Localizer.Format("startup.toggle.fail", msg),
                Localizer.T("startup.titleBox"), MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        r.RaiseState();
        AuditLog.Action($"启动项{(r.Entry.Enabled ? "启用" : "禁用")}：{r.Name}（{r.Source}）");
        UpdateSummary();
    }

    private void Remove(StartupRowViewModel r)
    {
        if (Dialogs.Show(Localizer.Format("startup.remove.confirm", r.Name, r.Source),
                Localizer.T("startup.remove.title"), MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        var (ok, msg) = StartupService.Remove(r.Entry);
        if (!ok)
        {
            Dialogs.Show(Localizer.Format("startup.remove.fail", msg),
                Localizer.T("startup.titleBox"), MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        AuditLog.Action($"启动项删除：{r.Name}（{r.Source}）");
        Items.Remove(r);
        UpdateSummary();
    }

    private void OpenLocation(StartupRowViewModel r)
    {
        try
        {
            var target = r.Entry.ExePath is { } exe && File.Exists(exe) ? exe : r.Entry.FilePath;
            if (!string.IsNullOrWhiteSpace(target))
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{target}\"") { UseShellExecute = true });
        }
        catch { /* ignore */ }
    }
}
