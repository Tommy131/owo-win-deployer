using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using WinDeploy.App.ViewModels;

namespace WinDeploy.App.Views.Deploy;

public partial class InstallCenterView : UserControl
{
    private bool _restored;
    private bool _unloading;

    public InstallCenterView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += (_, _) => _unloading = true;
    }

    // Restore the scroll position the view-model remembers (e.g. after returning from detail).
    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (DataContext is not InstallCenterViewModel vm) { _restored = true; return; }
        var target = vm.ScrollOffset;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            Scroller.ScrollToVerticalOffset(target);
            _restored = true;
        }), DispatcherPriority.Loaded);
    }

    // Save only genuine user scrolls — not the initial 0-offset on (re)load, nor the teardown reset.
    private void Scroller_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (!_restored || _unloading) return;
        if (DataContext is InstallCenterViewModel vm) vm.ScrollOffset = e.VerticalOffset;
    }

    private void SetPath_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not InstallCenterViewModel vm) return;
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "选择安装根目录" };
        if (dlg.ShowDialog() == true) vm.SetPathForSelected(dlg.FolderName);
    }
}
