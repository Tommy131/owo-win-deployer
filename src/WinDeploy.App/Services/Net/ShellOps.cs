using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace WinDeploy.App.Services.Net;

/// <summary>Shell helpers mirroring Task Manager's row actions: reveal a file in Explorer ("打开文件位置")
/// and open the Windows file-properties dialog ("属性").</summary>
public static class ShellOps
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHELLEXECUTEINFO
    {
        public int cbSize;
        public uint fMask;
        public IntPtr hwnd;
        public string? lpVerb;
        public string? lpFile;
        public string? lpParameters;
        public string? lpDirectory;
        public int nShow;
        public IntPtr hInstApp;
        public IntPtr lpIDList;
        public string? lpClass;
        public IntPtr hkeyClass;
        public uint dwHotKey;
        public IntPtr hIcon;
        public IntPtr hProcess;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShellExecuteEx(ref SHELLEXECUTEINFO lpExecInfo);

    private const uint SEE_MASK_INVOKEIDLIST = 0x0000000C;
    private const int SW_SHOW = 5;

    /// <summary>Open Explorer with the file selected (or just the containing folder). Returns false if the
    /// path is empty / missing.</summary>
    public static bool RevealInExplorer(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        try
        {
            if (File.Exists(path))
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
                return true;
            }
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
            {
                Process.Start(new ProcessStartInfo(dir) { UseShellExecute = true });
                return true;
            }
        }
        catch { /* ignore */ }
        return false;
    }

    /// <summary>Show the Windows shell "属性" (Properties) dialog for a file, exactly like Task Manager's
    /// right-click → 属性. Returns false if the path is empty / missing or the shell call fails.</summary>
    public static bool ShowProperties(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return false;
        try
        {
            var info = new SHELLEXECUTEINFO
            {
                cbSize = Marshal.SizeOf<SHELLEXECUTEINFO>(),
                fMask = SEE_MASK_INVOKEIDLIST,
                lpVerb = "properties",
                lpFile = path,
                nShow = SW_SHOW,
            };
            return ShellExecuteEx(ref info);
        }
        catch { return false; }
    }
}
