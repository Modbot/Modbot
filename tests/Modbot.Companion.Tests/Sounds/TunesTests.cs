using Modbot.Companion.Presentation;
using Modbot.Companion.Sounds;

namespace Modbot.Companion.Tests.Sounds;

/// <summary>
/// Which sound belongs to what: the moderator's five rows against the kinds the client actually
/// knows about.
/// </summary>
public class TunesTests
{
    [Theory]
    [InlineData(NotificationKind.Joined)]
    [InlineData(NotificationKind.AlreadyThere)]
    [InlineData(NotificationKind.Left)]
    [InlineData(NotificationKind.ChangedAvatar)]
    public void TheOrdinaryTrafficOfAnInstanceIsTheChime(NotificationKind kind)
    {
        Assert.Equal(Tune.Chime, Tunes.For(kind));
    }

    [Fact]
    public void AFlaggedArrivalIsTheAlert()
    {
        Assert.Equal(Tune.Alert, Tunes.For(NotificationKind.FlaggedJoin));
    }

    [Theory]
    [InlineData(NotificationKind.Problem)]
    [InlineData(NotificationKind.LogStopped)]
    public void TheTwoThingsThatStopTheClientDoingItsJobAreTheUrgentOne(NotificationKind kind)
    {
        Assert.Equal(Tune.Urgent, Tunes.For(kind));
    }

    [Fact]
    public void NothingTheClientKnowsAboutRaisesTheAllClear()
    {
        // The row the moderator asked for that has no event behind it. There is nothing in the
        // client today that means "this is over": a problem is never told it has been put right,
        // and a flagged arrival is never withdrawn. The sound exists and the Test button plays it;
        // inventing a kind of event to justify it would have been worse than saying so.
        foreach (var kind in Enum.GetValues<NotificationKind>())
            Assert.NotEqual(Tune.AllClear, Tunes.For(kind));
    }

    [Fact]
    public void EveryKindTheCardListsHasASound()
    {
        foreach (var kind in NotificationFilters.Kinds)
            Assert.Contains(Tunes.For(kind), Tunes.All);
    }

    [Fact]
    public void TheDoubledAlertIsTheOnlySoundNoKindAsksForOnItsOwn()
    {
        // It is the alert's own answer to more than one flagged arrival at once, which is a thing
        // the rule counts rather than a kind of event the client is told about.
        foreach (var kind in Enum.GetValues<NotificationKind>())
            Assert.NotEqual(Tune.AlertTwice, Tunes.For(kind));
    }

    [Fact]
    public void SeriousnessRunsFromTheQuietOnesUpToTheUrgentOne()
    {
        Assert.Equal(Tunes.Urgency(Tune.Chime), Tunes.Urgency(Tune.AllClear));
        Assert.True(Tunes.Urgency(Tune.Chime) < Tunes.Urgency(Tune.Alert));
        Assert.True(Tunes.Urgency(Tune.Alert) < Tunes.Urgency(Tune.AlertTwice));
        Assert.True(Tunes.Urgency(Tune.AlertTwice) < Tunes.Urgency(Tune.Urgent));
    }

    [Fact]
    public void EverySoundHasANameAPersonWouldRecognise()
    {
        foreach (var tune in Tunes.All)
            Assert.False(string.IsNullOrWhiteSpace(Tunes.Name(tune)));

        Assert.Equal(Tunes.All.Count, Tunes.All.Select(Tunes.Name).Distinct().Count());
        Assert.Equal(Enum.GetValues<Tune>().Length, Tunes.All.Count);
    }
}
