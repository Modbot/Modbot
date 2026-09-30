using Modbot.Companion.Ingest;
using Modbot.Companion.Instances;
using Modbot.Companion.Overlay;
using Modbot.Core.Users;
using Modbot.Overlay.Driving;
using Modbot.Overlay.Interaction;
using Modbot.Overlay.Views;
using Modbot.TestSupport;

namespace Modbot.Overlay.Tests.Driving;

/// <summary>
/// The filter row's taps, as the loop keeps them: a filter opens and closes, a choice is taken,
/// each list keeps its own, the roster scrolls inside what is left, and another instance starts
/// with the whole list again.
/// </summary>
public class OverlayDriverFilterTests
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

        public void Show() { }

        public void Hide() { }
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

    private static InstanceContext Roster() => new(
        Instance,
        [
            new RosterMember("usr_Rin", "Rin", RosterStanding.Flagged, 2, [], TrustRank.NewUser),
            new RosterMember("usr_Kai", "Kai", RosterStanding.Staff, 0, [], TrustRank.TrustedUser),
            new RosterMember("usr_Mira", "Mira", RosterStanding.Member, 0, [], TrustRank.KnownUser),
            new RosterMember("usr_Jo", "Jo", RosterStanding.Ordinary, 0, [], TrustRank.NewUser),
            new RosterMember("usr_Ash", "Ash", RosterStanding.Ordinary, 0, [], null),
        ]);

    private static async Task<(OverlayDriver Driver, RecordingPresenter Presenter, FakeClock Clock)> Build()
    {
        var presenter = new RecordingPresenter();
        var reads = new Reads();
        var clock = new FakeClock();
        var driver = new OverlayDriver(presenter, reads, clock);
        driver.Add(new ServerPairing("cats", new Uri("https://cats.example"), "token", Group), "Cat Lounge");
        driver.EnteredInstance(Location());
        reads.Contexts.Enqueue(new ReadResult<InstanceContext>(ReadOutcome.Fetched, Roster()));
        await driver.TickAsync(TestContext.Current.CancellationToken);
        return (driver, presenter, clock);
    }

    [Fact]
    public async Task TappingAFilterShowsItsChoicesAndTappingItAgainHidesThem()
    {
        var (driver, presenter, _) = await Build();

        driver.Tap(new OverlayTarget.Filter(OverlayPage.Instance, FilterPart.Rank));
        await driver.TickAsync(TestContext.Current.CancellationToken);
        Assert.Equal(FilterPart.Rank, presenter.Last.RosterFiltersOrNone.Open);

        driver.Tap(new OverlayTarget.Filter(OverlayPage.Instance, FilterPart.Rank));
        await driver.TickAsync(TestContext.Current.CancellationToken);
        Assert.Null(presenter.Last.RosterFiltersOrNone.Open);
    }

    [Fact]
    public async Task AOneChoiceFilterTakesItAndCloses()
    {
        var (driver, presenter, _) = await Build();

        driver.Tap(new OverlayTarget.Filter(OverlayPage.Instance, FilterPart.Who));
        driver.Tap(new OverlayTarget.Pick(OverlayPage.Instance, FilterPart.Who, (int)Who.Staff));
        await driver.TickAsync(TestContext.Current.CancellationToken);

        Assert.Equal(Who.Staff, presenter.Last.RosterFiltersOrNone.Who);
        Assert.Null(presenter.Last.RosterFiltersOrNone.Open);
    }

    [Fact]
    public async Task RanksAreTickedOneAtATimeAndTheChoicesStayOpen()
    {
        var (driver, _, _) = await Build();

        driver.Tap(new OverlayTarget.Filter(OverlayPage.Instance, FilterPart.Rank));
        driver.Tap(new OverlayTarget.Pick(OverlayPage.Instance, FilterPart.Rank, (int)TrustRank.NewUser));
        driver.Tap(new OverlayTarget.Pick(OverlayPage.Instance, FilterPart.Rank, -1));

        Assert.True(driver.RosterFilters.Ranks.Has(TrustRank.NewUser));
        Assert.True(driver.RosterFilters.Ranks.Has(null));
        Assert.Equal(FilterPart.Rank, driver.RosterFilters.Open);

        driver.Tap(new OverlayTarget.Pick(OverlayPage.Instance, FilterPart.Rank, (int)TrustRank.NewUser));
        Assert.False(driver.RosterFilters.Ranks.Has(TrustRank.NewUser));
    }

    [Fact]
    public async Task EachListKeepsItsOwnFilters()
    {
        var (driver, presenter, _) = await Build();

        driver.Tap(new OverlayTarget.Pick(OverlayPage.Instance, FilterPart.Who, (int)Who.Flagged));
        driver.Tap(new OverlayTarget.Pick(OverlayPage.Events, FilterPart.Kind, 1));
        await driver.TickAsync(TestContext.Current.CancellationToken);

        Assert.Equal(Who.Flagged, presenter.Last.RosterFiltersOrNone.Who);
        Assert.True(presenter.Last.RosterFiltersOrNone.Kinds.IsEmpty);
        Assert.Equal(Who.All, presenter.Last.EventFiltersOrNone.Who);
        Assert.True(presenter.Last.EventFiltersOrNone.Kinds.Has(LiveEventKinds.PersonLeft));
    }

    [Fact]
    public async Task ClearPutsTheWholeListBack()
    {
        var (driver, _, _) = await Build();

        driver.Tap(new OverlayTarget.Pick(OverlayPage.Instance, FilterPart.Who, (int)Who.Flagged));
        driver.Tap(new OverlayTarget.Pick(OverlayPage.Instance, FilterPart.Sort, (int)RosterOrder.Name));
        driver.SetName(OverlayPage.Instance, "ri");

        driver.Tap(new OverlayTarget.ClearFilters(OverlayPage.Instance));

        Assert.Equal(ListFilters.None, driver.RosterFilters);
    }

    [Fact]
    public async Task AnotherInstanceStartsWithTheWholeListAgain()
    {
        var (driver, _, _) = await Build();

        driver.Tap(new OverlayTarget.Pick(OverlayPage.Instance, FilterPart.Who, (int)Who.Flagged));
        driver.Tap(new OverlayTarget.Pick(OverlayPage.Events, FilterPart.Kind, 0));

        driver.EnteredInstance(Location("40022"));

        Assert.Equal(ListFilters.None, driver.RosterFilters);
        Assert.Equal(ListFilters.None, driver.EventFilters);
    }

    [Fact]
    public async Task ANameIsKeptTrimmedAndBlankClearsIt()
    {
        var (driver, _, _) = await Build();

        driver.SetName(OverlayPage.Instance, "  ri  ");
        Assert.Equal("ri", driver.RosterFilters.Name);

        driver.SetName(OverlayPage.Instance, "   ");
        Assert.Null(driver.RosterFilters.Name);

        driver.SetName(OverlayPage.Instance, new string('x', 100));
        Assert.Equal(ListFilters.LongestName, driver.RosterFilters.Name!.Length);
    }

    [Fact]
    public async Task ClearingTheNameClosesItsBox()
    {
        var (driver, _, _) = await Build();

        driver.Tap(new OverlayTarget.Filter(OverlayPage.Instance, FilterPart.Name));
        driver.SetName(OverlayPage.Instance, "ri");
        driver.Tap(new OverlayTarget.Pick(OverlayPage.Instance, FilterPart.Name, 0));

        Assert.Null(driver.RosterFilters.Name);
        Assert.Null(driver.RosterFilters.Open);
    }

    [Fact]
    public async Task AChoiceThatIsNotOneIsIgnored()
    {
        var (driver, _, _) = await Build();

        driver.Tap(new OverlayTarget.Pick(OverlayPage.Instance, FilterPart.Who, 99));
        driver.Tap(new OverlayTarget.Pick(OverlayPage.Events, FilterPart.Kind, 99));

        Assert.Equal(ListFilters.None, driver.RosterFilters);
        Assert.Equal(ListFilters.None, driver.EventFilters);
    }

    [Fact]
    public async Task TheRosterScrollsOnlyAsFarAsTheFiltersLeaveIt()
    {
        var (driver, _, _) = await Build();

        driver.Tap(new OverlayTarget.Pick(OverlayPage.Instance, FilterPart.Rank, (int)TrustRank.NewUser));
        driver.ScrollRoster(10);

        // Two people left: Rin and Jo. The last one can be scrolled to the top, and no further.
        Assert.Equal(1, driver.RosterSkip);
    }

    [Fact]
    public async Task ChangingWhatIsShownTakesTheRosterBackToTheTop()
    {
        var (driver, _, _) = await Build();

        driver.ScrollRoster(3);
        Assert.Equal(3, driver.RosterSkip);

        driver.Tap(new OverlayTarget.Pick(OverlayPage.Instance, FilterPart.Who, (int)Who.NotInGroup));
        Assert.Equal(0, driver.RosterSkip);
    }

    [Fact]
    public async Task OpeningAFilterLeavesTheScrollWhereItWas()
    {
        var (driver, _, _) = await Build();

        driver.ScrollRoster(2);
        driver.Tap(new OverlayTarget.Filter(OverlayPage.Instance, FilterPart.Rank));

        Assert.Equal(2, driver.RosterSkip);
    }

    [Fact]
    public async Task ArrivalTimesReachTheScreenForPeopleOnTheRoster()
    {
        var (driver, presenter, clock) = await Build();
        var local = TimeZoneInfo.ConvertTime(clock.UtcNow, TimeZoneInfo.Local).DateTime;

        driver.ArrivedAt = new Dictionary<string, DateTime?>
        {
            ["usr_Rin"] = local.AddMinutes(-3),
            ["usr_Kai"] = null,

            // Somebody the log saw who is not on the server's roster is left off.
            ["usr_Nobody"] = local.AddMinutes(-1),
        };
        await driver.TickAsync(TestContext.Current.CancellationToken);

        var arrivals = presenter.Last.ArrivalsOrNone;
        Assert.Equal(2, arrivals.Count);
        Assert.Equal(clock.UtcNow.AddMinutes(-3), arrivals["usr_Rin"]);
        Assert.Null(arrivals["usr_Kai"]);
        Assert.Equal(clock.UtcNow, presenter.Last.Now);
    }
}
