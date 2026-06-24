using System.Windows.Controls;

namespace WinDeploy.App.Views.Shell;

public partial class LogView : UserControl
{
    public LogView() => InitializeComponent();

    private void OutBox_TextChanged(object sender, TextChangedEventArgs e)
        => ((TextBox)sender).ScrollToEnd();
}
