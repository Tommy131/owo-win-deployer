using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace WinDeploy.App.Services.Terminal;

/// <summary>A real Windows pseudo-console (ConPTY) running a shell. Unlike redirected stdio, this gives the
/// child a genuine TTY, so interactive tools (ssh, read-host prompts, full-screen TUIs like vim/htop) behave
/// correctly. The PTY's output is a UTF-8 VT/ANSI stream — feed it to <see cref="VtScreen"/>; keystrokes are
/// written back as UTF-8. Requires Windows 10 1809+ (build 17763); older builds report <see cref="IsSupported"/>
/// false.</summary>
public sealed class PtySession : IDisposable
{
    [StructLayout(LayoutKind.Sequential)]
    private struct COORD { public short X; public short Y; }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern int CreatePseudoConsole(COORD size, SafeFileHandle hInput, SafeFileHandle hOutput, uint flags, out IntPtr phPC);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern int ResizePseudoConsole(IntPtr hPC, COORD size);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern void ClosePseudoConsole(IntPtr hPC);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CreatePipe(out SafeFileHandle hReadPipe, out SafeFileHandle hWritePipe, IntPtr attrs, int size);

    [StructLayout(LayoutKind.Sequential)]
    private struct STARTUPINFO
    {
        public int cb;
        public IntPtr lpReserved, lpDesktop, lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2;
        public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct STARTUPINFOEX { public STARTUPINFO StartupInfo; public IntPtr lpAttributeList; }
    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION { public IntPtr hProcess, hThread; public int dwProcessId, dwThreadId; }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool InitializeProcThreadAttributeList(IntPtr list, int count, int flags, ref IntPtr size);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool UpdateProcThreadAttribute(IntPtr list, uint flags, IntPtr attribute, IntPtr value, IntPtr cbSize, IntPtr prev, IntPtr ret);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern void DeleteProcThreadAttributeList(IntPtr list);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcess(string? app, StringBuilder cmd, IntPtr procAttrs, IntPtr threadAttrs,
        bool inherit, uint flags, IntPtr env, string? cwd, ref STARTUPINFOEX si, out PROCESS_INFORMATION pi);

    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint WaitForSingleObject(IntPtr h, uint ms);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetExitCodeProcess(IntPtr h, out uint code);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool TerminateProcess(IntPtr h, uint code);

    private const uint EXTENDED_STARTUPINFO_PRESENT = 0x00080000;
    private static readonly IntPtr PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE_HANDLE = (IntPtr)0x00020016;
    private const uint STILL_ACTIVE = 259;

    private IntPtr _hPC = IntPtr.Zero;
    private IntPtr _attrList = IntPtr.Zero;
    private PROCESS_INFORMATION _pi;
    private SafeFileHandle? _inWrite, _outRead;
    private FileStream? _inStream, _outStream;
    private int _exitedRaised;
    private volatile bool _disposed;

    /// <summary>Decoded UTF-8 output chunks (raised on a background reader thread).</summary>
    public event Action<string>? Output;
    /// <summary>Raised once when the shell process or the PTY output pipe ends.</summary>
    public event Action? Exited;

    public static bool IsSupported { get; } = SupportedCheck();
    private static bool SupportedCheck()
    {
        try { return Environment.OSVersion.Version.Build >= 17763; } catch { return false; }
    }

    public bool HasExited
    {
        get
        {
            try { return _pi.hProcess == IntPtr.Zero || (GetExitCodeProcess(_pi.hProcess, out var c) && c != STILL_ACTIVE); }
            catch { return true; }
        }
    }

