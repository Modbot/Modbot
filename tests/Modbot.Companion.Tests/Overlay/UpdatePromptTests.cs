using Modbot.Companion.Overlay;
using Modbot.TestSupport;

namespace Modbot.Companion.Tests.Overlay;

/// <summary>
/// The pop-up that asks whether to install a downloaded update, so nobody leaves VRChat to do it.
/// It asks once per version, Update asks twice like the sidebar's button, and Later leaves the
/// install to the next start.
/// </summary>
public class UpdatePromptTests
{
    private sealed class Rig
    {
        public FakeClock Clock { get; } = new();
        public PopUps PopUps { get; }
        public UpdatePrompt Prompt { get; }
        public int Restarts { get; private set; }
        public bool RestartWorks { get; set; } = true;

        public Rig()
        {
            PopUps = new PopUps(Clock) { Dwell = TimeSpan.FromSeconds(6) };
            Prompt = new UpdatePrompt(PopUps, Clock, () =>
            {
                Restarts++;
                return Task.FromResult(RestartWorks);
            });
        }

        public PopUp? Card => PopUps.Current().SingleOrDefault();
    }

    [Fact]
    public void NothingIsAskedUntilAnUpdateHasDownloaded()
    {
        var rig = new Rig();

        rig.Prompt.Tick(null);

        Assert.Null(rig.Card);
    }

    [Fact]
    public void ADownloadedUpdatePutsUpACardNamingTheVersionWithUpdateAndLater()
    {
        var rig = new Rig();

        rig.Prompt.Tick("2026.10.3");

        Assert.Equal("Update 2026.10.3 is ready", rig.Card!.Body);
        Assert.Equal(new PopUpAsk("Update", "Later"), rig.Card.Ask);
    }

    [Fact]
    public void TheCardStaysUntilItIsAnsweredNotJustForTheFewSecondsOfAnOrdinaryPopUp()
    {
        var rig = new Rig();
        rig.Prompt.Tick("2026.10.3");

        rig.Clock.Advance(TimeSpan.FromMinutes(10));

        Assert.NotNull(rig.Card);
    }

    [Fact]
    public void LeavingAnInstanceOrNewerCardsDoNotTakeTheQuestionAway()
    {
        var rig = new Rig();
        rig.PopUps.MostAtOnce = 2;
        rig.Prompt.Tick("2026.10.3");

        rig.PopUps.ClearAll();
        Assert.NotNull(rig.Card);

        for (var i = 0; i < 5; i++)
            rig.PopUps.Show(new PopUp("a" + i, "Cat Lounge", "Rin", null, PopUpTone.Plain));

        Assert.Contains(rig.PopUps.Current(), p => p.Id == UpdatePrompt.CardId);
        Assert.Equal(2, rig.PopUps.Current().Count);
    }

    [Fact]
    public async Task ItIsAskedOncePerVersionEvenIfTheCardIsTickedAgain()
    {
        var rig = new Rig();
        rig.Prompt.Tick("2026.10.3");
        await rig.Prompt.AnsweredAsync(UpdatePrompt.CardId, false);

        rig.Prompt.Tick("2026.10.3");

        Assert.Null(rig.Card);
    }

    [Fact]
    public async Task ANewerVersionAfterLaterIsAskedAbout()
    {
        var rig = new Rig();
        rig.Prompt.Tick("2026.10.3");
        await rig.Prompt.AnsweredAsync(UpdatePrompt.CardId, false);

        rig.Prompt.Tick("2026.10.4");

        Assert.Equal("Update 2026.10.4 is ready", rig.Card!.Body);
    }

    [Fact]
    public async Task LaterTakesTheCardAwayAndRestartsNothing()
    {
        var rig = new Rig();
        rig.Prompt.Tick("2026.10.3");

        await rig.Prompt.AnsweredAsync(UpdatePrompt.CardId, false);

        Assert.Null(rig.Card);
        Assert.Equal(0, rig.Restarts);
    }

    [Fact]
    public async Task TheFirstUpdatePressOnlyTurnsTheButtonIntoRestartNow()
    {
        var rig = new Rig();
        rig.Prompt.Tick("2026.10.3");

        await rig.Prompt.AnsweredAsync(UpdatePrompt.CardId, true);

        Assert.Equal("Restart now", rig.Card!.Ask!.Yes);
        Assert.Equal(0, rig.Restarts);
    }

    [Fact]
    public async Task TheSecondPressWithinFiveSecondsRestarts()
    {
        var rig = new Rig();
        rig.Prompt.Tick("2026.10.3");
        await rig.Prompt.AnsweredAsync(UpdatePrompt.CardId, true);

        rig.Clock.Advance(TimeSpan.FromSeconds(4));
        rig.Prompt.Tick("2026.10.3");
        await rig.Prompt.AnsweredAsync(UpdatePrompt.CardId, true);

        Assert.Equal(1, rig.Restarts);
    }

    [Fact]
    public async Task RestartNowGoesBackToUpdateAfterFiveSecondsAndTheNextPressAsksAgain()
    {
        var rig = new Rig();
        rig.Prompt.Tick("2026.10.3");
        await rig.Prompt.AnsweredAsync(UpdatePrompt.CardId, true);

        rig.Clock.Advance(TimeSpan.FromSeconds(5));
        rig.Prompt.Tick("2026.10.3");
        Assert.Equal("Update", rig.Card!.Ask!.Yes);

        await rig.Prompt.AnsweredAsync(UpdatePrompt.CardId, true);

        Assert.Equal(0, rig.Restarts);
        Assert.Equal("Restart now", rig.Card!.Ask!.Yes);
    }

