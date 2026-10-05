using Modbot.Companion.Overlay;
using Modbot.Companion.Presentation;
using Modbot.Companion.TestRemote;
using Modbot.Core.Users;

namespace Modbot.Companion.Tests.TestRemote;

/// <summary>One line of text in, one command out, or why not.</summary>
public class RemoteCommandTests
{
    private static RemoteCommand Ok(string line)
    {
        var parsed = RemoteCommands.Parse(line);
        Assert.Null(parsed.Error);
        return parsed.Command!;
    }

    private static string Fails(string line)
    {
        var parsed = RemoteCommands.Parse(line);
        Assert.Null(parsed.Command);
        return parsed.Error!;
    }

    [Fact]
    public void TheCommandsWithNothingAfterThem()
    {
        Assert.IsType<RemoteCommand.State>(Ok("state"));
        Assert.IsType<RemoteCommand.PutBack>(Ok("put-back"));
        Assert.IsType<RemoteCommand.ClearCards>(Ok("clear-cards"));
        Assert.IsType<RemoteCommand.GrabPanel>(Ok("grab-panel"));
        Assert.IsType<RemoteCommand.ReleasePanel>(Ok("release-panel"));
        Assert.IsType<RemoteCommand.State>(Ok("  STATE  "));

        Fails("state now");
    }

    [Fact]
    public void PressTakesOneLabelQuotedWhenItHasSpaces()
    {
        Assert.Equal(new RemoteCommand.Press("Put it back in front of me"), Ok("press \"Put it back in front of me\""));
        Assert.Equal(new RemoteCommand.Press("Head"), Ok("press Head"));

        Fails("press");
        Fails("press Put it back");
    }

    [Fact]
    public void SlideTakesAFractionFromZeroToOne()
    {
        Assert.Equal(new RemoteCommand.Slide("Width", 0.25), Ok("slide Width 0.25"));

        Fails("slide Width 1.5");
        Fails("slide Width -0.1");
        Fails("slide Width half");
    }

    [Fact]
    public void TapAndScrollTakePixels()
    {
        Assert.Equal(new RemoteCommand.TapPanel(512, 100.5), Ok("tap-panel 512 100.5"));
        Assert.Equal(new RemoteCommand.ScrollPanel(3, -1, -1), Ok("scroll-panel 3"));
        Assert.Equal(new RemoteCommand.ScrollPanel(-2, 500, 600), Ok("scroll-panel -2 500 600"));

        Fails("tap-panel 10");
        Fails("tap-panel -1 10");
        Fails("scroll-panel 0");
        Fails("scroll-panel 2 10");
    }

    [Fact]
    public void MovePanelTakesMetresFromTheHead()
    {
        Assert.Equal(new RemoteCommand.MovePanel(0.2f, -0.1f, -1f), Ok("move-panel 0.2 -0.1 -1"));

        Fails("move-panel 0 0");
        Fails("move-panel 0 0 -40");
    }

    [Fact]
    public void WaitIsBounded()
    {
        Assert.Equal(new RemoteCommand.Wait(500), Ok("wait 500"));

        Fails("wait -5");
        Fails($"wait {RemoteCommands.LongestWait + 1}");
    }

    [Fact]
    public void AnEventReadsKindNameRankEighteenPlusAndReason()
    {
        var command = Assert.IsType<RemoteCommand.Event>(Ok("event flagged-join \"Rin Test\" known-user 18+ Was rude in the last instance"));

        Assert.Equal(TestEventKind.FlaggedJoin, command.Test.Kind);
        Assert.Equal("Rin Test", command.Test.Name);
        Assert.Equal(TrustRank.KnownUser, command.Test.Rank);
        Assert.True(command.Test.EighteenPlus);
        Assert.Equal("Was rude in the last instance", command.Test.Reason);

        var bare = Assert.IsType<RemoteCommand.Event>(Ok("event joined Rin"));
        Assert.Null(bare.Test.Rank);
        Assert.False(bare.Test.EighteenPlus);
        Assert.Null(bare.Test.Reason);

        var none = Assert.IsType<RemoteCommand.Event>(Ok("event left Rin none no"));
        Assert.Null(none.Test.Rank);
    }

    [Fact]
    public void AnEventNeedsAKnownKindAndRank()
    {
        Fails("event");
        Fails("event joined");
        Fails("event dancing Rin");
        Fails("event joined Rin 3");
        Fails("event joined Rin wizard");
        Fails("event joined Rin user maybe");
    }

    [Fact]
    public void ARunReadsAnOptionalPerson()
    {
        Assert.IsType<RemoteCommand.Run>(Ok("run"));

        var run = Assert.IsType<RemoteCommand.Run>(Ok("run Rin user 18+"));
        Assert.Equal("Rin", run.First.Name);
        Assert.Equal(TrustRank.User, run.First.Rank);

        Fails("run Rin user 18+ extra");
    }

    [Theory]
    [InlineData("event joined usr_c1644b5b-3ca4-45b4-97c6-a2a0de70d469")]
    [InlineData("event flagged-join USR_abc")]
    [InlineData("run usr_c1644b5b-3ca4-45b4-97c6-a2a0de70d469")]
    public void ARealPersonsIdIsRefused(string line)
    {
        Assert.Contains("Test people only", Fails(line), StringComparison.Ordinal);
    }

    [Fact]
    public void EveryNameBecomesATestPerson()
    {
        Assert.Null(RemoteCommands.RefuseRealPerson("Rin"));
        Assert.Null(RemoteCommands.RefuseRealPerson(""));
        Assert.True(TestPeople.IsTest(TestEvents.SubjectOf("Rin")));
        Assert.NotNull(RemoteCommands.RefuseRealPerson("usr_1"));
    }

    [Fact]
    public void AnUnknownCommandNamesTheKnownOnes()
    {
        Assert.Contains("tap-panel", Fails("dance"), StringComparison.Ordinal);
        Assert.Contains("tap-panel", Fails(""), StringComparison.Ordinal);
    }

    [Fact]
    public void WordsSplitOnSpacesAndKeepQuotedOnes()
    {
        Assert.Equal(["press", "Put it back", "x"], RemoteCommands.Words("press  \"Put it back\" x"));
        Assert.Equal(["event", "joined", ""], RemoteCommands.Words("event joined \"\""));
    }
}
