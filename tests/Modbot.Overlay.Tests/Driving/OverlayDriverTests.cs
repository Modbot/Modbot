using Modbot.Client.Ingest;
using Modbot.Client.Instances;
using Modbot.Client.Overlay;
using Modbot.Overlay.Driving;
using Modbot.Overlay.Views;
using Modbot.TestSupport;

namespace Modbot.Overlay.Tests.Driving;

/// <summary>
/// The loop that makes the overlay run, and the four things that break it.
/// </summary>
/// <remarks>
/// A server that stops answering mid-session, a long poll a proxy hangs up on, an alert about a
/// room the moderator is not in, and — the one that would regress silently — a tick that redraws
/// when nothing changed. That last one costs nothing visible and throws away the entire reason the
/// CPU readback is affordable, so it is asserted on the rasteriser rather than on appearances.
/// </remarks>
public class OverlayDriverTests
{
    private const string Group = "grp_cats";
    private const string Instance = "39911";

    /// <summary>Counts what the real presenter would actually rasterise.</summary>
    private sealed class CountingPresenter : IOverlayPresenter
    {
        private OverlayScreen _last = OverlayScreen.Idle;

        public int Draws { get; private set; }

        public int Updates { get; private set; }

        public OverlayScreen Last => _last;

        public bool Update(OverlayScreen screen)
        {
            Updates++;

            // Exactly the gate OverlayHost applies, so this counts real frames.
            if (_last.LooksTheSameAs(screen))
                return false;

            _last = screen;
            Draws++;
            return true;
        }

        public void Show() { }

        public void Hide() { }
    }

    private sealed class ScriptedReads : IOverlayReadClient
    {
        public Queue<ReadResult<InstanceContext>> Contexts { get; } = new();

        public Queue<ReadResult<FlaggedJoinAlert>> Alerts { get; } = new();

        public int ContextCalls { get; private set; }

        public int AlertCalls { get; private set; }

        public Task<ReadResult<InstanceContext>> GetContextAsync(
            ServerPairing pairing, string instanceId, CancellationToken cancellationToken)
        {
            ContextCalls++;
            return Task.FromResult(Contexts.Count > 0
                ? Contexts.Dequeue()
                : new ReadResult<InstanceContext>(ReadOutcome.Unreachable));
        }

        public Task<ReadResult<FlaggedJoinAlert>> WaitForAlertAsync(
            ServerPairing pairing, int waitSeconds, CancellationToken cancellationToken)
        {
            AlertCalls++;
            return Task.FromResult(Alerts.Count > 0
                ? Alerts.Dequeue()
                : new ReadResult<FlaggedJoinAlert>(ReadOutcome.NothingWaiting, Elapsed: TimeSpan.FromSeconds(30)));
        }

        public Task<ReadResult<UserSummary>> GetUserAsync(
            ServerPairing pairing, string subjectId, CancellationToken cancellationToken)
            => throw new NotSupportedException();
    }

    private static ServerPairing Pairing(string serverId = "cats", string group = Group)
        => new(serverId, new Uri($"https://{serverId}.example"), "token", group);

    private static InstanceLocation Location(string instance = Instance, string? group = Group)
    {
        var raw = group is null
            ? $"wrld_4b34:{instance}~region(use)"
            : $"wrld_4b34:{instance}~group({group})~groupAccessType(members)~region(use)";

        Assert.True(InstanceLocation.TryParse(raw, out var location));
        return location;
    }

    private static InstanceContext Roster(params string[] names) => new(
        Instance,
        [.. names.Select(n => new RosterMember($"usr_{n}", n, RosterStanding.Ordinary, 0, []))]);

    private static FlaggedJoinAlert Alert(
        string subject = "usr_flag", string instance = Instance, string id = "a1")
        => new(id, subject, "Trouble", instance, "2 prior actions", 2,
            new DateTimeOffset(2026, 9, 12, 20, 0, 0, TimeSpan.Zero));

