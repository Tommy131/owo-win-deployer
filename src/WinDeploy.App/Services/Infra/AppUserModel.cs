using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace WinDeploy.App.Services.Infra;

/// <summary>Gives the process a stable AppUserModelID and registers a friendly DisplayName (+ icon) for it.
/// Tray balloon tips are rendered as toasts on Windows 10/11, and the toast's attribution line comes from the
/// process AUMID — without an explicit one, Windows invents an ugly "NotifyIconGeneratedAumid_…" string. With
/// this registered, the toast (and taskbar grouping) is attributed to "OwO! Win Deployer".</summary>
public static class AppUserModel
{
    /// <summary>Stable, reverse-DNS-style id — kept constant across versions so taskbar pinning / toast
    /// identity persist.</summary>
    public const string Aumid = "Tommy131.OwODeployer";

    [DllImport("shell32.dll", PreserveSig = true)]
    private static extern int SetCurrentProcessExplicitAppUserModelID([MarshalAs(UnmanagedType.LPWStr)] string appID);

    /// <summary>Register the friendly name/icon and bind the AUMID to this process. Call once at startup,
    /// before any tray balloon / toast is shown. Best-effort — never throws.</summary>
    public static void Configure()
    {
        try { RegisterDisplayName(); } catch { /* registry may be locked down */ }
        try { SetCurrentProcessExplicitAppUserModelID(Aumid); } catch { /* old OS / policy */ }
    }

    private static void RegisterDisplayName()
    {
        using var key = Registry.CurrentUser.CreateSubKey($@"Software\Classes\AppUserModelId\{Aumid}");
        if (key == null) return;
        key.SetValue("DisplayName", AppInfo.Name, RegistryValueKind.String);
        if (IconPath is { } icon) key.SetValue("IconUri", icon, RegistryValueKind.String);
    }

    /// <summary>Path to a cached PNG of the app icon (extracted from the exe — single-file builds embed it,
    /// so it isn't on disk otherwise), used as the toast attribution icon and body logo. Null on failure.</summary>
    public static string? IconPath { get; } = EnsureIcon();

    private static string? EnsureIcon()
    {
        try
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinDeploy");
            Directory.CreateDirectory(dir);
            // PNG (not .ico) — recommended for toast images; the toast renderer handles it reliably.
            var pngPath = Path.Combine(dir, "app.png");
            if (!File.Exists(pngPath))
            {
                var exe = Environment.ProcessPath;
                if (string.IsNullOrEmpty(exe)) return null;
                using var ico = System.Drawing.Icon.ExtractAssociatedIcon(exe);
                if (ico == null) return null;
                using var bmp = ico.ToBitmap();
                bmp.Save(pngPath, System.Drawing.Imaging.ImageFormat.Png);
            }
            return pngPath;
        }
        catch { return null; }
    }
}
