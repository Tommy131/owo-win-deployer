using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;

namespace WinDeploy.App.Services.Infra;

public enum ThemeMode { System, Light, Dark }

/// <summary>
/// Live theme switching. ThemeManager replaces the app's brush resources wholesale on every
/// change; because every usage is a DynamicResource, controls re-resolve and recolor instantly.
/// It also flips the native (DWM) title-bar between light/dark so the OS window chrome matches.
/// </summary>
public static class ThemeManager
{
    // (resource key, light hex, dark hex)
    private static readonly (string Key, string Light, string Dark)[] Palette =
    {
        ("PageBg",        "#F5F5F2", "#1E1E1E"),
        ("CardBg",        "#FFFFFF", "#2A2A2A"),
        ("BorderSoft",    "#E6E6E1", "#3A3A38"),
        ("BorderStrong",  "#D6D6D0", "#4A4A47"),
        ("TextPrimary",   "#1B1B1A", "#ECECEC"),
        ("TextSecondary", "#6E6E69", "#A8A8A2"),
        ("TextTertiary",  "#9A9A93", "#7A7A74"),
        ("Accent",        "#185FA5", "#5BA3E8"),
        ("AccentBg",      "#E6F1FB", "#14324A"),
        ("OkFg",          "#3B6D11", "#9FD069"),
        ("OkBg",          "#EAF3DE", "#1F3010"),
        ("FailFg",        "#A32D2D", "#E88A8A"),
        ("FailBg",        "#FCEBEB", "#3A1515"),
        ("WarnFg",        "#9A5B00", "#E0A24E"),
        ("WarnBg",        "#FBEFDD", "#3A2A12"),
        ("NavBg",         "#FBFBF9", "#232323"),
    };

    private static bool _dark;

    public static void Apply(ThemeMode mode)
    {
        var res = Application.Current?.Resources;
        if (res == null) return;

        _dark = mode == ThemeMode.Dark || (mode == ThemeMode.System && IsSystemDark());
        foreach (var (key, light, darkHex) in Palette)
        {
            var color = (Color)ColorConverter.ConvertFromString(_dark ? darkHex : light);
            // Replace the brush wholesale (new, unfrozen); DynamicResource usages re-resolve.
            res[key] = new SolidColorBrush(color);
        }

        // Recolor every open window's native title bar to match.
        if (Application.Current?.Windows is { } windows)
            foreach (Window w in windows) ApplyTitleBar(w);
    }

    /// <summary>Flip a single window's native title bar to the current theme. Safe to call any time;
    /// re-applied on SourceInitialized so windows opened after Apply() still match.</summary>
    public static void ApplyTitleBar(Window? w)
    {
        if (w == null) return;
        try
        {
            var hwnd = new WindowInteropHelper(w).Handle;
            if (hwnd == IntPtr.Zero) return;
            int on = _dark ? 1 : 0;
            // DWMWA_USE_IMMERSIVE_DARK_MODE = 20 (Win10 20H1+/Win11). 19 was the pre-release id.
            if (DwmSetWindowAttribute(hwnd, 20, ref on, sizeof(int)) != 0)
                DwmSetWindowAttribute(hwnd, 19, ref on, sizeof(int));
        }
        catch { /* DWM unavailable — leave default chrome */ }
    }

    public static ThemeMode Parse(string? s) => s switch
    {
        "dark" => ThemeMode.Dark,
        "light" => ThemeMode.Light,
        _ => ThemeMode.System,
    };

    private static bool IsSystemDark()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            if (k?.GetValue("AppsUseLightTheme") is int v) return v == 0;
        }
        catch { /* default light */ }
        return false;
    }

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);
}
