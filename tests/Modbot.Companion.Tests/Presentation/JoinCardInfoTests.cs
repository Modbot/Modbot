using Modbot.Companion.Instances;
using Modbot.Companion.Overlay;
using Modbot.Companion.Presentation;
using Modbot.Companion.Sounds;
using Modbot.Core.Users;
using Modbot.TestSupport;

namespace Modbot.Companion.Tests.Presentation;

/// <summary>
/// A join pop-up carries the person's VRChat trust rank and, when they have it, Modbot's 18+ mark.
/// A join waits a few seconds for them, and a card that went up without them is filled in where it
/// stands once they come.
/// </summary>
public class JoinCardInfoTests
{
    private readonly FakeClock _clock = new();

    private static InstanceLocation Instance()
    {
        Assert.True(InstanceLocation.TryParse("wrld_4b34:39911~group(grp_cats)~groupAccessType(members)~region(use)", out var location));
        return location;
    }

    private ObservedPresence Seen(PresenceKind kind, string id, string? name)
        => new(kind, _clock.UtcNow.DateTime, id, name, Instance());

    private static PersonInfo Rank(TrustRank rank) => new(rank, null);

    [Fact]
    public void AJoinCardSaysTheRankWhenItIsKnown()
    {
        var card = EventNotifier.Card(Seen(PresenceKind.Joined, "usr_rin", "Rin"), NotificationKind.Joined, Rank(TrustRank.TrustedUser));

        Assert.Equal("Rin", card.Body);
        Assert.Equal(TrustRank.TrustedUser, card.Rank);
        Assert.False(card.EighteenPlus);
        Assert.Null(card.Detail);
    }

    [Fact]
    public void AJoinCardCarriesTheRankAndTheMarkAsThemselves()
    {
        var card = EventNotifier.Card(
            Seen(PresenceKind.Joined, "usr_rin", "Rin"),
            NotificationKind.Joined,
            new PersonInfo(TrustRank.TrustedUser, true));

        // Kept as what they are, not flattened into a line of text, so the card can draw the
        // rank's colour and the green chip.
        Assert.Equal(TrustRank.TrustedUser, card.Rank);
        Assert.True(card.EighteenPlus);
        Assert.Null(card.Detail);
    }

    [Fact]
    public void SomebodyWithoutTheMarkGetsNoChip()
    {
        var card = EventNotifier.Card(
            Seen(PresenceKind.Joined, "usr_rin", "Rin"),
            NotificationKind.Joined,
            new PersonInfo(TrustRank.NewUser, false));

        Assert.Equal(TrustRank.NewUser, card.Rank);
        Assert.False(card.EighteenPlus);
    }

    [Fact]
    public void TheMarkWithNoRankIsStillSaid()
    {
        var card = EventNotifier.Card(
            Seen(PresenceKind.Joined, "usr_rin", "Rin"),
            NotificationKind.Joined,
            new PersonInfo(null, true));

        Assert.Null(card.Rank);
        Assert.True(card.EighteenPlus);
    }

    [Fact]
    public void AJoinCardWithNothingKnownYetHasNoMarks()
    {
        var unknown = EventNotifier.Card(Seen(PresenceKind.Joined, "usr_rin", "Rin"), NotificationKind.Joined);
        var noMark = EventNotifier.Card(Seen(PresenceKind.Joined, "usr_rin", "Rin"), NotificationKind.Joined, new PersonInfo(null, false));

        Assert.False(unknown.HasPersonInfo);
        Assert.Null(unknown.Detail);
        Assert.False(noMark.HasPersonInfo);
        Assert.Null(noMark.Detail);
    }

    [Theory]
    [InlineData(PresenceKind.Joined, NotificationKind.Joined)]
    [InlineData(PresenceKind.Left, NotificationKind.Left)]
    [InlineData(PresenceKind.PresenceObserved, NotificationKind.AlreadyThere)]
    [InlineData(PresenceKind.AvatarChanged, NotificationKind.ChangedAvatar)]
    public void EveryCardAboutAPersonCarriesTheInfo(PresenceKind presence, NotificationKind kind)
    {
        var card = EventNotifier.Card(Seen(presence, "usr_rin", "Rin"), kind, new PersonInfo(TrustRank.TrustedUser, true));

        Assert.Equal(TrustRank.TrustedUser, card.Rank);
        Assert.True(card.EighteenPlus);
        Assert.Equal("usr_rin", card.SubjectId);
        Assert.Equal($"{NotificationFilters.Word(kind)}:usr_rin", card.Id);
    }

