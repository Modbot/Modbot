using Modbot.Companion.Overlay;
using Modbot.TestSupport;

namespace Modbot.Companion.Tests.Overlay;

/// <summary>
/// Who counts as having just left the instance, for how long, and when they are somebody who is
/// here again. Only the app's own clock decides any of it.
/// </summary>
public class RecentLeaversTests
{
    private readonly FakeClock _clock = new();

    private static RosterMember Person(string name, RosterStanding standing = RosterStanding.Ordinary)
        => new("usr_" + name, name, standing, 0, []);

    private static readonly RosterMember Rin = Person("rin", RosterStanding.Flagged);
    private static readonly RosterMember Kai = Person("kai", RosterStanding.Staff);
    private static readonly RosterMember Jo = Person("jo");

    [Fact]
    public void TheFirstReadOnlySetsWhatToCompareWith()
    {
        // Walking in must not show the whole room as having just left.
        var leavers = new RecentLeavers(_clock);

        leavers.Update([Rin, Kai]);

        Assert.Empty(leavers.Current());
    }

    [Fact]
    public void SomebodyTheNextReadNoLongerListsHasJustLeft()
    {
        var leavers = new RecentLeavers(_clock);
        leavers.Update([Rin, Kai, Jo]);

        _clock.Advance(TimeSpan.FromSeconds(5));
        leavers.Update([Kai, Jo]);

        var left = Assert.Single(leavers.Current());
        Assert.Equal("usr_rin", left.Member.SubjectId);
        Assert.Equal(RosterStanding.Flagged, left.Member.Standing);
        Assert.Equal(_clock.UtcNow, left.LeftAt);
    }

    [Fact]
    public void TheRowStaysForAMinuteAndThenGoes()
    {
        var leavers = new RecentLeavers(_clock);
        leavers.Update([Rin, Kai]);
        leavers.Update([Kai]);

        _clock.Advance(RecentLeavers.Stay - TimeSpan.FromSeconds(1));
        Assert.Single(leavers.Current());

        _clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Empty(leavers.Current());
        Assert.Null(leavers.Find("usr_rin"));
    }

    [Fact]
    public void TheMinuteCountsDownInWholeSeconds()
    {
        var leavers = new RecentLeavers(_clock);
        leavers.Update([Rin, Kai]);
        leavers.Update([Kai]);

        var left = Assert.Single(leavers.Current());
        Assert.Equal(60, left.SecondsLeft(_clock.UtcNow));

        _clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(59, left.SecondsLeft(_clock.UtcNow));

        // Counted up to a whole second, so the last moment still says 1 rather than 0.
        _clock.Advance(TimeSpan.FromSeconds(58.5));
        Assert.Equal(1, left.SecondsLeft(_clock.UtcNow));

        _clock.Advance(TimeSpan.FromSeconds(1.5));
        Assert.Equal(0, left.SecondsLeft(_clock.UtcNow));
    }

    [Fact]
    public void SomebodyBackWithinAMinuteIsPresentAgainAndNotListedTwice()
    {
        var leavers = new RecentLeavers(_clock);
        leavers.Update([Rin, Kai]);
        leavers.Update([Kai]);

        _clock.Advance(TimeSpan.FromSeconds(20));
        leavers.Update([Rin, Kai]);

        Assert.Empty(leavers.Current());
        Assert.Null(leavers.Find("usr_rin"));
    }

    [Fact]
    public void FurtherReadsDoNotStartTheMinuteAgain()
    {
        var leavers = new RecentLeavers(_clock);
        leavers.Update([Rin, Kai]);
        leavers.Update([Kai]);
        var first = Assert.Single(leavers.Current()).LeftAt;

        _clock.Advance(TimeSpan.FromSeconds(20));
        leavers.Update([Kai]);
        _clock.Advance(TimeSpan.FromSeconds(20));
        leavers.Update([Kai]);

        Assert.Equal(first, Assert.Single(leavers.Current()).LeftAt);
    }

    [Fact]
    public void SomebodyWhoLeftAgainAfterComingBackGetsAFreshMinute()
    {
        var leavers = new RecentLeavers(_clock);
        leavers.Update([Rin, Kai]);
        leavers.Update([Kai]);
        _clock.Advance(TimeSpan.FromSeconds(10));
        leavers.Update([Rin, Kai]);
        _clock.Advance(TimeSpan.FromSeconds(10));
        leavers.Update([Kai]);

        var left = Assert.Single(leavers.Current());
        Assert.Equal(60, left.SecondsLeft(_clock.UtcNow));
    }

    [Fact]
    public void SeveralWhoLeftAtOnceAreAllListedFirstToGoFirst()
    {
        var leavers = new RecentLeavers(_clock);
        leavers.Update([Rin, Kai, Jo]);
        leavers.Update([Jo]);
        _clock.Advance(TimeSpan.FromSeconds(5));
        leavers.Update([]);

        Assert.Equal(["usr_kai", "usr_rin", "usr_jo"], leavers.Current().Select(l => l.Member.SubjectId));
    }

    [Fact]
    public void FindGivesTheRowAsItLastWasWhileTheyWereHere()
    {
        var leavers = new RecentLeavers(_clock);
        leavers.Update([Rin, Kai]);
        leavers.Update([Kai]);

        Assert.Equal(Rin, leavers.Find("usr_rin"));
        Assert.Null(leavers.Find("usr_kai"));
    }

    [Fact]
    public void ForgettingStartsOverFromTheNextRead()
    {
        // Another instance, other people: nothing carries across, and the first read there is
        // only what to compare with again, rather than a roomful of people who "left".
        var leavers = new RecentLeavers(_clock);
        leavers.Update([Rin, Kai]);
        leavers.Update([Kai]);

        leavers.Forget();
        Assert.Empty(leavers.Current());

        leavers.Update([Jo]);
        Assert.Empty(leavers.Current());
    }

    [Fact]
    public void AClockSetBackByMoreThanAMinuteDoesNotKeepSomebodyForever()
    {
        var leavers = new RecentLeavers(_clock);
        leavers.Update([Rin, Kai]);
        leavers.Update([Kai]);

        _clock.Advance(-TimeSpan.FromMinutes(5));

        Assert.Empty(leavers.Current());
    }
}
