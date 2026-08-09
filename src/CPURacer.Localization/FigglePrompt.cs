using System.Text.RegularExpressions;
using Figgle.Fonts;

namespace CPURacer.Localization;

/// <summary>Expands <c>&lt;figgle&gt;TEXT&lt;/figgle&gt;</c> tags to FIGlet ASCII via Figgle.</summary>
public static partial class FigglePrompt
{
    [GeneratedRegex(@"<figgle>(.*?)</figgle>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex FiggleTagRegex();

    public static string Expand(string template)
    {
        if (string.IsNullOrEmpty(template))
        {
            return string.Empty;
        }

        return FiggleTagRegex().Replace(template, static m =>
        {
            var text = m.Groups[1].Value.Trim();
            if (text.Length == 0)
            {
                return string.Empty;
            }

            // Figgle.Fonts ships Standard among built-in fonts.
            var art = FiggleFonts.Standard.Render(text);
            return art.TrimEnd('\r', '\n');
        });
    }

    /// <summary>Formats a localized template then expands figgle tags.</summary>
    public static string FormatExpand(string template, params object[] args)
    {
        var formatted = args.Length == 0 ? template : string.Format(Locale.Culture, template, args);
        return Expand(formatted);
    }
}
