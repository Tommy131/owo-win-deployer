using System.Diagnostics;
using System.Text;

namespace WinDeploy.Core.Util;

public sealed record ProcResult(int ExitCode, string StdOut, string StdErr)
{
    public bool Ok => ExitCode == 0;
}

/// <summary>Thin async process runner. Resolves bare commands on PATH and wraps .cmd/.bat/.ps1.</summary>
public static class Proc
{
    public static async Task<ProcResult> RunAsync(string file, IEnumerable<string> args,
        string? cwd = null, CancellationToken ct = default)
    {
        var psi = BuildPsi(file, args, cwd);
        using var p = new Process { StartInfo = psi };
        var so = new StringBuilder();
        var se = new StringBuilder();
        p.OutputDataReceived += (_, e) => { if (e.Data != null) so.AppendLine(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data != null) se.AppendLine(e.Data); };
        p.Start();
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        try
        {
            await p.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            try { p.Kill(entireProcessTree: true); } catch { /* already gone */ }
            throw;
        }
        // WaitForExitAsync returns on process exit, but the async OutputDataReceived/ErrorDataReceived events
        // may still be in flight — a fast process that dumps its output then exits (e.g. smartctl) can otherwise
        // be read back EMPTY. The parameterless WaitForExit() blocks until those handlers have flushed.
        p.WaitForExit();
        return new ProcResult(p.ExitCode, so.ToString(), se.ToString());
    }

    /// <summary>Like <see cref="RunAsync"/> but reads stdout/stderr char-by-char, flushing on \r OR \n,
    /// invoking <paramref name="onToken"/> per token — so in-place progress bars (winget) are captured.</summary>
    public static async Task<ProcResult> RunStreamingAsync(string file, IEnumerable<string> args,
        Action<string>? onToken, string? cwd = null, CancellationToken ct = default)
    {
        var psi = BuildPsi(file, args, cwd);
        using var p = new Process { StartInfo = psi };
        var so = new StringBuilder();
        var se = new StringBuilder();
        p.Start();
        var pumps = new[] { Pump(p.StandardOutput, so), Pump(p.StandardError, se) };
        try
        {
            await p.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            try { p.Kill(entireProcessTree: true); } catch { /* already gone */ }
            throw;
        }
        await Task.WhenAll(pumps);
        return new ProcResult(p.ExitCode, so.ToString(), se.ToString());

        async Task Pump(System.IO.StreamReader reader, StringBuilder sink)
        {
            var buf = new char[512];
            var token = new StringBuilder();
            int n;
            while ((n = await reader.ReadAsync(buf, 0, buf.Length)) > 0)
            {
                for (var i = 0; i < n; i++)
                {
                    var c = buf[i];
                    if (c is '\r' or '\n')
                    {
                        if (token.Length > 0) { var t = token.ToString(); sink.Append(t).Append('\n'); onToken?.Invoke(t); token.Clear(); }
                    }
                    else token.Append(c);
                }
            }
            if (token.Length > 0) { var t = token.ToString(); sink.Append(t); onToken?.Invoke(t); }
        }
    }

    private static ProcessStartInfo BuildPsi(string file, IEnumerable<string> args, string? cwd)
    {
        // Resolve a bare command name (e.g. "winget") to a full path on PATH.
        if (!Path.IsPathRooted(file) && string.IsNullOrEmpty(Path.GetExtension(file)))
            file = CommandFinder.Find(file) ?? file;

        var ext = Path.GetExtension(file);
        ProcessStartInfo psi;

        if (ext.Equals(".cmd", StringComparison.OrdinalIgnoreCase) ||
            ext.Equals(".bat", StringComparison.OrdinalIgnoreCase))
        {
            psi = new ProcessStartInfo("cmd.exe");
            psi.ArgumentList.Add("/c");
            psi.ArgumentList.Add(file);
        }
        else if (ext.Equals(".ps1", StringComparison.OrdinalIgnoreCase))
        {
            psi = new ProcessStartInfo("powershell.exe");
            foreach (var a in new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", file })
                psi.ArgumentList.Add(a);
        }
        else
        {
            psi = new ProcessStartInfo(file);
        }

        foreach (var a in args) psi.ArgumentList.Add(a);
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;
        psi.StandardOutputEncoding = Encoding.UTF8; // winget/git emit UTF-8; needed for CJK names
        psi.StandardErrorEncoding = Encoding.UTF8;
        psi.UseShellExecute = false;
        psi.CreateNoWindow = true;
        if (cwd != null) psi.WorkingDirectory = cwd;
        return psi;
    }
}
