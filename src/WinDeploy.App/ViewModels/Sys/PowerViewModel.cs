using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using WinDeploy.Core.I18n;

namespace WinDeploy.App.ViewModels.Sys;

/// <summary>One quick-apply preset card (节能 / 平衡 / 高性能 / 卓越性能). Title/Detail resolve through the
/// localizer by <see cref="Key"/>, so they follow the active UI language.</summary>
public sealed class PowerPresetViewModel : ObservableObject
{
    public string Key { get; }
    public string Glyph { get; }
    public string Guid { get; }

    public PowerPresetViewModel(string key, string glyph, string guid)
    {
        Key = key;
        Glyph = glyph;
        Guid = guid;
    }

    public string Title => Localizer.T($"power.preset.{Key}.title");
    public string Detail => Localizer.T($"power.preset.{Key}.detail");

    private bool _isActive;
    public bool IsActive
    {
        get => _isActive;
        set { if (Set(ref _isActive, value)) { OnPropertyChanged(nameof(CanApply)); OnPropertyChanged(nameof(ActionLabel)); } }
    }

    /// <summary>False until the (hidden) Ultimate Performance scheme is registered — its card then shows
    /// 解锁并应用 instead of 应用.</summary>
    private bool _available = true;
    public bool Available
    {
        get => _available;
        set { if (Set(ref _available, value)) OnPropertyChanged(nameof(ActionLabel)); }
    }

    public bool CanApply => !_isActive;
    public string ActionLabel => _isActive ? Localizer.T("power.current")
        : _available ? Localizer.T("power.apply") : Localizer.T("power.unlock");
}

/// <summary>One row in the "全部电源计划" list — every detected scheme (built-in + custom), apply-able.</summary>
public sealed class PowerPlanRowViewModel : ObservableObject
{
    public PowerPlan Plan { get; }
    public PowerPlanRowViewModel(PowerPlan plan, bool active) { Plan = plan; _isActive = active; }

    public string Guid => Plan.Guid;
    public string ShortGuid => Plan.Guid.Length >= 8 ? Plan.Guid[..8] : Plan.Guid;

    /// <summary>Built-in schemes show a localized name; custom schemes show their (ASCII) powercfg name, or a
    /// generic label keyed by short GUID when the name is empty / non-ASCII (avoids console code-page mojibake).</summary>
    public string Name
    {
        get
        {
            if (PowerPlans.KnownKey(Plan.Guid) is { } key) return Localizer.T(key);
            if (!string.IsNullOrWhiteSpace(Plan.Name) && IsAscii(Plan.Name)) return Plan.Name;
            return Localizer.Format("power.plan.custom", ShortGuid);
        }
    }

    private bool _isActive;
    public bool IsActive
    {
        get => _isActive;
        set { if (Set(ref _isActive, value)) { OnPropertyChanged(nameof(CanApply)); OnPropertyChanged(nameof(ActionLabel)); } }
    }
    public bool CanApply => !_isActive;
    public string ActionLabel => _isActive ? Localizer.T("power.current") : Localizer.T("power.apply");

    private static bool IsAscii(string s) { foreach (var c in s) if (c > '\x7f') return false; return true; }
}

/// <summary>The "能效管理" page (系统 group): manage Windows power-scheduling policies — one-click switch
/// between 节能 / 平衡 / 高性能 / 卓越性能, plus a full list of every registered scheme. All via powercfg,
/// no elevation required.</summary>
public sealed class PowerViewModel : LocalizedObject
{
    public ObservableCollection<PowerPresetViewModel> Presets { get; } = new();
    public ObservableCollection<PowerPlanRowViewModel> Plans { get; } = new();

    public RelayCommand ApplyPresetCommand { get; }
    public RelayCommand ApplyPlanCommand { get; }
    public RelayCommand RefreshCommand { get; }

    private string _activeGuid = "";

