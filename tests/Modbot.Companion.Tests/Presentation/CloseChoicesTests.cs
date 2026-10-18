using Modbot.Companion.Presentation;

namespace Modbot.Companion.Tests.Presentation;

/// <summary>
/// What the window's X and Quit button do for each value of Close to tray.
/// </summary>
public class CloseChoicesTests
{
    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public void TheXHidesToTheTrayOnlyWhileCloseToTrayIsOn(bool closeToTray, bool hides)
        => Assert.Equal(hides, CloseChoices.XHidesToTray(closeToTray));

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public void QuitAsksFirstOnlyWhileCloseToTrayIsOn(bool closeToTray, bool asks)
        => Assert.Equal(asks, CloseChoices.QuitAsksFirst(closeToTray));

    [Fact]
    public void TheQuestionIsTheAgreedWording()
        => Assert.Equal("Modbot will stop reporting and won't stay in the tray.", CloseChoices.QuitQuestion);
}
