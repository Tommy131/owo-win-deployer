using System.Collections.ObjectModel;
using System.Windows;
using WinDeploy.App.Services;
using WinDeploy.Core.I18n;

namespace WinDeploy.App.ViewModels.Tools;

public sealed class TweakRowViewModel : ObservableObject
{
    public RegTweak T { get; }
    public TweakRowViewModel(RegTweak t) { T = t; RefreshState(); }

    // T.Title / T.Detail hold localization keys (tweaks.item.<id>.title|detail) — resolve them here.
    public string Title => Localizer.T(T.Title);
    public string Detail => Localizer.T(T.Detail);
    public bool NeedsAdmin => T.NeedsAdmin;

    private bool? _on;
    public bool? On { get => _on; private set { if (Set(ref _on, value)) { OnPropertyChanged(nameof(StateText)); OnPropertyChanged(nameof(ToggleLabel)); OnPropertyChanged(nameof(IsOn)); } } }
    public bool IsOn => _on == true;
    public string StateText => _on switch { true => Localizer.T("tweaks.state.on"), false => Localizer.T("tweaks.state.off"), _ => Localizer.T("tweaks.state.default") };
    public string ToggleLabel => _on == true ? Localizer.T("tweaks.toggle.off") : Localizer.T("tweaks.toggle.on");

    public void RefreshState() => On = RegTweaks.IsOn(T);
}

/// <summary>The "系统调优" page (开发人员模式): curated reversible Windows tweaks (Explorer / appearance /
/// privacy) with one-click on/off and live state. Dev-only.</summary>
public sealed class TweaksViewModel : LocalizedObject
{
    public ObservableCollection<TweakRowViewModel> Tweaks { get; } = new();
    public RelayCommand ToggleCommand { get; }
    public RelayCommand RefreshCommand { get; }

    public TweaksViewModel()
    {
        // Hide tweaks the running Windows build doesn't support (e.g. Win11-only items on Windows 10).
        foreach (var t in RegTweaks.All)
            if (OsInfo.AtLeastBuild(t.MinBuild)) Tweaks.Add(new TweakRowViewModel(t));
        ToggleCommand = new RelayCommand(p => { if (p is TweakRowViewModel r) Toggle(r); });
        RefreshCommand = new RelayCommand(_ => { foreach (var r in Tweaks) r.RefreshState(); });
    }

    protected override void OnCultureChanged()
    {
        base.OnCultureChanged();
        foreach (var r in Tweaks) r.RaiseAllPropertiesChanged();
    }

    private void Toggle(TweakRowViewModel r)
    {
        var target = !(r.On == true);
        var (ok, msg) = RegTweaks.Set(r.T, target);
        if (!ok)
        {
            Dialogs.Show(Localizer.Format("tweaks.toggle.failed", msg) + (r.NeedsAdmin ? Localizer.T("tweaks.toggle.needAdminHint") : ""),
                Localizer.T("tweaks.title"), MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        r.RefreshState();
        AuditLog.Action($"系统调优：{r.Title} → {(target ? "开启" : "关闭")}");

        if (r.T.RestartExplorer && Dialogs.Show(
                Localizer.Format("tweaks.restartExplorer.confirm", r.Title),
                Localizer.T("tweaks.title"), MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
            RegTweaks.RestartExplorer();
    }
}
