using System.Windows.Controls;
using WinDeploy.App.ViewModels;

namespace WinDeploy.App.Views;

public partial class DetailView : UserControl
{
    public DetailView()
    {
        InitializeComponent();
        Loaded += (_, _) => (DataContext as DetailViewModel)?.StartRunningWatch();
        Unloaded += (_, _) => (DataContext as DetailViewModel)?.StopRunningWatch();
    }
}
