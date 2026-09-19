using Modbot.Companion.Pairing;
using Modbot.Companion.Startup;

namespace Modbot.Companion.Tests.Startup;

/// <summary>
/// Restarting: which address means "start a fresh copy", and the fresh copy waiting for the one it
/// is replacing to go before it takes over.
/// </summary>
public class CompanionRestartTests
{
    [Theory]
    [InlineData("modbot-companion://restart")]
    [InlineData("MODBOT-COMPANION://RESTART")]
    [InlineData("modbot-companion://restart/")]
    [InlineData("  modbot-companion://restart  ")]
    public void TheRestartAddressIsRecognised(string argument)
    {
        Assert.True(CompanionRestart.IsRestartLink(argument));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("show")]
    [InlineData("--autostart")]
    [InlineData("modbot-companion://pair?token=abc")]
    [InlineData("modbot-companion://restart-everything")]
    [InlineData("https://my.modbot.co/go?redir=/pair")]
    public void NothingElseIs(string? argument)
    {
        Assert.False(CompanionRestart.IsRestartLink(argument));
    }

    [Fact]
    public void ItIsStillALinkAsFarAsTheCommandLineIsConcerned()
    {
        // Windows hands it over the same way it hands over a pairing link, so the first check the
        // client makes still finds it; it is told apart afterwards.
        Assert.True(PairingToken.LooksLikeLink(CompanionRestart.Link));
    }

    [Fact]
    public void WaitsForTheOldCopyToLetGoAndThenTakesOver()
    {
        // A semaphore with nothing in it stands in for the lock the old copy is still holding.
        using var singleCopy = new Semaphore(0, 1);

        Assert.False(CompanionRestart.WaitForTheOldCopyToGo(singleCopy, TimeSpan.FromMilliseconds(50)));

        singleCopy.Release();

        Assert.True(CompanionRestart.WaitForTheOldCopyToGo(singleCopy, TimeSpan.FromMilliseconds(50)));
    }

    [Fact]
    public void ALockLeftBehindByACopyThatDiedCountsAsGone()
    {
        using var singleCopy = new Mutex(initiallyOwned: false);

        // Taken and never given back, which is what a copy that was killed leaves behind.
        var died = new Thread(() => singleCopy.WaitOne());
        died.Start();
        died.Join();

        Assert.True(CompanionRestart.WaitForTheOldCopyToGo(singleCopy, TimeSpan.FromMilliseconds(50)));
    }

    [Fact]
    public void TheWaitIsLongEnoughForACopyThatIsClosingASteamVrOverlay()
    {
        Assert.True(CompanionRestart.HowLongToWait >= TimeSpan.FromSeconds(10));
    }
}
