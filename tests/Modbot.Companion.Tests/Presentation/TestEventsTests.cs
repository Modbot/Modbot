using System.Reflection;
using Modbot.Companion.CloudBackup;
using Modbot.Companion.Ingest;
using Modbot.Companion.Journal;
using Modbot.Companion.Overlay;
using Modbot.Companion.Pipeline;
using Modbot.Companion.Presentation;
using Modbot.Companion.Sounds;
using Modbot.Core.Users;
using Modbot.TestSupport;

namespace Modbot.Companion.Tests.Presentation;

/// <summary>
/// The Debug page's test events go through the notifier, the pop-ups and the overlay's loop the
/// way real ones do, only in debug mode, and never anywhere that reports to a server.
/// </summary>
public class TestEventsTests
{
    private readonly FakeClock _clock = new();

    private sealed class Wired
    {
        public PopUps PopUps { get; set; } = null!;

        public TestEvents Tests { get; set; } = null!;

        public List<(NotificationKind Kind, string? About)> Sounds { get; } = [];

        public List<LiveEvent> Live { get; } = [];

        public List<FlaggedJoinAlert> Alerts { get; } = [];
    }

    /// <summary>Wired the way the client wires it: the notifier asks the test events for a made-up person's info first.</summary>
    private Wired Wire(bool debugMode = true, bool overlayTakesIt = false)
    {
        var wired = new Wired { PopUps = new PopUps(_clock) };

        var notices = new EventNotifier(
            () => "usr_moderator",
            (popUp, kind) => wired.PopUps.Show(popUp, kind),
            (kind, about) => wired.Sounds.Add((kind, about)),
            subjectId => wired.Tests.InfoOf(subjectId),
            _clock,
            () => false);

        wired.Tests = new TestEvents(
            debugMode,
            _clock,
            notices,
            wired.PopUps,
            () => null,
            e =>
            {
                wired.Live.Add(e);
                return overlayTakesIt;
            },
            wired.Alerts.Add);

        return wired;
    }

    [Fact]
    public void OutsideDebugModeNothingHappens()
    {
        var wired = Wire(debugMode: false);

        foreach (var kind in Enum.GetValues<TestEventKind>())
            Assert.False(wired.Tests.Send(new TestEvent(kind, "Rin", TrustRank.TrustedUser, true, "reason")));

        Assert.False(wired.Tests.IsOn);
        Assert.Empty(wired.PopUps.Current());
        Assert.Empty(wired.Sounds);
        Assert.Empty(wired.Live);
        Assert.Empty(wired.Alerts);
        Assert.Null(wired.Tests.InfoOf(TestEvents.SubjectOf("Rin")));
    }

    [Fact]
    public void AJoinGoesThroughTheNotifierWithItsRankAndMark()
    {
        var wired = Wire();

        Assert.True(wired.Tests.Send(new TestEvent(TestEventKind.Joined, "Rin", TrustRank.TrustedUser, EighteenPlus: true)));

        var card = Assert.Single(wired.PopUps.Current());
        Assert.Equal("Rin", card.Body);
        Assert.Equal(TrustRank.TrustedUser, card.Rank);
        Assert.True(card.EighteenPlus);
        Assert.Equal(TestEvents.SubjectOf("Rin"), card.SubjectId);
        Assert.StartsWith(TestEvents.SubjectPrefix, card.SubjectId, StringComparison.Ordinal);

        Assert.Equal([NotificationKind.Joined], wired.Sounds.Select(s => s.Kind));

        // Offered to the overlay's loop too, as the server's echo of the join would be.
        Assert.Equal(LiveEventKinds.PersonJoined, Assert.Single(wired.Live).Kind);
    }

    [Fact]
    public void NoRankIsNoRank()
    {
        var wired = Wire();

        wired.Tests.Send(new TestEvent(TestEventKind.Left, "Rin"));

        var card = Assert.Single(wired.PopUps.Current());
        Assert.Null(card.Rank);
        Assert.False(card.EighteenPlus);
    }

    [Fact]
    public void AnAvatarChangeNamesTheTestAvatar()
    {
        var wired = Wire();

        wired.Tests.Send(new TestEvent(TestEventKind.ChangedAvatar, "Rin"));

        Assert.Equal(TestEvents.TestAvatar, Assert.Single(wired.PopUps.Current()).Detail);
        Assert.Empty(wired.Live);
    }

    [Fact]
    public void ABlankNameIsTheTestPerson()
    {
        var wired = Wire();

        wired.Tests.Send(new TestEvent(TestEventKind.AlreadyThere, "  "));

        Assert.Equal(TestEvents.DefaultName, Assert.Single(wired.PopUps.Current()).Body);
    }

