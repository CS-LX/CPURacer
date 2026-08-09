using System.Globalization;
using System.Resources;

namespace CPURacer.Localization;

/// <summary>Strongly-typed access to Strings*.resx (en default, zh-Hans satellite).</summary>
public static class Strings
{
    private static readonly ResourceManager ResourceManager =
        new("CPURacer.Localization.Strings", typeof(Strings).Assembly);

    private static CultureInfo? _culture;

    public static CultureInfo? Culture
    {
        get => _culture;
        set => _culture = value;
    }

    public static string PromptIdle => Get(nameof(PromptIdle));
    public static string PromptGameOver => Get(nameof(PromptGameOver));
    public static string PromptWaitingChart => Get(nameof(PromptWaitingChart));
    public static string PromptCaptureFail => Get(nameof(PromptCaptureFail));
    public static string HudRacing => Get(nameof(HudRacing));
    public static string HudFlipped => Get(nameof(HudFlipped));
    public static string TrayStart => Get(nameof(TrayStart));
    public static string TrayStop => Get(nameof(TrayStop));
    public static string TrayRestart => Get(nameof(TrayRestart));
    public static string TrayExit => Get(nameof(TrayExit));
    public static string TrayAdvanced => Get(nameof(TrayAdvanced));
    public static string TrayPauseWatch => Get(nameof(TrayPauseWatch));
    public static string TrayResumeWatch => Get(nameof(TrayResumeWatch));
    public static string TrayFollowMode => Get(nameof(TrayFollowMode));
    public static string TrayFollowExternal => Get(nameof(TrayFollowExternal));
    public static string TrayFollowChild => Get(nameof(TrayFollowChild));
    public static string TrayManualOverlay => Get(nameof(TrayManualOverlay));
    public static string TrayManualOverlayCancel => Get(nameof(TrayManualOverlayCancel));
    public static string TrayDebugChrome => Get(nameof(TrayDebugChrome));
    public static string TrayDebugFit => Get(nameof(TrayDebugFit));
    public static string TrayStatusPrefix => Get(nameof(TrayStatusPrefix));
    public static string TrayStatusPaused => Get(nameof(TrayStatusPaused));
    public static string TrayLanguage => Get(nameof(TrayLanguage));
    public static string TrayLanguageEn => Get(nameof(TrayLanguageEn));
    public static string TrayLanguageZh => Get(nameof(TrayLanguageZh));
    public static string TipPausedWatch => Get(nameof(TipPausedWatch));
    public static string TipOpenCpu => Get(nameof(TipOpenCpu));
    public static string TipSpaceStart => Get(nameof(TipSpaceStart));
    public static string TipRacing => Get(nameof(TipRacing));
    public static string TipGameOver => Get(nameof(TipGameOver));
    public static string MsgWatchPaused => Get(nameof(MsgWatchPaused));
    public static string MsgNeedCpuChart => Get(nameof(MsgNeedCpuChart));
    public static string MsgAdminUipi => Get(nameof(MsgAdminUipi));
    public static string MsgTrackNativeMissing => Get(nameof(MsgTrackNativeMissing));

    private static string Get(string name)
        => ResourceManager.GetString(name, _culture ?? Locale.Culture) ?? name;
}
