namespace WinDeploy.App.Services.Sys;

/// <summary>Windows version gate. On .NET 5+ <see cref="Environment.OSVersion"/> returns the *real* build
/// (via RtlGetVersion, no app manifest needed), so we can hide features a given Windows build can't use.
/// The app itself is .NET 10 / WPF, which only runs on Windows 10 1607+; gating mainly hides Win11-only
/// (or newer-build-only) features when running on older Windows 10.</summary>
public static class OsInfo
{
    // Build numbers for the milestones we gate on.
    public const int Win10_1607 = 14393;   // WSL 引入
    public const int Win10_1809 = 17763;   // 深色模式
    public const int Win11_21H2 = 22000;   // Windows 11 / 经典右键菜单
    public const int Win11_22H2 = 22621;   // 任务栏时钟显秒

    public static Version Version { get; } = Environment.OSVersion.Version;
    public static int Build => Version.Build;
    public static bool IsWindows => OperatingSystem.IsWindows();
    public static bool IsWindows11 => IsWindows && Version.Major >= 10 && Build >= Win11_21H2;

    /// <summary>True when running on at least the given build. <paramref name="build"/> 0 = no requirement
    /// (always true), so features without a minimum still show on every OS.</summary>
    public static bool AtLeastBuild(int build)
        => build <= 0 || (IsWindows && Version.Major >= 10 && Build >= build);
}