    /// <summary>Launch <paramref name="commandLine"/> attached to a fresh pseudo-console of the given size.</summary>
    public void Start(string commandLine, string workingDir, short cols, short rows)
    {
        cols = Math.Max((short)2, cols);
        rows = Math.Max((short)1, rows);

        // The PTY reads our keystrokes from inRead and writes its screen output to outWrite; we keep the
        // opposite ends (inWrite to type, outRead to read). The console dups what it needs, so we drop our
        // copies of the sides it owns right after creating it.
        if (!CreatePipe(out var inRead, out var inWrite, IntPtr.Zero, 0)) throw new IOException("CreatePipe(in) failed");
        if (!CreatePipe(out var outRead, out var outWrite, IntPtr.Zero, 0)) throw new IOException("CreatePipe(out) failed");
        _inWrite = inWrite;
        _outRead = outRead;

        var hr = CreatePseudoConsole(new COORD { X = cols, Y = rows }, inRead, outWrite, 0, out _hPC);
        inRead.Dispose();
        outWrite.Dispose();
        if (hr != 0) throw new IOException($"CreatePseudoConsole failed (0x{hr:X8})");

        var si = new STARTUPINFOEX();
        si.StartupInfo.cb = Marshal.SizeOf<STARTUPINFOEX>();
        IntPtr size = IntPtr.Zero;
        InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size);   // sizing call (returns false by design)
        _attrList = Marshal.AllocHGlobal(size);
        si.lpAttributeList = _attrList;
        if (!InitializeProcThreadAttributeList(_attrList, 1, 0, ref size))
            throw new IOException("InitializeProcThreadAttributeList failed: " + Marshal.GetLastWin32Error());
        if (!UpdateProcThreadAttribute(_attrList, 0, PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE_HANDLE, _hPC, (IntPtr)IntPtr.Size, IntPtr.Zero, IntPtr.Zero))
            throw new IOException("UpdateProcThreadAttribute failed: " + Marshal.GetLastWin32Error());

        if (!CreateProcess(null, new StringBuilder(commandLine), IntPtr.Zero, IntPtr.Zero, false,
                EXTENDED_STARTUPINFO_PRESENT, IntPtr.Zero, string.IsNullOrEmpty(workingDir) ? null : workingDir, ref si, out _pi))
            throw new IOException("CreateProcess failed: " + Marshal.GetLastWin32Error());

        _inStream = new FileStream(_inWrite, FileAccess.Write);
        _outStream = new FileStream(_outRead, FileAccess.Read);

        new Thread(ReadLoop) { IsBackground = true, Name = "PtyReader" }.Start();
        new Thread(WaitLoop) { IsBackground = true, Name = "PtyWait" }.Start();
    }

    private void ReadLoop()
    {
        var buf = new byte[8192];
        var dec = Encoding.UTF8.GetDecoder();
        var chars = new char[16384];
        try
        {
            int n;
            while (_outStream != null && (n = _outStream.Read(buf, 0, buf.Length)) > 0)
            {
                var c = dec.GetChars(buf, 0, n, chars, 0, false);   // keeps partial multi-byte runes across reads
                if (c > 0) Output?.Invoke(new string(chars, 0, c));
            }
        }
        catch { /* pipe closed on exit */ }
        RaiseExited();
    }

    private void WaitLoop()
    {
        try { if (_pi.hProcess != IntPtr.Zero) WaitForSingleObject(_pi.hProcess, 0xFFFFFFFF); } catch { }
        RaiseExited();
    }

    private void RaiseExited()
    {
        if (Interlocked.Exchange(ref _exitedRaised, 1) == 0 && !_disposed) Exited?.Invoke();
    }

    /// <summary>Write UTF-8 keystrokes / paste text to the PTY input.</summary>
    public void Write(string s)
    {
        try
        {
            var b = Encoding.UTF8.GetBytes(s);
            _inStream?.Write(b, 0, b.Length);
            _inStream?.Flush();
        }
        catch { /* closed */ }
    }

    public void Resize(short cols, short rows)
    {
        if (_hPC == IntPtr.Zero) return;
        try { ResizePseudoConsole(_hPC, new COORD { X = Math.Max((short)2, cols), Y = Math.Max((short)1, rows) }); }
        catch { /* ignore transient */ }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { if (_pi.hProcess != IntPtr.Zero && !HasExited) TerminateProcess(_pi.hProcess, 0); } catch { }
        try { if (_hPC != IntPtr.Zero) ClosePseudoConsole(_hPC); } catch { }   // also unblocks the reader
        try { _inStream?.Dispose(); } catch { }
        try { _outStream?.Dispose(); } catch { }
        try { if (_attrList != IntPtr.Zero) { DeleteProcThreadAttributeList(_attrList); Marshal.FreeHGlobal(_attrList); } } catch { }
        try { if (_pi.hThread != IntPtr.Zero) CloseHandle(_pi.hThread); } catch { }
        try { if (_pi.hProcess != IntPtr.Zero) CloseHandle(_pi.hProcess); } catch { }
        _hPC = IntPtr.Zero;
        _attrList = IntPtr.Zero;
        _pi = default;
    }
}
