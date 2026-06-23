using System.IO;
using System.Windows;
using System.Windows.Threading;
using WinDeploy.App.Services;

namespace WinDeploy.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        // Catch unhandled exceptions so a single page/feature fault logs an error and keeps the app alive,
        // instead of crashing the whole window to the desktop. The full stack is written to crash.log.
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, ex) => LogCrash("AppDomain", ex.ExceptionObject as Exception);
        System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (_, ex) => { LogCrash("Task", ex.Exception); ex.SetObserved(); };

        // Set a stable AppUserModelID first, so tray balloons (shown as toasts on Win10/11) are attributed to
        // "OwO! Win Deployer" rather than an auto-generated "NotifyIconGeneratedAumid_…" id.
        AppUserModel.Configure();
        var settings = SettingsStore.Load();
        ThemeManager.Apply(ThemeManager.Parse(settings.Theme));
        AuditLog.App($"应用启动 · 主题 {settings.Theme ?? "system"}");
        base.OnStartup(e);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        LogCrash("UI", e.Exception);
        MessageBox.Show(
            $"操作出错（已记录到 crash.log，应用将继续运行）：\n\n{e.Exception.Message}\n\n{e.Exception.GetType().FullName}",
            "出现错误", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;   // keep the app alive
    }

    private static void LogCrash(string source, Exception? ex)
    {
        if (ex == null) return;
        try { AuditLog.App($"未处理异常[{source}]：{ex.GetType().Name}: {ex.Message}"); } catch { }
        try
        {
            Directory.CreateDirectory(SettingsStore.Folder);
            File.AppendAllText(Path.Combine(SettingsStore.Folder, "crash.log"), $"==== {DateTime.Now:O} [{source}] ====\n{ex}\n\n");
        }
        catch { /* best effort */ }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        AuditLog.App("应用退出");
        base.OnExit(e);
    }
}
