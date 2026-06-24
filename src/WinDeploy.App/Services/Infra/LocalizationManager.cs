using System.Windows;
using WinDeploy.Core.I18n;

namespace WinDeploy.App.Services.Infra;

/// <summary>
/// Live language switching for the WPF layer. Mirrors <see cref="ThemeManager"/>: it pushes every
/// translated string into <c>Application.Current.Resources["S.&lt;key&gt;"]</c>, so any XAML that uses
/// <c>{DynamicResource S.&lt;key&gt;}</c> re-resolves instantly when the language changes — no restart.
///
/// Imperative text (ViewModels, dialogs, MessageBox) calls <see cref="Localizer.T"/> /
/// <see cref="Localizer.Format"/> directly; long-lived ViewModels refresh via the
/// <see cref="Localizer.CultureChanged"/> event (see <c>LocalizedObject</c>).
/// </summary>
public static class LocalizationManager
{
    /// <summary>Seed / refresh the <c>S.*</c> string resources from the current language.</summary>
    public static void Apply()
    {
        var res = Application.Current?.Resources;
        if (res == null) return;
        foreach (var key in Localizer.AllKeys())
            res["S." + key] = Localizer.T(key);
    }

    /// <summary>Switch language and update both the XAML resource strings and (via the event) ViewModels.</summary>
    public static void SetLanguage(string code)
    {
        Localizer.SetLanguage(code);   // raises CultureChanged → ViewModels refresh
        Apply();                       // rewrites S.* → every DynamicResource usage updates live
    }
}
