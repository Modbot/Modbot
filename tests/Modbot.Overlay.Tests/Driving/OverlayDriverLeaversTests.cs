using Modbot.Companion.Ingest;
using Modbot.Companion.Instances;
using Modbot.Companion.Overlay;
using Modbot.Overlay.Driving;
using Modbot.Overlay.Interaction;
using Modbot.Overlay.Views;
using Modbot.TestSupport;

namespace Modbot.Overlay.Tests.Driving;

/// <summary>
/// The loop's side of the rows for people who just left: who it notices gone from a roster read,
/// how long it holds them, and when it lets them go, all on the app's own clock.
/// </summary>
public class OverlayDriverLeaversTests
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

        public Task<ReadResult<InstanceContext>> GetContextAsync(ServerPairing pairing, string instanceId, string? worldId, CancellationToken cancellationToken)
            => Task.FromResult(Contexts.Count > 0 ? Contexts.Dequeue() : new ReadResult<InstanceContext>(ReadOutcome.Unreachable));

        public Task<ReadResult<LivePollPage>> PollLiveAsync(ServerPairing pairing, string instanceId, string? worldId, string? after, int waitSeconds, CancellationToken cancellationToken)
            => Task.FromResult(new ReadResult<LivePollPage>(ReadOutcome.NothingWaiting, Elapsed: TimeSpan.FromSeconds(waitSeconds)));

        public Task<ReadResult<UserSummary>> GetUserAsync(ServerPairing pairing, string subjectId, CancellationToken cancellationToken)
            => Task.FromResult(new ReadResult<UserSummary>(ReadOutcome.Unreachable));
    }

    private static InstanceLocation Location(string instance = Instance)
    {
        Assert.True(InstanceLocation.TryParse($"wrld_4b34:{instance}~group({Group})~groupAccessType(members)~region(use)", out var location));
        return location;
    }

    private static readonly RosterMember Rin = new("usr_rin", "Rin", RosterStanding.Flagged, 2, ["prior kick"]);
    private static readonly RosterMember Kai = new("usr_kai", "Kai", RosterStanding.Staff, 0, []);

    private static ReadResult<InstanceContext> Read(params RosterMember[] members)
        => new(ReadOutcome.Fetched, new InstanceContext(Instance, members));

    private static (OverlayDriver Driver, RecordingPresenter Presenter, Reads Reads, FakeClock Clock) Build()
    {
        var presenter = new RecordingPresenter();
        var reads = new Reads();
        var clock = new FakeClock();
        var driver = new OverlayDriver(presenter, reads, clock);
        driver.Add(new ServerPairing("cats", new Uri("https://cats.example"), "token", Group), "Cat Lounge");
        driver.EnteredInstance(Location());
        reads.Contexts.Enqueue(Read(Rin, Kai));
        return (driver, presenter, reads, clock);
    }

    /// <summary>The next roster read, which is due once the refresh interval has passed.</summary>
    private static async Task NextReadAsync(OverlayDriver driver, Reads reads, FakeClock clock, ReadResult<InstanceContext> read)
    {
        reads.Contexts.Enqueue(read);
        clock.Advance(OverlayDriver.ContextRefreshInterval + TimeSpan.FromSeconds(1));
        await driver.TickAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task NobodyHasLeftOnTheFirstRead()
    {
        var (driver, presenter, _, _) = Build();

        await driver.TickAsync(TestContext.Current.CancellationToken);

        Assert.Empty(presenter.Last.LeftOrNone);
        Assert.Equal(2, presenter.Last.Roster.Value?.Members.Count);
    }

    [Fact]
    public async Task SomebodyTheNextReadDropsIsKeptAsJustLeftAndIsNotInTheRoster()
    {
        var (driver, presenter, reads, clock) = Build();
        await driver.TickAsync(TestContext.Current.CancellationToken);

        await NextReadAsync(driver, reads, clock, Read(Kai));

        var left = Assert.Single(presenter.Last.LeftOrNone);
        Assert.Equal("usr_rin", left.Member.SubjectId);
        Assert.Equal(60, left.SecondsLeft(clock.UtcNow));

        // The count is of who is here: one.
        Assert.Equal(["usr_kai"], presenter.Last.Roster.Value?.Members.Select(m => m.SubjectId));
    }

    [Fact]
    public async Task TheRowGoesAfterAMinuteOnTheAppsClock()
    {
        var (driver, presenter, reads, clock) = Build();
        await driver.TickAsync(TestContext.Current.CancellationToken);
        await NextReadAsync(driver, reads, clock, Read(Kai));
        Assert.Single(presenter.Last.LeftOrNone);

        clock.Advance(TimeSpan.FromSeconds(40));
        await driver.TickAsync(TestContext.Current.CancellationToken);
        Assert.Single(presenter.Last.LeftOrNone);

        clock.Advance(TimeSpan.FromSeconds(21));
        await driver.TickAsync(TestContext.Current.CancellationToken);
        Assert.Empty(presenter.Last.LeftOrNone);
    }

    [Fact]
    public async Task SomebodyWhoComesBackWithinAMinuteHasTheirOrdinaryRowAndNoLeftRow()
    {
        var (driver, presenter, reads, clock) = Build();
        await driver.TickAsync(TestContext.Current.CancellationToken);
        await NextReadAsync(driver, reads, clock, Read(Kai));
        Assert.Single(presenter.Last.LeftOrNone);

        await NextReadAsync(driver, reads, clock, Read(Rin, Kai));

        Assert.Empty(presenter.Last.LeftOrNone);
        Assert.Equal(2, presenter.Last.Roster.Value?.Members.Count);
    }

    [Fact]
    public async Task AnotherInstanceStartsWithNoLeftRows()
    {
        var (driver, presenter, reads, clock) = Build();
        await driver.TickAsync(TestContext.Current.CancellationToken);
        await NextReadAsync(driver, reads, clock, Read(Kai));
        Assert.Single(presenter.Last.LeftOrNone);

        driver.EnteredInstance(Location("40000"));
        await driver.TickAsync(TestContext.Current.CancellationToken);

        Assert.Empty(presenter.Last.LeftOrNone);
    }

    [Fact]
    public async Task TappingARowThatJustLeftOpensTheirCardFromTheRowItHad()
    {
        var (driver, presenter, reads, clock) = Build();
        await driver.TickAsync(TestContext.Current.CancellationToken);
        await NextReadAsync(driver, reads, clock, Read(Kai));

        driver.Tap(new OverlayTarget.Person("usr_rin"));
        await driver.TickAsync(TestContext.Current.CancellationToken);

        Assert.Equal(OverlayPage.Person, presenter.Last.Page);
        Assert.Equal("Rin", presenter.Last.Person?.DisplayName);
        Assert.Equal(RosterStanding.Flagged, presenter.Last.Person?.Standing);
    }

    [Fact]
    public async Task ScrollingCountsTheRowsThatJustLeftToo()
    {
        var (driver, presenter, reads, clock) = Build();
        await driver.TickAsync(TestContext.Current.CancellationToken);
        await NextReadAsync(driver, reads, clock, Read(Kai));

        // Two rows on the list now, Rin (left) and Kai, so one scroll step is allowed.
        driver.ScrollRoster(5);
        await driver.TickAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, presenter.Last.RosterSkip);
    }
}
