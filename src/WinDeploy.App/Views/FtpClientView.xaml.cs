using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using WinDeploy.App.ViewModels;

namespace WinDeploy.App.Views;

public partial class FtpClientView : UserControl
{
    private FtpClientViewModel? _vm;

    public FtpClientView()
    {
        InitializeComponent();
        // PasswordBox can't bind; push changes into the VM.
        PwBox.PasswordChanged += (_, _) => { if (DataContext is FtpClientViewModel vm) vm.Password = PwBox.Password; };
        LogBox.TextChanged += (_, _) => LogBox.ScrollToEnd();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_vm != null) _vm.PasswordFilled -= OnPasswordFilled;
        _vm = DataContext as FtpClientViewModel;
        if (_vm != null) _vm.PasswordFilled += OnPasswordFilled;
    }

    // When a saved profile is loaded, mirror its decrypted password into the PasswordBox.
    private void OnPasswordFilled(string pw) => PwBox.Password = pw;

    private void LocalGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is FtpClientViewModel vm && LocalGrid.SelectedItem is FtpLocalRowVm row)
            vm.OpenLocalCommand.Execute(row);
    }

    private void RemoteGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is FtpClientViewModel vm && RemoteGrid.SelectedItem is FtpRemoteRowVm row)
            vm.OpenRemoteCommand.Execute(row);
    }

    // Sync multi-selection to the VM so batch upload/download act on every highlighted row.
    private void LocalGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
        => (DataContext as FtpClientViewModel)?.SetLocalSelection(LocalGrid.SelectedItems);

    private void RemoteGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
        => (DataContext as FtpClientViewModel)?.SetRemoteSelection(RemoteGrid.SelectedItems);

    /// <summary>Right-clicking a row selects ONLY that row (clearing any previous selection), so the
    /// context-menu commands act on exactly the row under the cursor. Without the clear, the grid's Extended
    /// selection mode would accumulate a new highlighted row on every right-click.</summary>
    private void Grid_RightSelect(object sender, MouseButtonEventArgs e)
    {
        var dep = e.OriginalSource as DependencyObject;
        while (dep != null && dep is not DataGridRow) dep = VisualTreeHelper.GetParent(dep);
        if (dep is DataGridRow row && sender is DataGrid grid)
        {
            grid.UnselectAll();
            row.IsSelected = true;
        }
    }
}
