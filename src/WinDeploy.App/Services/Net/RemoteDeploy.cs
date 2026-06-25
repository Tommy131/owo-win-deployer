using System.Text;
using WinDeploy.Core.Util;

namespace WinDeploy.App.Services.Net;

/// <summary>Pushes the local repo to another machine and runs a deploy command there, over the built-in
/// Windows OpenSSH client (ssh.exe / scp.exe) using the user's existing SSH key — so no passwords are ever
/// handled in-app. The remote must have an SSH server and accept the key (see the SSH-setup card).</summary>
public static class RemoteDeploy
{
    /// <summary>Non-interactive connectivity probe (key auth only; never prompts for a password).</summary>
    public static async Task<(bool Ok, string Output)> TestAsync(string host, string user, int port, CancellationToken ct = default)
    {
        var r = await Proc.RunAsync("ssh", new[]
        {
            "-p", port.ToString(), "-o", "BatchMode=yes", "-o", "ConnectTimeout=8",
            "-o", "StrictHostKeyChecking=accept-new", $"{user}@{host}", "echo windeploy-ok",
        }, ct: ct);
        var ok = r.Ok && r.StdOut.Contains("windeploy-ok");
        return (ok, (r.StdOut + r.StdErr).Trim());
    }

    /// <summary>scp the local repo to <paramref name="remoteDir"/>, then ssh-run <paramref name="command"/>.
    /// Returns success plus a combined transcript for display.</summary>
    public static async Task<(bool Ok, string Output)> DeployAsync(
        string host, string user, int port, string localRepo, string remoteDir, string command, CancellationToken ct = default)
    {
        var target = $"{user}@{host}";
        var log = new StringBuilder();

        log.AppendLine($"$ scp -r \"{localRepo}\" {target}:\"{remoteDir}\"");
        var scp = await Proc.RunAsync("scp", new[]
        {
            "-r", "-P", port.ToString(), "-o", "StrictHostKeyChecking=accept-new", localRepo, $"{target}:{remoteDir}",
        }, ct: ct);
        log.AppendLine((scp.StdOut + scp.StdErr).Trim());
        if (!scp.Ok) { log.AppendLine($"scp exit {scp.ExitCode}"); return (false, log.ToString()); }

        log.AppendLine().AppendLine($"$ ssh {target} {command}");
        var ssh = await Proc.RunAsync("ssh", new[]
        {
            "-p", port.ToString(), "-o", "StrictHostKeyChecking=accept-new", target, command,
        }, ct: ct);
        log.AppendLine((ssh.StdOut + ssh.StdErr).Trim());
        log.AppendLine($"exit {ssh.ExitCode}");
        return (ssh.Ok, log.ToString());
    }
}