    [Fact]
    public void AFlaggedJoinTheOverlayTakesIsLeftToTheOverlay()
    {
        var wired = Wire(overlayTakesIt: true);

        wired.Tests.Send(new TestEvent(TestEventKind.FlaggedJoin, "Rin", TrustRank.User, true, "two kicks"));

        var live = Assert.Single(wired.Live);
        Assert.Equal(LiveEventKinds.FlaggedJoin, live.Kind);
        Assert.True(live.Flagged);
        Assert.Equal("two kicks", live.Reason);
        Assert.Equal("User", live.Person!.TrustRank);
        Assert.True(live.Person.EighteenPlus);

        // The overlay's own loop puts the card up and plays the sound; nothing twice.
        Assert.Empty(wired.PopUps.Current());
        Assert.Empty(wired.Alerts);
    }

    [Fact]
    public void AFlaggedJoinNoServerCoversGetsTheSameCardHere()
    {
        var wired = Wire(overlayTakesIt: false);

        wired.Tests.Send(new TestEvent(TestEventKind.FlaggedJoin, "Rin", TrustRank.User, true, "two kicks"));

        var card = Assert.Single(wired.PopUps.Current());
        Assert.Equal(PopUpTone.Flagged, card.Tone);
        Assert.Equal("Flagged user joined", card.Heading);
        Assert.Equal("Rin", card.Body);
        Assert.Equal("two kicks", card.Detail);
        Assert.Equal(TrustRank.User, card.Rank);
        Assert.True(card.EighteenPlus);

        Assert.Equal(TestEvents.SubjectOf("Rin"), Assert.Single(wired.Alerts).SubjectId);
    }

    [Fact]
    public void AProblemAndTheHeadsUpsAreCards()
    {
        var wired = Wire();

        wired.Tests.Send(new TestEvent(TestEventKind.Problem, Reason: "Cannot reach the server"));
        wired.Tests.Send(new TestEvent(TestEventKind.KeepAnEye, "Rin", Reason: "watch the mirror"));
        wired.Tests.Send(new TestEvent(TestEventKind.Pin, Reason: "Event at 9"));

        var cards = wired.PopUps.Current();
        Assert.Equal(3, cards.Count);
        Assert.Contains(cards, c => c.Tone is PopUpTone.Problem && c.Body == "Cannot reach the server");
        Assert.Contains(cards, c => c.Body == "Rin" && c.Detail == "watch the mirror");
        Assert.Contains(cards, c => c.Body == "Event at 9");
    }

    [Fact]
    public void ThePopUpFiltersStillApply()
    {
        var wired = Wire();
        wired.PopUps.Wanted = kind => kind is not NotificationKind.Joined;

        wired.Tests.Send(new TestEvent(TestEventKind.Joined, "Rin"));

        Assert.Empty(wired.PopUps.Current());
    }

    [Fact]
    public void ARunIsAJoinAFlaggedJoinAnAvatarChangeAndALeave()
    {
        var run = TestEvents.Run(new TestEvent(TestEventKind.Problem, "Rin", TrustRank.User, true, "why"));

        Assert.Equal(
            [TestEventKind.Joined, TestEventKind.FlaggedJoin, TestEventKind.ChangedAvatar, TestEventKind.Left],
            run.Select(e => e.Kind));
        Assert.All(run, e => Assert.Equal("Rin", e.Name));
    }

    [Fact]
    public void TheSameNameIsTheSamePerson()
    {
        Assert.Equal(TestEvents.SubjectOf("Rin"), TestEvents.SubjectOf(" rin "));
        Assert.NotEqual(TestEvents.SubjectOf("Rin"), TestEvents.SubjectOf("Kai"));
        Assert.StartsWith(TestEvents.SubjectPrefix, TestEvents.SubjectOf("!!!"), StringComparison.Ordinal);
    }

    /// <summary>
    /// The servers hear what the engine routes to each connection's queue, and the event backup
    /// what the engine offers it; both take the same observations the notifier does. The test
    /// events are given only the notifier — as itself, not as the sink type the backup also is —
    /// so there is no way for a made-up person to reach anything that reports.
    /// </summary>
    [Fact]
    public void NothingThatReportsToAServerCanBeHandedIn()
    {
        Type[] reporting =
        [
            typeof(CompanionEngine),
            typeof(ServerConnection),
            typeof(FileEventBuffer),
            typeof(IIngestTransport),
            typeof(SentJournal),
            typeof(CloudEventBackup),
            typeof(IObservationSink),
            typeof(HttpClient),
        ];

        var type = typeof(TestEvents);
        var taken = type.GetConstructors().SelectMany(c => c.GetParameters()).Select(p => p.ParameterType)
            .Concat(type.GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public).Select(f => f.FieldType))
            .ToList();

        foreach (var held in taken)
        {
            Assert.DoesNotContain(held, reporting);
            foreach (var argument in held.IsGenericType ? held.GetGenericArguments() : [])
                Assert.DoesNotContain(argument, reporting);
        }

        Assert.Contains(typeof(EventNotifier), taken);
    }
}
