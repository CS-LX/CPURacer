using System.Globalization;
using CPURacer.Localization;

namespace CPURacer.Tests;

public class LocalizationTests
{
    [Fact]
    public void GameOverReason_IsLocalizedAndInsertedIntoPrompt()
    {
        var originalCulture = Strings.Culture;
        try
        {
            Strings.Culture = CultureInfo.GetCultureInfo("en");
            var englishReason = Strings.DeathReasonLeftTrack;
            Assert.Equal("ran off the track", englishReason);
            Assert.Contains(
                englishReason,
                FigglePrompt.FormatExpand(Strings.PromptGameOverWithReason, 12d, 12d, 3, englishReason));

            Strings.Culture = CultureInfo.GetCultureInfo("zh-Hans");
            var chineseReason = Strings.DeathReasonLeftTrack;
            Assert.Equal("驶出赛道", chineseReason);
            Assert.Contains(
                chineseReason,
                FigglePrompt.FormatExpand(Strings.PromptGameOverWithReason, 12d, 12d, 3, chineseReason));
        }
        finally
        {
            Strings.Culture = originalCulture;
        }
    }
}
