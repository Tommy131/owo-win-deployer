using System.Windows.Controls;

namespace WinDeploy.App.Views.Ftp;

public partial class FtpServerView : UserControl
{
    public FtpServerView()
    {
        InitializeComponent();
        // Keep the log scrolled to the newest line.
        LogBox.TextChanged += (_, _) => LogBox.ScrollToEnd();
    }
}
