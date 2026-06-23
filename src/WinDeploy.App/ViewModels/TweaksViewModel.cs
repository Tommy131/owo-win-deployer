using System.Collections.ObjectModel;
using System.Windows;
using WinDeploy.App.Services;

namespace WinDeploy.App.ViewModels;

public sealed class TweakRowViewModel : ObservableObject
{
    public RegTweak T { get; }
    public TweakRowViewModel(RegTweak t) { T = t; RefreshState(); }

    public string Title => T.Title;
    public string Detail => T.Detail;
    public bool NeedsAdmin => T.NeedsAdmin;

    private bool? _on;
    public bool? On { get => _on; private set { if (Set(ref _on, value)) { OnPropertyChanged(nameof(StateText)); OnPropertyChanged(nameof(ToggleLabel)); OnPropertyChanged(nameof(IsOn)); } } }
    public bool IsOn => _on == true;
    public string StateText => _on switch { true => "已开启", false => "已关闭", _ => "默认 / 未设置" };
    public string ToggleLabel => _on == true ? "关闭" : "开启";

    public void RefreshState() => On = RegTweaks.IsOn(T);
}

/// <summary>The "系统调优" page (开发人员模式): curated reversible Windows tweaks (Explorer / appearance /
/// privacy) with one-click on/off and live state. Dev-only.</summary>
public sealed class TweaksViewModel : ObservableObject
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

    private void Toggle(TweakRowViewModel r)
    {
        var target = !(r.On == true);
        var (ok, msg) = RegTweaks.Set(r.T, target);
        if (!ok)
        {
            MessageBox.Show($"操作失败：{msg}" + (r.NeedsAdmin ? "\n\n该项需以管理员身份运行 WinDeploy。" : ""),
                "系统调优", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        r.RefreshState();
        AuditLog.Action($"系统调优：{r.Title} → {(target ? "开启" : "关闭")}");

        if (r.T.RestartExplorer && MessageBox.Show(
                $"「{r.Title}」已更改，需重启资源管理器才能生效。\n\n现在重启？（桌面会短暂闪烁）",
                "系统调优", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
            RegTweaks.RestartExplorer();
    }
}
