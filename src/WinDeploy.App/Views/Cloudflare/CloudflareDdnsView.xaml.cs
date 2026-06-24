using System.Windows.Controls;
using WinDeploy.App.ViewModels;

namespace WinDeploy.App.Views.Cloudflare;

public partial class CloudflareDdnsView : UserControl
{
    public CloudflareDdnsView()
    {
        InitializeComponent();
        // PasswordBox can't bind: push edits into the VM, and pre-fill the decrypted token on load.
        TokenBox.PasswordChanged += (_, _) => { if (DataContext is CloudflareDdnsViewModel vm) vm.Token = TokenBox.Password; };
        DataContextChanged += (_, _) =>
        {
            if (DataContext is CloudflareDdnsViewModel vm && TokenBox.Password != vm.Token)
                TokenBox.Password = vm.Token;
        };
        // Lazily load the zone list the first time the page is shown.
        Loaded += (_, _) => (DataContext as CloudflareDdnsViewModel)?.Activate();
    }
}
