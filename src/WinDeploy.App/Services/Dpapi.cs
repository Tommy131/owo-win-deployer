using System.Runtime.InteropServices;
using System.Text;

namespace WinDeploy.App.Services;

/// <summary>Thin wrapper over Windows DPAPI (CurrentUser scope) via crypt32 P/Invoke — zero NuGet. Used to
/// encrypt saved FTP passwords at rest so they're recoverable only by the same Windows user on this machine.</summary>
internal static class Dpapi
{
    [StructLayout(LayoutKind.Sequential)]
    private struct DATA_BLOB { public int cbData; public IntPtr pbData; }

    [DllImport("crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(ref DATA_BLOB pDataIn, string? szDataDescr, IntPtr pOptionalEntropy,
        IntPtr pvReserved, IntPtr pPromptStruct, int dwFlags, ref DATA_BLOB pDataOut);

    [DllImport("crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(ref DATA_BLOB pDataIn, IntPtr ppszDataDescr, IntPtr pOptionalEntropy,
        IntPtr pvReserved, IntPtr pPromptStruct, int dwFlags, ref DATA_BLOB pDataOut);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr hMem);

    private const int CRYPTPROTECT_UI_FORBIDDEN = 0x1;

    /// <summary>Encrypt to a base64 string. Returns "" for empty input.</summary>
    public static string Protect(string? plain)
    {
        if (string.IsNullOrEmpty(plain)) return "";
        var data = Encoding.UTF8.GetBytes(plain);
        var h = GCHandle.Alloc(data, GCHandleType.Pinned);
        var outBlob = new DATA_BLOB();
        try
        {
            var inBlob = new DATA_BLOB { cbData = data.Length, pbData = h.AddrOfPinnedObject() };
            if (!CryptProtectData(ref inBlob, "WinDeployFtp", IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, CRYPTPROTECT_UI_FORBIDDEN, ref outBlob))
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            var outBytes = new byte[outBlob.cbData];
            Marshal.Copy(outBlob.pbData, outBytes, 0, outBlob.cbData);
            return Convert.ToBase64String(outBytes);
        }
        finally
        {
            if (h.IsAllocated) h.Free();
            if (outBlob.pbData != IntPtr.Zero) LocalFree(outBlob.pbData);
        }
    }

    /// <summary>Decrypt a base64 string produced by <see cref="Protect"/>. Returns "" on any failure.</summary>
    public static string Unprotect(string? b64)
    {
        if (string.IsNullOrEmpty(b64)) return "";
        byte[] data;
        try { data = Convert.FromBase64String(b64); } catch { return ""; }
        var h = GCHandle.Alloc(data, GCHandleType.Pinned);
        var outBlob = new DATA_BLOB();
        try
        {
            var inBlob = new DATA_BLOB { cbData = data.Length, pbData = h.AddrOfPinnedObject() };
            if (!CryptUnprotectData(ref inBlob, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, CRYPTPROTECT_UI_FORBIDDEN, ref outBlob))
                return "";
            var outBytes = new byte[outBlob.cbData];
            Marshal.Copy(outBlob.pbData, outBytes, 0, outBlob.cbData);
            return Encoding.UTF8.GetString(outBytes);
        }
        catch { return ""; }
        finally
        {
            if (h.IsAllocated) h.Free();
            if (outBlob.pbData != IntPtr.Zero) LocalFree(outBlob.pbData);
        }
    }
}
