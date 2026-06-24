using WinDeploy.Core.I18n;

namespace WinDeploy.App.Services.Software;

/// <summary>Result of an app self-update check.</summary>
public sealed record UpdateCheck(bool Available, string Latest, string Current, string HtmlUrl, string? Error);

/// <summary>Checks GitHub for a newer release of the app itself (<see cref="AppInfo.Repo"/>). Shared by the
/// startup check and the Settings「检查更新」button so the version logic lives in one place.</summary>
public static class SelfUpdate
{
    public static async Task<UpdateCheck> CheckAsync(bool force = false)
    {
        var cur = AppInfo.Version;
        var fallbackUrl = $"https://github.com/{AppInfo.Repo}/releases/latest";
        try
        {
            var rel = await GitHub.LatestReleaseAsync(AppInfo.Repo, force);
            if (rel == null) return new(false, "", cur, fallbackUrl, Localizer.T("update.noReleaseOrNet"));
            var latest = rel.Tag.TrimStart('v', 'V');
            var url = string.IsNullOrWhiteSpace(rel.HtmlUrl) ? fallbackUrl : rel.HtmlUrl;
            var available = !string.IsNullOrWhiteSpace(latest) && UpdateChecker.CompareSemver(latest, cur) > 0;
            return new(available, latest, cur, url, null);
        }
        catch (Exception ex) { return new(false, "", cur, fallbackUrl, ex.Message); }
    }
}
