using Modbot.Companion.Ingest;
using Modbot.Companion.Instances;
using Modbot.Companion.Overlay;
using Modbot.Overlay.Driving;
using Modbot.Overlay.Views;
using Modbot.TestSupport;

namespace Modbot.Overlay.Tests.Driving;

/// <summary>
/// A moderation row the server sends (a sentence and where it comes from) is a line on the Audit Log
/// and nothing else: no roster read, no card, no keep-an-eye, and never twice.
/// </summary>
public class OverlayDriverModerationRowsTests
{
    private const string Group = "grp_cats";
    private const string Instance = "39911";

    private sealed class RecordingPresenter : IOverlayPresenter
    {
        public OverlayScreen Last { get; private set; } = OverlayScreen.Idle;

        public bool Update(OverlayScreen screen)
        {
            Last = screen;
            return true;
        }

        public void Show()
        {
        }

        public void Hide()
        {
        }
    }

    private sealed class Reads : IOverlayReadClient
    {
        public Queue<ReadResult<LivePollPage>> Live { get; } = new();

        public int ContextReads { get; private set; }

        public Task<ReadResult<InstanceContext>> GetContextAsync(ServerPairing pairing, string instanceId, string? worldId, CancellationToken cancellationToken)
        {
            ContextReads++;
            return Task.FromResult(new ReadResult<InstanceContext>(ReadOutcome.Fetched, new InstanceContext(instanceId, [])));
        }

        public Task<ReadResult<LivePollPage>> PollLiveAsync(ServerPairing pairing, string instanceId, string? worldId, string? after, int waitSeconds, CancellationToken cancellationToken)
            => Task.FromResult(Live.Count > 0 ? Live.Dequeue() : new ReadResult<LivePollPage>(ReadOutcome.NothingWaiting, Elapsed: TimeSpan.FromSeconds(waitSeconds)));

        public Task<ReadResult<UserSummary>> GetUserAsync(ServerPairing pairing, string subjectId, CancellationToken cancellationToken)
            => Task.FromResult(new ReadResult<UserSummary>(ReadOutcome.Unreachable));
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static InstanceLocation Location(string instance = Instance)
    {
        Assert.True(InstanceLocation.TryParse($"wrld_4b34:{instance}~group({Group})~groupAccessType(members)~region(use)", out var location));
        return location;
    }

    private static LiveEvent Moderation(string id, string? instance = null, string origin = "VRChat") => new(
        id,
        id,
        "fact",
        DateTimeOffset.UnixEpoch,
        instance,
        null,
        false,
        null,
        false,
        WorldId: instance is null ? null : "wrld_4b34",
        Sentence: "Kai joined the group.",
        Origin: origin);

    private static LiveEvent Joined(string id, string kind = LiveEventKinds.PersonJoined) => new(
        id,
        id,
        kind,
        DateTimeOffset.UnixEpoch,
        Instance,
        new LivePerson("usr_kai", "Kai", null, RosterStanding.Flagged, 2, ["kicked before"]),
        kind == LiveEventKinds.FlaggedJoin,
        "kicked before",
        false);

    private static (OverlayDriver Driver, RecordingPresenter Presenter, Reads Reads, PopUps PopUps) Build()
    {
        var presenter = new RecordingPresenter();
        var reads = new Reads();
        var clock = new FakeClock();
        var popUps = new PopUps(clock);
        var driver = new OverlayDriver(presenter, reads, clock, popUps: popUps);
        driver.Add(new ServerPairing("cats", new Uri("https://cats.example"), "token", Group), "Cat Lounge");
        driver.EnteredInstance(Location());
        return (driver, presenter, reads, popUps);
    }

    private static async Task Poll(OverlayDriver driver, Reads reads, params LiveEvent[] events)
    {
        reads.Live.Enqueue(new ReadResult<LivePollPage>(ReadOutcome.Fetched, new LivePollPage(events, events[^1].Cursor, false)));

        // The first tick starts the poll; the second harvests it; the third shows what a read it asked for brought.
        await driver.TickAsync(Ct);
        await driver.TickAsync(Ct);
        await driver.TickAsync(Ct);
    }