    [Fact]
    public async Task ARestartThatCouldNotStartKeepsTheCardAndSaysSo()
    {
        var rig = new Rig { RestartWorks = false };
        rig.Prompt.Tick("2026.10.3");
        await rig.Prompt.AnsweredAsync(UpdatePrompt.CardId, true);

        await rig.Prompt.AnsweredAsync(UpdatePrompt.CardId, true);

        Assert.Equal(1, rig.Restarts);
        Assert.Equal("Could not start another copy of Modbot.", rig.Card!.Detail);
        Assert.Equal("Update", rig.Card.Ask!.Yes);
    }

    [Fact]
    public void ATestCardShowsTheMadeUpVersionWithUpdateAndLater()
    {
        var rig = new Rig();

        rig.Prompt.ShowTest("9.9.9-test");

        Assert.Equal("Update 9.9.9-test is ready", rig.Card!.Body);
        Assert.Equal(new PopUpAsk("Update", "Later"), rig.Card.Ask);
    }

    [Fact]
    public async Task RestartNowOnATestCardTakesItAwayAndRestartsNothing()
    {
        var rig = new Rig();
        rig.Prompt.ShowTest("9.9.9-test");

        await rig.Prompt.AnsweredAsync(UpdatePrompt.CardId, true);
        Assert.Equal("Restart now", rig.Card!.Ask!.Yes);

        await rig.Prompt.AnsweredAsync(UpdatePrompt.CardId, true);

        Assert.Null(rig.Card);
        Assert.Equal(0, rig.Restarts);
    }

    [Fact]
    public async Task LaterOnATestCardTakesItAway()
    {
        var rig = new Rig();
        rig.Prompt.ShowTest("9.9.9-test");

        await rig.Prompt.AnsweredAsync(UpdatePrompt.CardId, false);

        Assert.Null(rig.Card);
        Assert.Equal(0, rig.Restarts);
    }

    [Fact]
    public async Task ARealVersionAfterATestCardIsStillAskedAboutAndRestarts()
    {
        var rig = new Rig();
        rig.Prompt.ShowTest("9.9.9-test");
        await rig.Prompt.AnsweredAsync(UpdatePrompt.CardId, false);

        rig.Prompt.Tick("2026.10.3");
        Assert.Equal("Update 2026.10.3 is ready", rig.Card!.Body);

        await rig.Prompt.AnsweredAsync(UpdatePrompt.CardId, true);
        await rig.Prompt.AnsweredAsync(UpdatePrompt.CardId, true);

        Assert.Equal(1, rig.Restarts);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ClosingATestCardPutsBackARealOneItReplaced(bool restartNow)
    {
        var rig = new Rig();
        rig.Prompt.Tick("2026.10.3");
        rig.Prompt.ShowTest("9.9.9-test");
        Assert.Equal("Update 9.9.9-test is ready", rig.Card!.Body);

        if (restartNow)
        {
            await rig.Prompt.AnsweredAsync(UpdatePrompt.CardId, true);
            await rig.Prompt.AnsweredAsync(UpdatePrompt.CardId, true);
        }
        else
        {
            await rig.Prompt.AnsweredAsync(UpdatePrompt.CardId, false);
        }

        Assert.Equal("Update 2026.10.3 is ready", rig.Card!.Body);
        Assert.Equal("Update", rig.Card.Ask!.Yes);
        Assert.Equal(0, rig.Restarts);
    }

    [Fact]
    public async Task ClosingATestCardDoesNotBringBackARealOneThatWasAlreadyAnswered()
    {
        var rig = new Rig();
        rig.Prompt.Tick("2026.10.3");
        await rig.Prompt.AnsweredAsync(UpdatePrompt.CardId, false);
        rig.Prompt.ShowTest("9.9.9-test");

        await rig.Prompt.AnsweredAsync(UpdatePrompt.CardId, false);

        Assert.Null(rig.Card);
    }

    [Fact]
    public void ARealVersionArrivingWhileATestCardIsUpReplacesIt()
    {
        var rig = new Rig();
        rig.Prompt.ShowTest("9.9.9-test");

        rig.Prompt.Tick("2026.10.3");

        Assert.Equal("Update 2026.10.3 is ready", rig.Card!.Body);
    }

    [Fact]
    public async Task ATestCardDoesNotUseUpTheQuestionForARealVersionAlreadyWaiting()
    {
        var rig = new Rig();
        rig.Prompt.ShowTest("9.9.9-test");
        await rig.Prompt.AnsweredAsync(UpdatePrompt.CardId, false);

        rig.Prompt.Tick("2026.10.3");
        await rig.Prompt.AnsweredAsync(UpdatePrompt.CardId, false);
        rig.Prompt.ShowTest("9.9.9-test");
        await rig.Prompt.AnsweredAsync(UpdatePrompt.CardId, false);
        rig.Prompt.Tick("2026.10.3");

        Assert.Null(rig.Card);
    }

    [Fact]
    public async Task AnAnswerToSomeOtherCardIsIgnored()
    {
        var rig = new Rig();
        rig.Prompt.Tick("2026.10.3");

        await rig.Prompt.AnsweredAsync("alert:1", false);

        Assert.NotNull(rig.Card);
    }
}
