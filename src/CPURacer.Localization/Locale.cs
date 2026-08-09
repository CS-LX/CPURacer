using System.Globalization;

namespace CPURacer.Localization;

/// <summary>Process-wide UI culture for player strings (en / zh-Hans).</summary>
public static class Locale
{
    public const string English = "en";
    public const string ChineseSimplified = "zh-Hans";

    private static CultureInfo _culture = ResolveFromOs();

    public static CultureInfo Culture
    {
        get => _culture;
        private set
        {
            _culture = value;
            Strings.Culture = value;
            CultureInfo.CurrentUICulture = value;
        }
    }

    public static bool IsChinese =>
        _culture.TwoLetterISOLanguageName.Equals("zh", StringComparison.OrdinalIgnoreCase);

    public static event Action? Changed;

    public static void ApplyFromOs() => SetCulture(ResolveFromOs());

    public static void SetEnglish() => SetCulture(CultureInfo.GetCultureInfo(English));

    public static void SetChinese() => SetCulture(CultureInfo.GetCultureInfo(ChineseSimplified));

    public static void SetCulture(CultureInfo culture)
    {
        ArgumentNullException.ThrowIfNull(culture);
        var normalized = Normalize(culture);
        if (string.Equals(_culture.Name, normalized.Name, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        Culture = normalized;
        Changed?.Invoke();
    }

    private static CultureInfo ResolveFromOs()
        => Normalize(CultureInfo.CurrentUICulture);

    private static CultureInfo Normalize(CultureInfo culture)
    {
        if (culture.TwoLetterISOLanguageName.Equals("zh", StringComparison.OrdinalIgnoreCase))
        {
            return CultureInfo.GetCultureInfo(ChineseSimplified);
        }

        return CultureInfo.GetCultureInfo(English);
    }
}
