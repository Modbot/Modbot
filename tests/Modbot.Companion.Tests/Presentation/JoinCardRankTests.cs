using Modbot.Companion.Instances;
using Modbot.Companion.Overlay;
using Modbot.Companion.Presentation;
using Modbot.Companion.Sounds;
using Modbot.Core.Users;
using Modbot.TestSupport;

namespace Modbot.Companion.Tests.Presentation;

/// <summary>
/// A join pop-up carries the person's VRChat trust rank. The card goes up the moment the log says
/// somebody arrived, which is usually before the server knows them, so a rank that arrives later
/// is written onto the card where it stands.
/// </summary>
public class JoinCardRankTests
{
    private readonly FakeClock _clock = new();

    private static InstanceLocation Instance()
    {
        Assert.True(InstanceLocation.TryParse("wrld_4b34:39911~group(grp_cats)~groupAccessType(members)~region(use)", out var location));
        return location;
    }

    private ObservedPresence Seen(PresenceKind kind, string id, string? name)
        => new(kind, _clock.UtcNow.DateTime, id, name, Instance());

    [Fact]
    public void AJoinCardSaysTheRankWhenItIsKnown()
    {
        var card = EventNotifier.Card(Seen(PresenceKind.Joined, "usr_rin", "Rin"), NotificationKind.Joined, TrustRank.TrustedUser);

        Assert.Equal("Rin", card.Body);
        Assert.Equal("Trusted User", card.Detail);
    }

    [Fact]
    public void AJoinCardWithNoRankYetHasNoSmallLine()
    {
        var card = EventNotifier.Card(Seen(PresenceKind.Joined, "usr_rin", "Rin"), NotificationKind.Joined);

        Assert.Null(card.Detail);
    }

    [Fact]
    public void OnlyJoinCardsCarryTheRank()
    {
        var card = EventNotifier.Card(Seen(PresenceKind.Left, "usr_rin", "Rin"), NotificationKind.Left, TrustRank.TrustedUser);

        Assert.Null(card.Detail);
    }

    [Fact]
    public void TheNotifierAsksForTheRankOfWhoeverJoined()
    {
        var shown = new List<PopUp>();
        var notifier = new EventNotifier(
            () => "usr_me",
            (popUp, _) => shown.Add(popUp),
            rankOf: id => id == "usr_rin" ? TrustRank.KnownUser : null);

        notifier.Offer([Seen(PresenceKind.Joined, "usr_rin", "Rin"), Seen(PresenceKind.Joined, "usr_kai", "Kai")]);

        Assert.Equal("Known User", shown[0].Detail);
        Assert.Null(shown[1].Detail);
    }

    [Fact]
    public void ARankThatArrivesLaterIsWrittenOntoTheCardThatIsUp()
    {
        var popUps = new PopUps(_clock) { Dwell = TimeSpan.FromSeconds(6) };
        popUps.Show(EventNotifier.Card(Seen(PresenceKind.Joined, "usr_rin", "Rin"), NotificationKind.Joined));

        _clock.Advance(TimeSpan.FromSeconds(4));
        EventNotifier.AddRanks(popUps, id => id == "usr_rin" ? TrustRank.User : null);

        Assert.Equal("User", Assert.Single(popUps.Current()).Detail);

        // Filling it in kept its time: it still goes when it would have.
        _clock.Advance(TimeSpan.FromSeconds(2));
        Assert.Empty(popUps.Current());
    }

    [Fact]
    public void OtherCardsAreLeftAlone()
    {
        var popUps = new PopUps(_clock);
        var left = EventNotifier.Card(Seen(PresenceKind.Left, "usr_rin", "Rin"), NotificationKind.Left);
        var flagged = new PopUp("alert-1", "Flagged user joined", "Rin", null, PopUpTone.Flagged);
        popUps.Show(left);
        popUps.Show(flagged);

        EventNotifier.AddRanks(popUps, _ => TrustRank.TrustedUser);

        Assert.Equal([flagged, left], popUps.Current());
    }

    [Fact]
    public void AnAmendThatChangesTheIdIsIgnored()
    {
        var popUps = new PopUps(_clock);
        var card = new PopUp("joined:usr_rin", "Joined", "Rin", null, PopUpTone.Plain);
        popUps.Show(card);

        popUps.Amend(c => c with { Id = "something else", Detail = "x" });

        Assert.Equal(card, Assert.Single(popUps.Current()));
    }
}
