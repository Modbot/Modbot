using Modbot.Companion.Instances;
using Modbot.Companion.Overlay;
using Modbot.Companion.Presentation;
using Modbot.Companion.Sounds;
using Modbot.TestSupport;

namespace Modbot.Companion.Tests.Presentation;

/// <summary>
/// What the client read out of VRChat's log, offered to the pop-up overlay and to the bleep. It
/// puts the question; the two surfaces ask the moderator's filters and decide.
/// </summary>
public class EventNotifierTests
{
    private const string Moderator = "usr_me";

    private readonly FakeClock _clock = new();
    private readonly List<(PopUp PopUp, NotificationKind Kind)> _popUps = [];
    private readonly List<(NotificationKind Kind, string? About)> _sounds = [];

    private EventNotifier Notifier() => new(
        () => Moderator,
        (popUp, kind) => _popUps.Add((popUp, kind)),
        (kind, about) => _sounds.Add((kind, about)));

    private static InstanceLocation Instance()
    {
        Assert.True(InstanceLocation.TryParse("wrld_4b34:39911~group(grp_cats)~groupAccessType(members)~region(use)", out var location));
        return location;
    }

    private ObservedPresence Seen(PresenceKind kind, string id, string? name, string? avatar = null)
        => new(kind, _clock.UtcNow.DateTime, id, name, Instance(), avatar);

    [Theory]
    [InlineData(PresenceKind.Joined, NotificationKind.Joined)]
    [InlineData(PresenceKind.PresenceObserved, NotificationKind.AlreadyThere)]
    [InlineData(PresenceKind.Left, NotificationKind.Left)]
    [InlineData(PresenceKind.AvatarChanged, NotificationKind.ChangedAvatar)]
    public void EveryKindTheLogReaderProducesIsOfferedToBothWays(PresenceKind presence, NotificationKind expected)
    {
        Notifier().Offer([Seen(presence, "usr_rin", "Rin")]);

        Assert.Equal(expected, Assert.Single(_popUps).Kind);
        Assert.Equal((expected, "usr_rin"), Assert.Single(_sounds));
    }

    [Fact]
    public void NeverTheModeratorThemselves()
    {
        // Their own arrival and departure are things they were there for.
        Notifier().Offer(
        [
            Seen(PresenceKind.Joined, Moderator, "Me"),
            Seen(PresenceKind.Left, Moderator, "Me"),
        ]);

        Assert.Empty(_popUps);
        Assert.Empty(_sounds);
    }

    [Fact]
    public void AStoppedLogIsToldEvenThoughItIsAboutTheModerator()
    {
        // It is about them and it is the thing worth knowing: the client can no longer see who
        // is in the instance.
        Notifier().Offer([Seen(PresenceKind.LogStopped, Moderator, "Me")]);

        Assert.Equal(NotificationKind.LogStopped, Assert.Single(_popUps).Kind);
        Assert.Equal(NotificationKind.LogStopped, Assert.Single(_sounds).Kind);
    }

    [Fact]
    public void TheCardNamesWhatHappenedAndWho()
    {
        Notifier().Offer([Seen(PresenceKind.Joined, "usr_rin", "Rin")]);

        var card = Assert.Single(_popUps).PopUp;

        Assert.Equal("Joined", card.Heading);
        Assert.Equal("Rin", card.Body);
        Assert.Equal(PopUpTone.Plain, card.Tone);
    }

    [Fact]
    public void TheSamePersonArrivingTwiceIsOneCardRatherThanTwo()
    {
        var notifier = Notifier();
        notifier.Offer([Seen(PresenceKind.Joined, "usr_rin", "Rin")]);
        notifier.Offer([Seen(PresenceKind.Joined, "usr_rin", "Rin")]);

        Assert.Equal(2, _popUps.Count);
        Assert.Equal(_popUps[0].PopUp.Id, _popUps[1].PopUp.Id);
    }

    [Fact]
    public void ALeaveAndAJoinForOnePersonAreDifferentCards()
    {
        var notifier = Notifier();
        notifier.Offer([Seen(PresenceKind.Joined, "usr_rin", "Rin"), Seen(PresenceKind.Left, "usr_rin", "Rin")]);

        Assert.NotEqual(_popUps[0].PopUp.Id, _popUps[1].PopUp.Id);
    }

    [Fact]
    public void AnAvatarChangeNamesTheAvatarUnderneath()
    {
        Notifier().Offer([Seen(PresenceKind.AvatarChanged, "usr_rin", "Rin", "Tall Cat")]);

        var card = Assert.Single(_popUps).PopUp;

        Assert.Equal("Rin", card.Body);
        Assert.Equal("Tall Cat", card.Detail);
    }

    [Fact]
    public void SomebodyWithNoNameYetIsNamedByTheirId()
    {
        Notifier().Offer([Seen(PresenceKind.Joined, "usr_rin", null)]);

        Assert.Equal("usr_rin", Assert.Single(_popUps).PopUp.Body);
    }

    [Fact]
    public void AStoppedLogReadsAsAProblemInTheEventsPagesOwnWords()
    {
        Notifier().Offer([Seen(PresenceKind.LogStopped, Moderator, "Me")]);

        var card = Assert.Single(_popUps).PopUp;

        Assert.Equal("Modbot", card.Heading);
        Assert.Equal(PopUpTone.Problem, card.Tone);
        Assert.Contains("VRChat's log stopped", card.Body, StringComparison.Ordinal);
    }

    [Fact]
    public void WithNowhereToSendAnythingItDoesNothing()
    {
        // No notification overlay and no sound on this PC: nothing to ask, and nothing thrown.
        new EventNotifier(() => Moderator).Offer([Seen(PresenceKind.Joined, "usr_rin", "Rin")]);

        Assert.Empty(_popUps);
        Assert.Empty(_sounds);
    }
}
