using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace WinDeploy.App.Services;

/// <summary>Extracts an application's real icon from its .exe (so installed apps show their actual icon
/// instead of a bundled favicon — works on any machine, no network). Tries the embedded icon first
/// (ExtractIconEx), then the Shell's associated icon (SHGetFileInfo) for files without an embedded one.</summary>
public static class IconExtractor
{
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern uint ExtractIconEx(string file, int index, IntPtr[] largeIcons, IntPtr[] smallIcons, uint count);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr handle);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEINFO
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string szTypeName;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfo(string pszPath, uint dwFileAttributes, ref SHFILEINFO psfi, uint cbFileInfo, uint uFlags);

    private const uint SHGFI_ICON = 0x000000100;
    private const uint SHGFI_LARGEICON = 0x000000000;

    /// <summary>The app's own EMBEDDED icon, or null. Embedded-only on purpose: it must never return the
    /// generic shell "application" icon, otherwise an installed console tool with no embedded icon (git,
    /// python, ffmpeg…) would override its nice bundled brand icon with a blank one.</summary>
    public static BitmapSource? FromExe(string? exe)
    {
        if (string.IsNullOrWhiteSpace(exe) || !File.Exists(exe)) return null;
        return FromEmbedded(exe);
    }

    /// <summary>Best-effort ANY icon: embedded, else the Shell's associated icon. For callers that have no
    /// bundled brand icon to protect (startup items / borrowing a running process's icon).</summary>
    public static BitmapSource? FromExeAnyIcon(string? exe)
    {
        if (string.IsNullOrWhiteSpace(exe) || !File.Exists(exe)) return null;
        return FromEmbedded(exe) ?? FromShell(exe);
    }

    private static BitmapSource? FromEmbedded(string exe)
    {
        var large = new IntPtr[1];
        try
        {
            var n = ExtractIconEx(exe, 0, large, new IntPtr[1], 1);
            if (n == 0 || large[0] == IntPtr.Zero) return null;
            return FromHIcon(large[0]);
        }
        catch { return null; }
        finally { if (large[0] != IntPtr.Zero) DestroyIcon(large[0]); }
    }

    private static BitmapSource? FromShell(string path)
    {
        var info = new SHFILEINFO();
        var hr = SHGetFileInfo(path, 0, ref info, (uint)Marshal.SizeOf(info), SHGFI_ICON | SHGFI_LARGEICON);
        if (hr == IntPtr.Zero || info.hIcon == IntPtr.Zero) return null;
        try { return FromHIcon(info.hIcon); }
        finally { DestroyIcon(info.hIcon); }
    }

    private static BitmapSource? FromHIcon(IntPtr hIcon)
    {
        try
        {
            var src = Imaging.CreateBitmapSourceFromHIcon(hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            src.Freeze();   // frozen → safe to create on a background thread and bind on the UI thread
            return src;
        }
        catch { return null; }
    }
}
