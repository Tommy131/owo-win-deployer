using System.Speech.Synthesis;
using WinDeploy.Core.I18n;

namespace WinDeploy.App.Services.Sys;

/// <summary>Text-to-speech via the Windows SAPI engine (<c>System.Speech</c>). Picks a voice matching the app's
/// current UI language (zh / en / de) when one is installed, otherwise speaks with the default voice — the text
/// is already localized, so the words are correct regardless. Best-effort: silently no-ops when no speech engine
/// or no voice is available. Speaks on a background thread; calls are serialized so alerts don't overlap.</summary>
public static class Tts
{
    private static readonly object Gate = new();

    /// <summary>Speak <paramref name="text"/> off the calling thread, in a voice matching the current UI language.</summary>
    public static void Speak(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        var lang = Localizer.Current;
        Task.Run(() =>
        {
            try
            {
                lock (Gate)   // System.Speech is not built for concurrent Speak — serialize alerts
                {
                    using var synth = new SpeechSynthesizer();
                    SelectVoice(synth, lang);
                    synth.Speak(text);
                }
            }
            catch { /* no engine / no installed voice — stay silent */ }
        });
    }

    private static void SelectVoice(SpeechSynthesizer synth, string lang)
    {
        var iso = lang switch { Lang.Zh => "zh", Lang.De => "de", _ => "en" };
        try
        {
            var match = synth.GetInstalledVoices()
                .Where(v => v.Enabled)
                .Select(v => v.VoiceInfo)
                .FirstOrDefault(vi => string.Equals(vi.Culture?.TwoLetterISOLanguageName, iso, StringComparison.OrdinalIgnoreCase));
            if (match != null) synth.SelectVoice(match.Name);
        }
        catch { /* keep the default voice */ }
    }
}
