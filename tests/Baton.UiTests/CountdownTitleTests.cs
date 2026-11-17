using Baton.Core;
using Baton.App.Services;
using Baton.App.Views;

namespace Baton.UiTests;

/// <summary>The title of the end-of-queue window says what the text says: with a queue an agent started nothing happens by itself.</summary>
public sealed class CountdownTitleTests
{
    [Theory]
    [InlineData(AppLanguage.English, "Queue finished", "shortly")]
    [InlineData(AppLanguage.Italian, "Coda finita", "tra poco")]
    public void WhenAClickIsNeeded_TheTitleIsNeutral_NotShortly(AppLanguage language, string expected, string countdownWord)
    {
        try
        {
            Localizer.Apply(language);
            var title = CountdownWindow.TitleFor(CountdownWindow.ActionText(Baton.Core.EndAction.CloseApp), needsClick: true);
            Assert.Equal(expected, title);
            Assert.DoesNotContain(countdownWord, title);
            // the countdown that does go ahead by itself keeps announcing the action
            Assert.Contains(countdownWord, CountdownWindow.TitleFor(CountdownWindow.ActionText(Baton.Core.EndAction.CloseApp), needsClick: false));
        }
        finally
        {
            Localizer.Apply(AppLanguage.Italian); // what the other UI tests expect
        }
    }
}