    private static (OverlayDriver Driver, CountingPresenter Presenter, ScriptedReads Reads, FakeClock Clock)
        Build(string label = "Cat Lounge")
    {
        var clock = new FakeClock();
        var presenter = new CountingPresenter();
        var reads = new ScriptedReads();
        var driver = new OverlayDriver(presenter, reads, clock);

        driver.Add(Pairing(), label);
        driver.EnteredInstance(Location());

        return (driver, presenter, reads, clock);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ATickThatChangesNothingDoesNotRasteriseAnything()
    {
        // The regression that costs nothing visible and throws away the whole performance
        // argument: an overlay texture is submitted on change and re-projected by the compositor
        // at headset rate, so redrawing unconditionally burns a core beside a game that wants it.
        var (driver, presenter, reads, clock) = Build();
        reads.Contexts.Enqueue(new ReadResult<InstanceContext>(ReadOutcome.Fetched, Roster("Rin")));

        await driver.TickAsync(Ct);
        var afterFirst = presenter.Draws;

        for (var i = 0; i < 200; i++)
            Assert.False((await driver.TickAsync(Ct)).Drew);

        Assert.Equal(afterFirst, presenter.Draws);
        Assert.True(presenter.Updates > 200, "the loop should still be running, just not drawing");
    }

    [Fact]
    public async Task ARosterThatChangedIsDrawn()
    {
        var (driver, presenter, reads, clock) = Build();
        reads.Contexts.Enqueue(new ReadResult<InstanceContext>(ReadOutcome.Fetched, Roster("Rin")));
        await driver.TickAsync(Ct);

        var before = presenter.Draws;
        reads.Contexts.Enqueue(new ReadResult<InstanceContext>(ReadOutcome.Fetched, Roster("Rin", "Mei")));
        clock.Advance(OverlayDriver.ContextRefreshInterval);

        Assert.True((await driver.TickAsync(Ct)).Drew);
        Assert.Equal(before + 1, presenter.Draws);
        Assert.Equal(2, presenter.Last.Roster.Value!.Members.Count);
    }

    [Fact]
    public async Task TheRosterIsNotReReadOnEveryTick()
    {
        // Ticking is cheap on purpose so it can run on a short timer; the reads are not.
        var (driver, _, reads, clock) = Build();

        for (var i = 0; i < 50; i++)
            await driver.TickAsync(Ct);

        Assert.Equal(1, reads.ContextCalls);

        clock.Advance(OverlayDriver.ContextRefreshInterval);
        await driver.TickAsync(Ct);

        Assert.Equal(2, reads.ContextCalls);
    }

    [Fact]
    public async Task AServerThatStopsAnsweringMidSessionKeepsRenderingWhatWasLastKnown()
    {
        // The overlay's highest-value moment is also the moment the network is worst. Stale data
        // with its age on screen is actionable; a blank card is not.
        var (driver, presenter, reads, clock) = Build();
        reads.Contexts.Enqueue(new ReadResult<InstanceContext>(ReadOutcome.Fetched, Roster("Rin")));
        await driver.TickAsync(Ct);

        clock.Advance(TimeSpan.FromMinutes(20));
        reads.Contexts.Enqueue(new ReadResult<InstanceContext>(ReadOutcome.Unreachable));
        await driver.TickAsync(Ct);

        var screen = presenter.Last;

        Assert.NotNull(screen.Roster.Value);
        Assert.Equal("Rin", screen.Roster.Value!.Members[0].DisplayName);
        Assert.Equal(Freshness.Stale, screen.Freshness);
        Assert.Equal("as of 20 minutes ago", screen.Roster.Describe());
        Assert.Contains("Cannot reach Cat Lounge", screen.Health);
    }

    [Fact]
    public async Task EveryPairedServerUnreachableFromTheStartStillRendersAScreen()
    {
        var (driver, presenter, reads, _) = Build();
        reads.Contexts.Enqueue(new ReadResult<InstanceContext>(ReadOutcome.Unreachable));

        await driver.TickAsync(Ct);

        // The only honest blank, and it is still a frame rather than nothing.
        Assert.True(presenter.Draws >= 1);
        Assert.Equal(Freshness.Never, presenter.Last.Freshness);
        Assert.Equal("not loaded", presenter.Last.Roster.Describe());
    }

    [Fact]
    public async Task GoingBackToStaleAndThenFreshRedrawsEachTime()
    {
        var (driver, presenter, reads, clock) = Build();
        reads.Contexts.Enqueue(new ReadResult<InstanceContext>(ReadOutcome.Fetched, Roster("Rin")));
        await driver.TickAsync(Ct);

        // The freshness label ticking over is itself a change worth a frame -- it is the thing
        // that stops a moderator acting on twenty-minute-old data believing it is current.
        var before = presenter.Draws;
        clock.Advance(TimeSpan.FromMinutes(5));
        await driver.TickAsync(Ct);

        Assert.True(presenter.Draws > before);
    }

    [Fact]
    public async Task ATokenRejectionStopsTheReadsAndSaysSoOnTheOverlay()
    {
        var (driver, presenter, reads, clock) = Build();
        reads.Contexts.Enqueue(new ReadResult<InstanceContext>(ReadOutcome.Unauthorised));

        await driver.TickAsync(Ct);
        Assert.Contains("rejected this device", presenter.Last.Health);

        // Terminal: not retried, however long the loop runs.
        clock.Advance(TimeSpan.FromHours(2));
        await driver.TickAsync(Ct);
        await driver.TickAsync(Ct);

        Assert.Equal(1, reads.ContextCalls);
    }

    [Fact]
    public async Task AnAlertForThisInstanceBecomesACard()
    {
        var (driver, presenter, reads, _) = Build();
        reads.Contexts.Enqueue(new ReadResult<InstanceContext>(ReadOutcome.Fetched, Roster("Rin")));
        reads.Alerts.Enqueue(new ReadResult<FlaggedJoinAlert>(
            ReadOutcome.Fetched, Alert(), TimeSpan.FromSeconds(4)));

        // The first tick starts the poll; the second harvests it.
        await driver.TickAsync(Ct);
        var tick = await driver.TickAsync(Ct);

        Assert.True(tick.AlertShown);
        Assert.Equal("usr_flag", presenter.Last.Alert!.SubjectId);
    }

    [Fact]
    public async Task AnAlertAboutARoomTheModeratorIsNotInIsDropped()
    {
        // The answer to "several servers, one headset": an alert is shown only when it names the
        // instance the moderator is standing in, so at most one server can ever qualify. A flagged
        // user walking into somewhere else is not something they can act on from here.
        var (driver, presenter, reads, _) = Build();
        reads.Contexts.Enqueue(new ReadResult<InstanceContext>(ReadOutcome.Fetched, Roster("Rin")));
        reads.Alerts.Enqueue(new ReadResult<FlaggedJoinAlert>(
            ReadOutcome.Fetched, Alert(instance: "somewhere-else"), TimeSpan.FromSeconds(4)));

        await driver.TickAsync(Ct);
        var tick = await driver.TickAsync(Ct);

        Assert.False(tick.AlertShown);
        Assert.Null(presenter.Last.Alert);
    }

    [Fact]
    public async Task TwoServersWithSomethingToSayAtOnceProduceOneCardFromTheRoomYouAreIn()
    {
        var clock = new FakeClock();
        var presenter = new CountingPresenter();
        var reads = new ScriptedReads();
        var driver = new OverlayDriver(presenter, reads, clock);

        driver.Add(Pairing("cats", Group), "Cat Lounge");
        driver.Add(Pairing("dogs", "grp_dogs"), "Dog Park");
        driver.EnteredInstance(Location());

        reads.Contexts.Enqueue(new ReadResult<InstanceContext>(ReadOutcome.Fetched, Roster("Rin")));
        reads.Alerts.Enqueue(new ReadResult<FlaggedJoinAlert>(
            ReadOutcome.Fetched, Alert(), TimeSpan.FromSeconds(4)));

        await driver.TickAsync(Ct);
        await driver.TickAsync(Ct);

        Assert.Equal("Cat Lounge", presenter.Last.GroupLabel);
        Assert.Equal("usr_flag", presenter.Last.Alert!.SubjectId);

        // The other server was never contacted at all -- not filtered on receipt, not asked.
        Assert.Equal(1, reads.ContextCalls);
    }

    [Fact]
    public async Task ACardClearsItselfRatherThanStayingUpForever()
    {
        var (driver, presenter, reads, clock) = Build();
        reads.Contexts.Enqueue(new ReadResult<InstanceContext>(ReadOutcome.Fetched, Roster("Rin")));
        reads.Alerts.Enqueue(new ReadResult<FlaggedJoinAlert>(
            ReadOutcome.Fetched, Alert(), TimeSpan.FromSeconds(4)));

        await driver.TickAsync(Ct);
        await driver.TickAsync(Ct);
        Assert.NotNull(presenter.Last.Alert);

        clock.Advance(OverlayDriver.AlertDwell);
        await driver.TickAsync(Ct);

        Assert.Null(presenter.Last.Alert);
    }

    [Fact]
    public async Task TheSamePersonDoesNotRaiseACardAgainWithinTheCooldown()
    {
        // Somebody rejoining repeatedly is exactly what being kicked looks like.
        var (driver, presenter, reads, clock) = Build();
        reads.Contexts.Enqueue(new ReadResult<InstanceContext>(ReadOutcome.Fetched, Roster("Rin")));
        reads.Alerts.Enqueue(new ReadResult<FlaggedJoinAlert>(
            ReadOutcome.Fetched, Alert(id: "a1"), TimeSpan.FromSeconds(4)));

        await driver.TickAsync(Ct);
        await driver.TickAsync(Ct);
        driver.Dismiss();

        reads.Alerts.Enqueue(new ReadResult<FlaggedJoinAlert>(
            ReadOutcome.Fetched, Alert(id: "a2"), TimeSpan.FromSeconds(4)));
        clock.Advance(TimeSpan.FromSeconds(30));

        await driver.TickAsync(Ct);
        var tick = await driver.TickAsync(Ct);

        Assert.False(tick.AlertShown);
        Assert.Null(presenter.Last.Alert);
    }

    [Fact]
    public async Task TheSamePersonCanRaiseACardOnceTheCooldownHasPassed()
    {
        var (driver, presenter, reads, clock) = Build();
        reads.Contexts.Enqueue(new ReadResult<InstanceContext>(ReadOutcome.Fetched, Roster("Rin")));
        reads.Alerts.Enqueue(new ReadResult<FlaggedJoinAlert>(
            ReadOutcome.Fetched, Alert(id: "a1"), TimeSpan.FromSeconds(4)));

        await driver.TickAsync(Ct);
        await driver.TickAsync(Ct);

        clock.Advance(OverlayDriver.AlertCooldown + TimeSpan.FromSeconds(1));
        reads.Alerts.Enqueue(new ReadResult<FlaggedJoinAlert>(
            ReadOutcome.Fetched, Alert(id: "a2"), TimeSpan.FromSeconds(4)));

        await driver.TickAsync(Ct);
        Assert.True((await driver.TickAsync(Ct)).AlertShown);
    }

    [Fact]
    public async Task DismissingACardMeansItDoesNotComeBack()
    {
        var (driver, presenter, reads, _) = Build();
        reads.Contexts.Enqueue(new ReadResult<InstanceContext>(ReadOutcome.Fetched, Roster("Rin")));
        reads.Alerts.Enqueue(new ReadResult<FlaggedJoinAlert>(
            ReadOutcome.Fetched, Alert(), TimeSpan.FromSeconds(4)));

        await driver.TickAsync(Ct);
        await driver.TickAsync(Ct);

        driver.Dismiss();
        await driver.TickAsync(Ct);

        Assert.Null(presenter.Last.Alert);
    }

    [Fact]
    public async Task LeavingTheInstanceClearsTheCardAboutIt()
    {
        // A card about the room you just left is worse than no card.
        var (driver, presenter, reads, _) = Build();
        reads.Contexts.Enqueue(new ReadResult<InstanceContext>(ReadOutcome.Fetched, Roster("Rin")));
        reads.Alerts.Enqueue(new ReadResult<FlaggedJoinAlert>(
            ReadOutcome.Fetched, Alert(), TimeSpan.FromSeconds(4)));

        await driver.TickAsync(Ct);
        await driver.TickAsync(Ct);
        Assert.NotNull(presenter.Last.Alert);

        driver.EnteredInstance(Location("85019"));
        await driver.TickAsync(Ct);

        Assert.Null(presenter.Last.Alert);
    }

    [Fact]
    public async Task AProxyKillingTheLongPollAtThirtySecondsIsNotTreatedAsAnOutage()
    {
        // The symptom of an idle-connection cap. Read as a failure it would back the one push
        // channel off into uselessness while still never delivering anything.
        var (driver, _, reads, _) = Build();
        reads.Contexts.Enqueue(new ReadResult<InstanceContext>(ReadOutcome.Fetched, Roster("Rin")));

        for (var i = 0; i < 6; i++)
        {
            reads.Alerts.Enqueue(new ReadResult<FlaggedJoinAlert>(
                ReadOutcome.Unreachable, Elapsed: TimeSpan.FromSeconds(29.5)));
        }

        for (var i = 0; i < 12; i++)
            await driver.TickAsync(Ct);

        // It kept polling rather than going quiet, and the alert channel is still usable.
        Assert.True(reads.AlertCalls >= 5, $"only polled {reads.AlertCalls} times");

        reads.Alerts.Enqueue(new ReadResult<FlaggedJoinAlert>(
            ReadOutcome.Fetched, Alert(), TimeSpan.FromSeconds(3)));

        await driver.TickAsync(Ct);
        Assert.True((await driver.TickAsync(Ct)).AlertShown);
    }

    [Fact]
    public async Task AnImmediateFailureBacksOffRatherThanSpinning()
    {
        // The opposite case: a server that refuses instantly must not become a tight loop of
        // requests on a machine that is also running a game.
        var (driver, _, reads, _) = Build();
        reads.Contexts.Enqueue(new ReadResult<InstanceContext>(ReadOutcome.Fetched, Roster("Rin")));

        for (var i = 0; i < 20; i++)
        {
            reads.Alerts.Enqueue(new ReadResult<FlaggedJoinAlert>(
                ReadOutcome.Unreachable, Elapsed: TimeSpan.Zero));
        }

        for (var i = 0; i < 40; i++)
            await driver.TickAsync(Ct);

        Assert.True(reads.AlertCalls < 5, $"polled {reads.AlertCalls} times while backing off");
    }

    [Fact]
    public async Task OutsideAGroupInstanceNothingIsContactedAndTheIdleScreenIsShown()
    {
        // A moderator's public, friends-only and private VRChat use. The overlay has nothing to
        // say and asks nobody about it.
        var (driver, presenter, reads, _) = Build();

        driver.EnteredInstance(Location(group: null));
        await driver.TickAsync(Ct);
        await driver.TickAsync(Ct);

        Assert.Equal(0, reads.ContextCalls);
        Assert.Equal(0, reads.AlertCalls);
        Assert.Null(presenter.Last.GroupLabel);
        Assert.Null(driver.CurrentServer);
    }

    [Fact]
    public async Task AnInstanceOwnedByAGroupNoPairedServerManagesIsAlsoIdle()
    {
        var (driver, presenter, reads, _) = Build();

        driver.EnteredInstance(Location(group: "grp_somebody_else"));
        await driver.TickAsync(Ct);

        Assert.Equal(0, reads.ContextCalls);
        Assert.Null(presenter.Last.GroupLabel);
    }

    [Fact]
    public async Task TheOverlayFollowsTheModeratorBetweenGroups()
    {
        var clock = new FakeClock();
        var presenter = new CountingPresenter();
        var reads = new ScriptedReads();
        var driver = new OverlayDriver(presenter, reads, clock);

        driver.Add(Pairing("cats", Group), "Cat Lounge");
        driver.Add(Pairing("dogs", "grp_dogs"), "Dog Park");

        driver.EnteredInstance(Location());
        reads.Contexts.Enqueue(new ReadResult<InstanceContext>(ReadOutcome.Fetched, Roster("Rin")));
        await driver.TickAsync(Ct);
        Assert.Equal("Cat Lounge", presenter.Last.GroupLabel);

        driver.EnteredInstance(Location("85019", "grp_dogs"));
        reads.Contexts.Enqueue(new ReadResult<InstanceContext>(ReadOutcome.Fetched, Roster("Kai")));
        await driver.TickAsync(Ct);

        Assert.Equal("Dog Park", presenter.Last.GroupLabel);
        Assert.Equal("dogs", driver.CurrentServer!.ServerId);
    }

    [Fact]
    public async Task RemovingAPairingLeavesNothingOfThatGroupsContext()
    {
        var (driver, presenter, reads, _) = Build();
        reads.Contexts.Enqueue(new ReadResult<InstanceContext>(ReadOutcome.Fetched, Roster("Rin")));
        await driver.TickAsync(Ct);

        driver.Remove("cats");
        await driver.TickAsync(Ct);

        Assert.Null(driver.CurrentServer);
        Assert.Null(presenter.Last.Roster.Value);
    }
}
