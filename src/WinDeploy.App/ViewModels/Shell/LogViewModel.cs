using System.Diagnostics;
using System.Windows;
using WinDeploy.App.Services;
using WinDeploy.Core.I18n;

namespace WinDeploy.App.ViewModels.Shell;

/// <summary>The "日志" page: shows the global audit log, follows new entries live,
/// and can open the log folder or clear the file.</summary>
public sealed class LogViewModel : LocalizedObject
{
    public RelayCommand RefreshCommand { get; }
    public RelayCommand OpenFolderCommand { get; }
    public RelayCommand ClearCommand { get; }

    public string FilePath => AuditLog.FilePath;

    public LogViewModel()
    {
        RefreshCommand = new RelayCommand(_ => Refresh());
        OpenFolderCommand = new RelayCommand(_ => OpenFolder());
        ClearCommand = new RelayCommand(_ => ClearWithConfirm());
        AuditLog.Logged += OnLogged;
        Refresh();
    }

    private string _text = "";
    public string Text { get => _text; private set => Set(ref _text, value); }

    private void Refresh() => Text = AuditLog.ReadTail();

    private void ClearWithConfirm()
    {
        if (Dialogs.Show(Localizer.T("log.clear.confirmBody"), Localizer.T("log.clear.confirmTitle"),
                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        AuditLog.Clear();
        Refresh();
    }

    private void OnLogged(string line)
    {
        var disp = Application.Current?.Dispatcher;
        if (disp != null && !disp.CheckAccess()) { disp.BeginInvoke(() => OnLogged(line)); return; }
        Text = string.IsNullOrEmpty(_text) ? line : _text + Environment.NewLine + line;
    }

    private void OpenFolder()
    {
        try { Process.Start(new ProcessStartInfo(AuditLog.Folder) { UseShellExecute = true }); }
        catch { /* ignore */ }
    }
}
