using System.IO;
using System.Text.Json;

namespace WinDeploy.App.Services.Ftp;

/// <summary>A saved client login (FileZilla-style "site"). The password is stored DPAPI-encrypted, never clear.</summary>
public sealed class FtpClientProfile
{
    public string Name { get; set; } = "";
    public string Host { get; set; } = "";
    public int Port { get; set; } = 21;
    public string TlsMode { get; set; } = "explicit";   // none | explicit | implicit
    public string UserName { get; set; } = "";
    /// <summary>DPAPI-encrypted (current Windows user) base64; empty = no saved password.</summary>
    public string PasswordEnc { get; set; } = "";
}

/// <summary>Persists saved client logins to %LOCALAPPDATA%/WinDeploy/ftp-clients.json.</summary>
public static class FtpClientStore
{
    private static readonly string DirPath =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinDeploy");
    private static readonly string FilePathValue = Path.Combine(DirPath, "ftp-clients.json");
    private static readonly JsonSerializerOptions Opt = new() { WriteIndented = true };

    public static List<FtpClientProfile> Load()
    {
        try
        {
            if (File.Exists(FilePathValue))
                return JsonSerializer.Deserialize<List<FtpClientProfile>>(File.ReadAllText(FilePathValue)) ?? new();
        }
        catch { /* fall through */ }
        return new();
    }

    public static void Save(IEnumerable<FtpClientProfile> profiles)
    {
        try
        {
            Directory.CreateDirectory(DirPath);
            File.WriteAllText(FilePathValue, JsonSerializer.Serialize(profiles, Opt));
        }
        catch { /* best effort */ }
    }
}
