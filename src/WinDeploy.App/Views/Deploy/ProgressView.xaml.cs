using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows.Controls;
using System.Windows.Threading;
using WinDeploy.App.ViewModels;

namespace WinDeploy.App.Views.Deploy;

public partial class ProgressView : UserControl
{
    private ProgressViewModel? _vm;

    public ProgressView()
    {
        InitializeComponent();
        DataContextChanged += (_, e) => Hook(e.NewValue as ProgressViewModel);
        Loaded += (_, _) => { Hook(DataContext as ProgressViewModel); ScrollTasksToEnd(); ScrollLogToEnd(); };
    }

    private void Hook(ProgressViewModel? vm)
    {
        if (ReferenceEquals(_vm, vm)) return;
        if (_vm != null) { _vm.Items.CollectionChanged -= OnItemsChanged; _vm.PropertyChanged -= OnVmPropertyChanged; }
        _vm = vm;
        if (_vm != null) { _vm.Items.CollectionChanged += OnItemsChanged; _vm.PropertyChanged += OnVmPropertyChanged; }
    }

    // A new task row was added (排队 / 运行中) → keep the newest task in view.
    private void OnItemsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action is NotifyCollectionChangedAction.Add or NotifyCollectionChangedAction.Reset)
            ScrollTasksToEnd();
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ProgressViewModel.Log)) ScrollLogToEnd();
    }

    // Scroll after layout so the just-added row is measured first.
    private void ScrollTasksToEnd()
        => Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => TaskScroller?.ScrollToEnd()));

    private void ScrollLogToEnd()
        => Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => LogScroller?.ScrollToEnd()));
}
