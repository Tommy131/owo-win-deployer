using System.Windows.Controls;
using WinDeploy.App.ViewModels;

namespace WinDeploy.App.Views;

public partial class ProcessManagerView : UserControl
{
    public ProcessManagerView()
    {
        InitializeComponent();
        Loaded += (_, _) => (DataContext as ProcessManagerViewModel)?.StartLive();
        Unloaded += (_, _) => (DataContext as ProcessManagerViewModel)?.StopLive();
    }
}