    [Fact]
    public void ALogStoppedCardIsAboutNobody()
    {
        var card = EventNotifier.Card(Seen(PresenceKind.LogStopped, "usr_me", "Me"), NotificationKind.LogStopped, new PersonInfo(TrustRank.TrustedUser, true));

        Assert.False(card.HasPersonInfo);
        Assert.Null(card.SubjectId);
    }

    [Fact]
    public void TheNotifierAsksForTheInfoOfWhoeverLeft()
    {
        var shown = new List<PopUp>();
        var notifier = new EventNotifier(
            () => "usr_me",
            (popUp, _) => shown.Add(popUp),
            infoOf: id => id == "usr_rin" ? new PersonInfo(TrustRank.KnownUser, true) : null);

        notifier.Offer([Seen(PresenceKind.Left, "usr_rin", "Rin"), Seen(PresenceKind.Left, "usr_kai", "Kai")]);

        Assert.Equal(TrustRank.KnownUser, shown[0].Rank);
        Assert.True(shown[0].EighteenPlus);

        // Never known: no marks, and nothing written in their place.
        Assert.False(shown[1].HasPersonInfo);
        Assert.Null(shown[1].Detail);
    }

    [Fact]
    public void InfoIsNothingWhenTheServerHasSaidNeither()
    {
        Assert.Null(PersonInfo.Of(null, null));
        Assert.Equal(new PersonInfo(null, false), PersonInfo.Of(null, false));
        Assert.Equal(new PersonInfo(TrustRank.User, null), PersonInfo.Of(TrustRank.User, null));
    }

    [Fact]
    public void TheNotifierAsksForTheInfoOfWhoeverJoined()
    {
        var shown = new List<PopUp>();
        var notifier = new EventNotifier(
            () => "usr_me",
            (popUp, _) => shown.Add(popUp),
            infoOf: id => id == "usr_rin" ? new PersonInfo(TrustRank.KnownUser, true) : null);

        notifier.Offer([Seen(PresenceKind.Joined, "usr_rin", "Rin"), Seen(PresenceKind.Joined, "usr_kai", "Kai")]);

        Assert.Equal(TrustRank.KnownUser, shown[0].Rank);
        Assert.True(shown[0].EighteenPlus);
        Assert.False(shown[1].HasPersonInfo);
    }

    [Fact]
    public void InfoThatArrivesLaterIsWrittenOntoTheCardThatIsUp()
    {
        var popUps = new PopUps(_clock) { Dwell = TimeSpan.FromSeconds(6) };
        popUps.Show(EventNotifier.Card(Seen(PresenceKind.Joined, "usr_rin", "Rin"), NotificationKind.Joined));

        _clock.Advance(TimeSpan.FromSeconds(4));
        EventNotifier.AddInfo(popUps, id => id == "usr_rin" ? new PersonInfo(TrustRank.User, true) : null);

        var card = Assert.Single(popUps.Current());
        Assert.Equal(TrustRank.User, card.Rank);
        Assert.True(card.EighteenPlus);
        Assert.Null(card.Detail);

        // Filling it in kept its time: it still goes when it would have.
        _clock.Advance(TimeSpan.FromSeconds(2));
        Assert.Empty(popUps.Current());
    }

    [Fact]
    public void InfoThatArrivesLaterIsWrittenOntoEveryCardAboutThatPerson()
    {
        var popUps = new PopUps(_clock);
        popUps.Show(EventNotifier.Card(Seen(PresenceKind.Left, "usr_rin", "Rin"), NotificationKind.Left));
        popUps.Show(EventNotifier.Card(Seen(PresenceKind.PresenceObserved, "usr_kai", "Kai"), NotificationKind.AlreadyThere));
        popUps.Show(new PopUp("alert:a1", "Flagged user joined", "Rin", "kicked before", PopUpTone.Flagged, SubjectId: "usr_rin"));

        EventNotifier.AddInfo(popUps, id => id == "usr_rin" ? new PersonInfo(TrustRank.TrustedUser, true) : new PersonInfo(TrustRank.User, null));

        var up = popUps.Current();
        Assert.Equal(["alert:a1", "already there:usr_kai", "left:usr_rin"], up.Select(p => p.Id));
        Assert.All(up, p => Assert.True(p.HasPersonInfo));
        Assert.Equal(TrustRank.TrustedUser, up[0].Rank);
        Assert.Equal("kicked before", up[0].Detail);
        Assert.Equal(TrustRank.User, up[1].Rank);
        Assert.False(up[1].EighteenPlus);
        Assert.True(up[2].EighteenPlus);
    }

