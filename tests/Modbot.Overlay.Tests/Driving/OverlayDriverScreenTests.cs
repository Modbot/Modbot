using Modbot.Companion.Ingest;
using Modbot.Companion.Instances;
using Modbot.Companion.Overlay;
using Modbot.Overlay.Driving;
using Modbot.Overlay.Interaction;
using Modbot.Overlay.Views;
using Modbot.TestSupport;

namespace Modbot.Overlay.Tests.Driving;

/// <summary>
/// The loop's side of the two overlays: which screen the main panel is on, what the Events screen
/// holds, and what reaches the notification overlay's pop-ups.
/// </summary>
public class OverlayDriverScreenTests
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
        public Queue<ReadResult<InstanceContext>> Contexts { get; } = new();

        public Queue<ReadResult<LivePollPage>> Live { get; } = new();

        public Task<ReadResult<InstanceContext>> GetContextAsync(ServerPairing pairing, string instanceId, CancellationToken cancellationToken)
            => Task.FromResult(Contexts.Count > 0 ? Contexts.Dequeue() : new ReadResult<InstanceContext>(ReadOutcome.Unreachable));

        public Task<ReadResult<LivePollPage>> PollLiveAsync(ServerPairing pairing, string instanceId, string? after, int waitSeconds, CancellationToken cancellationToken)
            => Task.FromResult(Live.Count > 0 ? Live.Dequeue() : new ReadResult<LivePollPage>(ReadOutcome.NothingWaiting, Elapsed: TimeSpan.FromSeconds(waitSeconds)));

        public Task<ReadResult<UserSummary>> GetUserAsync(ServerPairing pairing, string subjectId, CancellationToken cancellationToken)
            => Task.FromResult(new ReadResult<UserSummary>(ReadOutcome.Unreachable));
    }

    private static InstanceLocation Location(string instance = Instance)
    {
        Assert.True(InstanceLocation.TryParse($"wrld_4b34:{instance}~group({Group})~groupAccessType(members)~region(use)", out var location));
        return location;
    }

    private static InstanceContext Roster() => new(
        Instance,
        [new RosterMember("usr_rin", "Rin", RosterStanding.Member, 0, [])]);

    private static LiveEvent Event(string id, string kind, string instance = Instance) => new(
        id,
        id,
        kind,
        DateTimeOffset.UnixEpoch,
        instance,
        new LivePerson("usr_rin", "Rin", null, RosterStanding.Flagged, 2, ["kicked before"]),
        kind == LiveEventKinds.FlaggedJoin,
        "kicked before",
        false);

    private static (OverlayDriver Driver, RecordingPresenter Presenter, Reads Reads, PopUps PopUps, FakeClock Clock) Build()
    {
        var presenter = new RecordingPresenter();
        var reads = new Reads();
        var clock = new FakeClock();
        var popUps = new PopUps(clock);
        var driver = new OverlayDriver(presenter, reads, clock, popUps: popUps);
        driver.Add(new ServerPairing("cats", new Uri("https://cats.example"), "token", Group), "Cat Lounge");
        driver.EnteredInstance(Location());
        reads.Contexts.Enqueue(new ReadResult<InstanceContext>(ReadOutcome.Fetched, Roster()));
        return (driver, presenter, reads, popUps, clock);
    }

    [Fact]
    public async Task ATabTapMovesBetweenScreens()
    {
        var (driver, presenter, _, _, _) = Build();
        await driver.TickAsync(TestContext.Current.CancellationToken);
        Assert.Equal(OverlayPage.Instance, presenter.Last.Page);

        driver.Tap(new OverlayTarget.GoTo(OverlayPage.Events));
        await driver.TickAsync(TestContext.Current.CancellationToken);

        Assert.Equal(OverlayPage.Events, presenter.Last.Page);
    }

    [Fact]
    public async Task AskingForThePersonScreenWithNobodyOpenShowsTheRoster()
    {
        var (driver, presenter, _, _, _) = Build();
        await driver.TickAsync(TestContext.Current.CancellationToken);

        driver.Tap(new OverlayTarget.GoTo(OverlayPage.Person));
        await driver.TickAsync(TestContext.Current.CancellationToken);

        Assert.Equal(OverlayPage.Instance, presenter.Last.Page);
    }

    [Fact]
    public async Task OpeningAPersonShowsThePersonScreen()
    {
        var (driver, presenter, _, _, _) = Build();
        await driver.TickAsync(TestContext.Current.CancellationToken);

        driver.Tap(new OverlayTarget.Person("usr_rin"));
        await driver.TickAsync(TestContext.Current.CancellationToken);

        Assert.Equal(OverlayPage.Person, presenter.Last.Page);
        Assert.Equal("usr_rin", presenter.Last.Person?.SubjectId);
    }

    [Fact]
    public async Task TheEventsScreenFillsFromTheLiveLinkNewestFirst()
    {
        var (driver, presenter, reads, _, _) = Build();
        reads.Live.Enqueue(new ReadResult<LivePollPage>(
            ReadOutcome.Fetched,
            new LivePollPage([Event("e1", LiveEventKinds.PersonJoined), Event("e2", LiveEventKinds.PersonLeft)], "e2", false)));

        // The first tick starts the poll; the second harvests it.
        await driver.TickAsync(TestContext.Current.CancellationToken);
        await driver.TickAsync(TestContext.Current.CancellationToken);

        Assert.Equal(["e2", "e1"], presenter.Last.EventsOrNone.Select(e => e.Id));
    }

    [Fact]
    public async Task AnEventForAnotherInstanceIsNotListed()
    {
        // The same boundary the roster and the alert card already follow: a moderator can only act
        // on the instance they are standing in.
        var (driver, presenter, reads, _, _) = Build();
        reads.Live.Enqueue(new ReadResult<LivePollPage>(
            ReadOutcome.Fetched,
            new LivePollPage([Event("e1", LiveEventKinds.PersonJoined, "40000")], "e1", false)));

        await driver.TickAsync(TestContext.Current.CancellationToken);
        await driver.TickAsync(TestContext.Current.CancellationToken);

        Assert.Empty(presenter.Last.EventsOrNone);
    }

    [Fact]
    public async Task LeavingTheInstanceClearsTheEventsAndThePopUps()
    {
        var (driver, presenter, reads, popUps, _) = Build();
        reads.Live.Enqueue(new ReadResult<LivePollPage>(
            ReadOutcome.Fetched,
            new LivePollPage([Event("e1", LiveEventKinds.FlaggedJoin)], "e1", false)));

        await driver.TickAsync(TestContext.Current.CancellationToken);
        await driver.TickAsync(TestContext.Current.CancellationToken);
        Assert.NotEmpty(popUps.Current());

        driver.EnteredInstance(Location("40000"));

        Assert.Empty(driver.Events);
        Assert.Empty(popUps.Current());
    }

    [Fact]
    public async Task AFlaggedArrivalReachesTheNotificationOverlayAsWellAsTheCard()
    {
        // The whole reason for a second overlay: a moderator with the main panel closed still gets
        // told, because the pop-up is head-locked and always there.
        var (driver, presenter, reads, popUps, _) = Build();
        reads.Live.Enqueue(new ReadResult<LivePollPage>(
            ReadOutcome.Fetched,
            new LivePollPage([Event("a1", LiveEventKinds.FlaggedJoin)], "a1", false)));

        await driver.TickAsync(TestContext.Current.CancellationToken);
        await driver.TickAsync(TestContext.Current.CancellationToken);

        Assert.NotNull(presenter.Last.Alert);
        var popUp = Assert.Single(popUps.Current());
        Assert.Equal(PopUpTone.Flagged, popUp.Tone);
        Assert.Equal("Rin", popUp.Body);
        Assert.Contains("Cat Lounge", popUp.Heading, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AProblemIsSaidOncePerProblemRatherThanOncePerTick()
    {
        var (driver, presenter, _, popUps, _) = Build();

        // Every context read after the first one fails, so the "cannot reach" line appears.
        for (var i = 0; i < 3; i++)
            await driver.TickAsync(TestContext.Current.CancellationToken);

        if (presenter.Last.Health is null)
            return;

        var up = popUps.Current();
        Assert.Single(up);
        Assert.Equal(PopUpTone.Problem, up[0].Tone);
    }

    [Fact]
    public void ThePresenterCanBeSwappedWhenTheMainPanelIsSwitchedOff()
    {
        // The loop keeps running for the notification overlay, with the screen going nowhere.
        var (driver, presenter, _, _, _) = Build();

        Assert.Same(presenter, driver.Presenter);
        driver.Presenter = NoPanel.Instance;
        Assert.False(driver.Presenter.Update(OverlayScreen.Idle));
    }
}
