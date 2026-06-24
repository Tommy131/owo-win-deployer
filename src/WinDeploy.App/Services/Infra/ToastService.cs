using System.IO;
using System.Security;
using Windows.Data.Xml.Dom;
using Windows.UI.Notifications;

namespace WinDeploy.App.Services.Infra;

/// <summary>Sends a modern Windows toast through the app's registered <see cref="AppUserModel.Aumid"/>, so the
/// notification is correctly attributed to "OwO! Win Deployer" with the app icon — unlike the legacy
/// <c>NotifyIcon</c> balloon, which the shell labels with an auto-generated AUMID and no app icon.</summary>
public static class ToastService
{
    /// <summary>Show a toast. Returns false if toasts are unavailable (old OS / policy) so the caller can fall
    /// back to a legacy balloon. Best-effort — never throws.</summary>
    public static bool TryShow(string title, string body)
    {
        try
        {
            var logo = AppUserModel.IconPath;
            var logoElement = logo != null && File.Exists(logo)
                ? $"<image placement=\"appLogoOverride\" src=\"{SecurityElement.Escape(logo)}\"/>"
                : "";

            var xml = new XmlDocument();
            xml.LoadXml($"""
                <toast>
                  <visual>
                    <binding template="ToastGeneric">
                      {logoElement}
                      <text>{SecurityElement.Escape(title)}</text>
                      <text>{SecurityElement.Escape(body)}</text>
                    </binding>
                  </visual>
                </toast>
                """);

            ToastNotificationManager.CreateToastNotifier(AppUserModel.Aumid).Show(new ToastNotification(xml));
            return true;
        }
        catch { return false; }
    }
}