    [Fact]
    public void CardsAboutNobodyAreLeftAlone()
    {
        var popUps = new PopUps(_clock);
        var problem = new PopUp("problem", "Modbot", "Cannot reach the server", null, PopUpTone.Problem);
        var flaggedFromBefore = new PopUp("alert-1", "Flagged user joined", "Rin", null, PopUpTone.Flagged);
        popUps.Show(problem);
        popUps.Show(flaggedFromBefore);

        EventNotifier.AddInfo(popUps, _ => new PersonInfo(TrustRank.TrustedUser, true));

        Assert.Equal([flaggedFromBefore, problem], popUps.Current());
    }

    private sealed class Waits
    {
        public readonly List<PopUp> Shown = [];
        public readonly List<NotificationKind> Sounds = [];
        public readonly Dictionary<string, PersonInfo> Info = [];
        public bool ServerCovers = true;
    }

    private EventNotifier Waiting(Waits waits) => new(
        () => "usr_me",
        (popUp, _) => waits.Shown.Add(popUp),
        (kind, _) => waits.Sounds.Add(kind),
        id => waits.Info.GetValueOrDefault(id),
        _clock,
        () => waits.ServerCovers);

    [Fact]
    public void AJoinWaitsForTheInfoAndGoesUpTheMomentItArrives()
    {
        var waits = new Waits();
        var notifier = Waiting(waits);

        notifier.Offer([Seen(PresenceKind.Joined, "usr_rin", "Rin")]);

        // Nothing yet: neither the card nor its sound.
        Assert.Empty(waits.Shown);
        Assert.Empty(waits.Sounds);
        Assert.Equal(1, notifier.Waiting);

        _clock.Advance(TimeSpan.FromSeconds(1));
        notifier.TellWaiting();
        Assert.Empty(waits.Shown);

        waits.Info["usr_rin"] = new PersonInfo(TrustRank.TrustedUser, true);
        notifier.TellWaiting();

        var card = Assert.Single(waits.Shown);
        Assert.Equal(TrustRank.TrustedUser, card.Rank);
        Assert.True(card.EighteenPlus);
        Assert.Equal(NotificationKind.Joined, Assert.Single(waits.Sounds));
        Assert.Equal(0, notifier.Waiting);
    }

    [Fact]
    public void AnAnswerOfNotEighteenPlusEndsTheWaitToo()
    {
        // The server has read the profile and there is no mark: that is an answer, not a gap.
        var waits = new Waits();
        var notifier = Waiting(waits);

        notifier.Offer([Seen(PresenceKind.Joined, "usr_rin", "Rin")]);
        waits.Info["usr_rin"] = new PersonInfo(null, false);
        notifier.TellWaiting();

        var card = Assert.Single(waits.Shown);
        Assert.False(card.HasPersonInfo);
        Assert.Equal(0, notifier.Waiting);
    }

    [Fact]
    public void AJoinWhoseInfoNeverComesGoesUpWithoutItOnceTheWaitIsOver()
    {
        var waits = new Waits();
        var notifier = Waiting(waits);

        notifier.Offer([Seen(PresenceKind.Joined, "usr_rin", "Rin")]);

        _clock.Advance(EventNotifier.InfoWait - TimeSpan.FromMilliseconds(1));
        notifier.TellWaiting();
        Assert.Empty(waits.Shown);

        _clock.Advance(TimeSpan.FromMilliseconds(1));
        notifier.TellWaiting();

        var card = Assert.Single(waits.Shown);
        Assert.Equal("Rin", card.Body);
        Assert.False(card.HasPersonInfo);
        Assert.Single(waits.Sounds);
    }

    [Fact]
    public void AJoinWhoseInfoIsAlreadyKnownDoesNotWait()
    {
        var waits = new Waits();
        waits.Info["usr_rin"] = Rank(TrustRank.User);

        Waiting(waits).Offer([Seen(PresenceKind.Joined, "usr_rin", "Rin")]);

        Assert.Equal(TrustRank.User, Assert.Single(waits.Shown).Rank);
    }

    [Fact]
    public void AJoinInAnInstanceNoPairedServerCoversDoesNotWait()
    {
        // No server will ever send the info for it, so waiting would only make it late.
        var waits = new Waits { ServerCovers = false };

        Waiting(waits).Offer([Seen(PresenceKind.Joined, "usr_rin", "Rin")]);

        Assert.Single(waits.Shown);
        Assert.Single(waits.Sounds);
    }

