using System.Windows;
using System.Windows.Controls;
using ICSharpCode.AvalonEdit.Highlighting;
using WinDeploy.App.ViewModels;

namespace WinDeploy.App.Views.Server;

public partial class ServerDetailView : UserControl
{
    private ServerDetailViewModel? _vm;
    private bool _loading;

    public ServerDetailView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Editor.TextChanged += (_, _) => { if (_vm != null && !_loading) _vm.Editor = Editor.Text; };
        Loaded += (_, _) => _vm?.StartLive();
        Unloaded += (_, _) => _vm?.StopLive();
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_vm != null) _vm.FileOpened -= OnFileOpened;
        _vm = DataContext as ServerDetailViewModel;
        if (_vm != null)
        {
            _vm.FileOpened += OnFileOpened;
            if (IsLoaded) _vm.StartLive();
        }
    }

    private void OnFileOpened(string content, string path)
    {
        _loading = true;
        Editor.Text = content;
        Editor.SyntaxHighlighting = HighlightFor(path);
        _loading = false;
    }

    private static IHighlightingDefinition? HighlightFor(string path)
    {
        var ext = System.IO.Path.GetExtension(path).ToLowerInvariant();
        var name = ext switch
        {
            ".xml" => "XML",
            ".json" => "Json",
            ".ini" or ".conf" or ".cfg" => "INI",
            _ => null,
        };
        return name != null ? HighlightingManager.Instance.GetDefinition(name) : null;
    }
}
