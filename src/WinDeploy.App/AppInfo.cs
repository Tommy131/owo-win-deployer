using System.Reflection;

namespace WinDeploy.App;

/// <summary>Central app identity: product name, semantic version (from the assembly &lt;Version&gt;), and the
/// GitHub repo used for the built-in self-update check.</summary>
public static class AppInfo
{
    public const string Name = "OwO! Win Deployer";

    /// <summary>Author / copyright holder.</summary>
    public const string Author = "Tommy131";
    public const string Copyright = "© 2026 Tommy131";

    /// <summary>owner/repo on GitHub — drives the self-update release check.</summary>
    public const string Repo = "Tommy131/owo-win-deployer";

    public static string Version
    {
        get
        {
            var v = Assembly.GetExecutingAssembly().GetName().Version;
            return v == null ? "1.0.0" : $"{v.Major}.{v.Minor}.{v.Build}";
        }
    }

    public static string TitleWithVersion => $"{Name} v{Version}";
}
