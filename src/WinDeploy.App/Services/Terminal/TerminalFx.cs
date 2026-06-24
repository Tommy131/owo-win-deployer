namespace WinDeploy.App.Services.Terminal;

/// <summary>Central, persisted terminal-effect settings, managed from the Settings page and applied live by the
/// terminal view. Three independent toggles — hacker palette, CRT (scanlines + flicker), background code-rain —
/// plus the code-rain opacity (so it never drowns the foreground) and scroll speed.</summary>
public static class TerminalFx
{
    public static bool Hacker { get; private set; } = true;
    public static bool Crt { get; private set; } = true;
    public static bool CodeRain { get; private set; } = true;
    public static double CodeOpacity { get; private set; } = 0.4;   // 0.05–1.0
    public static double Speed { get; private set; } = 1.0;          // 0.2–4.0 multiplier

    /// <summary>Raised after any value changes (and is persisted) so the terminal re-applies live.</summary>
    public static event Action? Changed;

    static TerminalFx() => Reload();

    public static void Reload()
    {
        var s = SettingsStore.Load();
        Hacker = s.TerminalHackerFx ?? true;
        Crt = s.TerminalCrtFx ?? true;
        CodeRain = s.TerminalCodeRain ?? true;
        CodeOpacity = Clamp(s.TerminalCodeOpacity ?? 0.4, 0.05, 1.0);
        Speed = Clamp(s.TerminalCodeSpeed ?? 1.0, 0.2, 4.0);
    }

    public static void SetHacker(bool v) => Mutate(() => Hacker = v, s => s.TerminalHackerFx = v);
    public static void SetCrt(bool v) => Mutate(() => Crt = v, s => s.TerminalCrtFx = v);
    public static void SetCodeRain(bool v) => Mutate(() => CodeRain = v, s => s.TerminalCodeRain = v);

    public static void SetCodeOpacity(double v)
    {
        v = Clamp(v, 0.05, 1.0);
        Mutate(() => CodeOpacity = v, s => s.TerminalCodeOpacity = v);
    }

    public static void SetSpeed(double v)
    {
        v = Clamp(v, 0.2, 4.0);
        Mutate(() => Speed = v, s => s.TerminalCodeSpeed = v);
    }

    private static void Mutate(Action apply, Action<AppSettings> persist)
    {
        apply();
        try { var s = SettingsStore.Load(); persist(s); SettingsStore.Save(s); } catch { /* best effort */ }
        Changed?.Invoke();
    }

    private static double Clamp(double v, double lo, double hi) => Math.Max(lo, Math.Min(hi, v));
}
