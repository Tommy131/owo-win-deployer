using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using WinDeploy.App.Services;
using WinDeploy.Core.I18n;

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

        // Language: saved choice wins; on first run follow the Windows UI language (de/zh, else en),
        // then persist it. Seed the S.* string resources BEFORE base.OnStartup creates MainWindow,
        // so every {DynamicResource S.*} resolves on first layout.
        var lang = settings.Language ?? Lang.FromCulture(CultureInfo.CurrentUICulture);
        if (settings.Language is null) { settings.Language = lang; SettingsStore.Save(settings); }
        Localizer.SetLanguage(lang);
        LocalizationManager.Apply();

        ThemeManager.Apply(ThemeManager.Parse(settings.Theme));

        // Route downloads through the saved proxy (no-op / system default when disabled).
        WinDeploy.Core.Net.HttpProxy.Apply(settings.ProxyEnabled, settings.ProxyUrl);

        // Start the background hardware-temperature watchdog (no-op unless enabled in settings).
        TempMonitor.Configure(TempMonitorConfig.From(settings));

        AuditLog.App($"应用启动 · 语言 {lang} · 主题 {settings.Theme ?? "system"}");
        base.OnStartup(e);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        LogCrash("UI", e.Exception);
        MessageBox.Show(
            Localizer.Format("crash.dialogBody", e.Exception.Message, e.Exception.GetType().FullName),
            Localizer.T("crash.dialogTitle"), MessageBoxButton.OK, MessageBoxImage.Error);
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
        TempMonitor.Stop();
        AuditLog.App("应用退出");
        base.OnExit(e);
    }
}