    public PowerViewModel()
    {
        Presets.Add(new PowerPresetViewModel("saver", "", PowerPlans.PowerSaver));
        Presets.Add(new PowerPresetViewModel("balanced", "", PowerPlans.Balanced));
        Presets.Add(new PowerPresetViewModel("high", "", PowerPlans.HighPerformance));
        Presets.Add(new PowerPresetViewModel("ultimate", "", PowerPlans.UltimatePerformance));

        ApplyPresetCommand = new RelayCommand(p => { if (p is PowerPresetViewModel x) ApplyPreset(x); });
        ApplyPlanCommand = new RelayCommand(p => { if (p is PowerPlanRowViewModel x) ApplyPlan(x); });
        RefreshCommand = new RelayCommand(_ => Reload());
        Reload();
    }

    protected override void OnCultureChanged()
    {
        base.OnCultureChanged();
        foreach (var p in Presets) p.RaiseAllPropertiesChanged();
        foreach (var p in Plans) p.RaiseAllPropertiesChanged();
    }

    private string _activePlanName = "";
    public string ActivePlanName { get => _activePlanName; private set => Set(ref _activePlanName, value); }

    private string _note = "";
    public string Note { get => _note; set { if (Set(ref _note, value)) OnPropertyChanged(nameof(HasNote)); } }
    public bool HasNote => !string.IsNullOrEmpty(_note);

    private void Reload()
    {
        List<PowerPlan> plans;
        try { plans = PowerPlans.List(); }
        catch { plans = new(); }
        _activeGuid = PowerPlans.ActiveGuid() ?? plans.FirstOrDefault(p => p.Active)?.Guid ?? "";
        var hasUltimate = plans.Any(p => Same(p.Guid, PowerPlans.UltimatePerformance));

        Plans.Clear();
        foreach (var p in plans) Plans.Add(new PowerPlanRowViewModel(p, Same(p.Guid, _activeGuid)));

        foreach (var preset in Presets)
        {
            preset.Available = preset.Key != "ultimate" || hasUltimate;
            preset.IsActive = Same(preset.Guid, _activeGuid);
        }

        ActivePlanName = ResolveActiveName(plans);
        CommandManager.InvalidateRequerySuggested();
    }

    private string ResolveActiveName(List<PowerPlan> plans)
    {
        if (string.IsNullOrEmpty(_activeGuid)) return Localizer.T("power.active.unknown");
        if (PowerPlans.KnownKey(_activeGuid) is { } key) return Localizer.T(key);
        var match = plans.FirstOrDefault(p => Same(p.Guid, _activeGuid));
        return match != null ? new PowerPlanRowViewModel(match, true).Name : Localizer.T("power.active.unknown");
    }

    private void ApplyPreset(PowerPresetViewModel preset)
    {
        var guid = preset.Guid;
        if (preset.Key == "ultimate" && !preset.Available)
        {
            if (Dialogs.Show(Localizer.T("power.ultimate.unlockConfirm"), Localizer.T("power.title"),
                    MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            var (ok, guidOrMsg) = PowerPlans.UnlockUltimate();
            if (!ok) { Warn(guidOrMsg); return; }
            guid = guidOrMsg;
            AuditLog.Action("能效管理：解锁并注册卓越性能电源计划");
        }

        var (applied, msg) = PowerPlans.SetActive(guid);
        if (!applied) { Warn(msg); return; }
        var name = Localizer.T($"power.preset.{preset.Key}.title");
        AuditLog.Action($"能效管理：切换电源计划 → {name}（{guid}）");
        Note = Localizer.Format("power.note.applied", name);
        Reload();
    }

    private void ApplyPlan(PowerPlanRowViewModel row)
    {
        var (ok, msg) = PowerPlans.SetActive(row.Guid);
        if (!ok) { Warn(msg); return; }
        AuditLog.Action($"能效管理：切换电源计划 → {row.Name}（{row.Guid}）");
        Note = Localizer.Format("power.note.applied", row.Name);
        Reload();
    }

    private void Warn(string msg)
        => Dialogs.Show(msg, Localizer.T("power.title"), MessageBoxButton.OK, MessageBoxImage.Warning);

    private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
