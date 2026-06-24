using System.Windows.Controls;
using WinDeploy.App.ViewModels;

namespace WinDeploy.App.Views.Sys;

public partial class StartupView : UserControl
{
    public StartupView()
    {
        InitializeComponent();
        Loaded += (_, _) => { if (DataContext is StartupViewModel vm && vm.Items.Count == 0) vm.Refresh(); };
    }
}