    [Fact]
    public void OnlyJoinsWait()
    {
        var waits = new Waits();

        Waiting(waits).Offer([Seen(PresenceKind.Left, "usr_rin", "Rin")]);

        Assert.Single(waits.Shown);
    }

    [Fact]
    public void ACardFilledInAlreadyIsNotFilledInAgain()
    {
        var popUps = new PopUps(_clock);
        var card = EventNotifier.Card(Seen(PresenceKind.Joined, "usr_rin", "Rin"), NotificationKind.Joined, new PersonInfo(TrustRank.KnownUser, true));
        popUps.Show(card);

        EventNotifier.AddInfo(popUps, _ => new PersonInfo(TrustRank.TrustedUser, false));

        Assert.Equal(card, Assert.Single(popUps.Current()));
    }

    [Fact]
    public void AMissingHalfIsFilledInAndTheOtherKept()
    {
        // A flagged card that went up with the rank the alert carried still gets its chip once the
        // server has read the profile, and a card that had the chip still gets its rank.
        var popUps = new PopUps(_clock);
        popUps.Show(new PopUp("alert:a1", "Flagged user joined", "Rin", "kicked before", PopUpTone.Flagged, TrustRank.KnownUser, SubjectId: "usr_rin"));
        popUps.Show(new PopUp("left:usr_kai", "Left", "Kai", null, PopUpTone.Plain, EighteenPlus: true, SubjectId: "usr_kai"));

        EventNotifier.AddInfo(popUps, _ => new PersonInfo(TrustRank.TrustedUser, true));

        var up = popUps.Current();
        Assert.Equal(TrustRank.TrustedUser, up[0].Rank);
        Assert.True(up[0].EighteenPlus);
        Assert.Equal(TrustRank.KnownUser, up[1].Rank);
        Assert.True(up[1].EighteenPlus);
    }

    [Fact]
    public void AnAnswerOfNoMarkLeavesARankedCardAsItWas()
    {
        var popUps = new PopUps(_clock);
        var card = new PopUp("left:usr_rin", "Left", "Rin", null, PopUpTone.Plain, TrustRank.User, SubjectId: "usr_rin");
        popUps.Show(card);

        EventNotifier.AddInfo(popUps, _ => new PersonInfo(TrustRank.TrustedUser, false));

        Assert.Equal(card, Assert.Single(popUps.Current()));
    }

    [Fact]
    public void AnAvatarChangeKeepsItsSmallLineAndGetsTheMarksBesideIt()
    {
        var popUps = new PopUps(_clock);
        var changed = EventNotifier.Card(
            Seen(PresenceKind.AvatarChanged, "usr_rin", "Rin") with { AvatarName = "Tall Cat" },
            NotificationKind.ChangedAvatar);
        popUps.Show(changed);

        EventNotifier.AddInfo(popUps, _ => new PersonInfo(TrustRank.TrustedUser, true));

        var card = Assert.Single(popUps.Current());
        Assert.Equal("Tall Cat", card.Detail);
        Assert.Equal(TrustRank.TrustedUser, card.Rank);
        Assert.True(card.EighteenPlus);
    }

    [Fact]
    public void TheSamePersonLeavingTwiceRestartsOneCard()
    {
        var popUps = new PopUps(_clock) { Dwell = TimeSpan.FromSeconds(6) };
        popUps.Show(EventNotifier.Card(Seen(PresenceKind.Left, "usr_rin", "Rin"), NotificationKind.Left, Rank(TrustRank.User)));
        _clock.Advance(TimeSpan.FromSeconds(4));
        popUps.Show(EventNotifier.Card(Seen(PresenceKind.Left, "usr_rin", "Rin"), NotificationKind.Left, Rank(TrustRank.User)));

        Assert.Single(popUps.Current());

        _clock.Advance(TimeSpan.FromSeconds(6));
        Assert.Empty(popUps.Current());
    }

    [Fact]
    public void AnAmendThatChangesTheIdIsIgnored()
    {
        var popUps = new PopUps(_clock);
        var card = new PopUp("joined:usr_rin", "Joined", "Rin", null, PopUpTone.Plain);
        popUps.Show(card);

        popUps.Amend(c => c with { Id = "something else", Rank = TrustRank.User });

        Assert.Equal(card, Assert.Single(popUps.Current()));
    }
}