    [Fact]
    public async Task ModerationRowsAreListedWithThePresenceRowsNewestFirst()
    {
        var (driver, presenter, reads, _) = Build();

        // One about this instance and one that happened elsewhere and so names no instance.
        await Poll(driver, reads, Joined("e1"), Moderation("e2"), Moderation("e3", Instance, "Discord"));

        Assert.Equal(["e3", "e2", "e1"], presenter.Last.EventsOrNone.Select(e => e.Id));
        Assert.Equal(["Discord", "VRChat", null], presenter.Last.EventsOrNone.Select(e => e.Origin));
    }

    [Fact]
    public async Task AModerationRowDoesNotMakeTheRosterDueForARead()
    {
        var (driver, _, reads, _) = Build();
        await driver.TickAsync(Ct);
        var before = reads.ContextReads;

        await Poll(driver, reads, Moderation("e1"), Moderation("e2", Instance));

        Assert.Equal(before, reads.ContextReads);
    }

    [Fact]
    public async Task APresenceRowStillMakesTheRosterDueForARead()
    {
        var (driver, _, reads, _) = Build();
        await driver.TickAsync(Ct);
        var before = reads.ContextReads;

        await Poll(driver, reads, Joined("e1"));

        Assert.True(reads.ContextReads > before);
    }

    [Fact]
    public async Task AModerationRowRaisesNoCardAndNoPopUp()
    {
        var (driver, presenter, reads, popUps) = Build();

        await Poll(driver, reads, Moderation("e1"), Moderation("e2", Instance));

        Assert.Null(presenter.Last.Alert);
        Assert.Empty(popUps.Current());
    }

    [Fact]
    public async Task AModerationRowNamingAnotherInstanceIsListed_BecauseTheServerSendsItOnlyForSomebodySeenHere()
    {
        var (driver, presenter, reads, popUps) = Build();
        await driver.TickAsync(Ct);
        var before = reads.ContextReads;

        await Poll(driver, reads, Moderation("e1", "40000"));

        Assert.Equal(["e1"], presenter.Last.EventsOrNone.Select(e => e.Id));
        Assert.Equal(before, reads.ContextReads);
        Assert.Null(presenter.Last.Alert);
        Assert.Empty(popUps.Current());
    }

    [Fact]
    public async Task AModerationRowSentTwiceIsListedOnce()
    {
        var (driver, presenter, reads, _) = Build();

        await Poll(driver, reads, Moderation("e1"));
        await Poll(driver, reads, Moderation("e1"), Moderation("e2"));

        Assert.Equal(["e2", "e1"], presenter.Last.EventsOrNone.Select(e => e.Id));
    }

    [Fact]
    public async Task ModerationRowsAreKeptWithinTheLimitAndGoWhenTheModeratorMovesOn()
    {
        var (driver, presenter, reads, _) = Build();

        await Poll(driver, reads, [.. Enumerable.Range(1, OverlayDriver.MostEventsKept + 5).Select(n => Moderation("m" + n))]);

        Assert.Equal(OverlayDriver.MostEventsKept, presenter.Last.EventsOrNone.Count);
        Assert.Equal("m" + (OverlayDriver.MostEventsKept + 5), presenter.Last.EventsOrNone[0].Id);

        driver.EnteredInstance(Location("40000"));

        Assert.Empty(driver.Events);
    }

    [Fact]
    public async Task AFlaggedJoinIsStillACardBesideAModerationRow()
    {
        var (driver, presenter, reads, popUps) = Build();

        await Poll(driver, reads, Moderation("e1"), Joined("e2", LiveEventKinds.FlaggedJoin));

        Assert.NotNull(presenter.Last.Alert);
        Assert.Single(popUps.Current());
        Assert.Equal(["e2", "e1"], presenter.Last.EventsOrNone.Select(e => e.Id));
    }

    [Fact]
    public async Task ATestModerationRowIsPlacedInTheInstanceAndListed()
    {
        var (driver, _, _, popUps) = Build();
        await driver.TickAsync(Ct);

        Assert.True(driver.TakeTestEvent(Moderation("test-1", "00000", "Modbot")));

        var row = Assert.Single(driver.Events);
        Assert.Equal("Modbot", row.Origin);
        Assert.Equal(Instance, row.InstanceId);
        Assert.Empty(popUps.Current());
    }
}
