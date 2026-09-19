using Modbot.Companion.Ingest;
using Modbot.Companion.Instances;
using Modbot.Companion.Overlay;
using Modbot.Overlay.Driving;
using Modbot.Overlay.Interaction;
using Modbot.Overlay.Views;
using Modbot.TestSupport;

namespace Modbot.Overlay.Tests.Driving;

/// <summary>
/// What a tap on the panel does to the loop: the alert goes, a person's card opens with the
/// row's own facts and then the server's, a tap elsewhere closes it, the roster scrolls within
/// its rows, and leaving the instance clears the lot.
/// </summary>
public class OverlayDriverTapTests
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

        public Queue<ReadResult<LivePollPage>> Live { get; } = new();

        public Dictionary<string, ReadResult<UserSummary>> Users { get; } = new(StringComparer.Ordinal);

        public List<string> UsersAsked { get; } = [];

        public Task<ReadResult<InstanceContext>> GetContextAsync(ServerPairing pairing, string instanceId, CancellationToken cancellationToken)
            => Task.FromResult(Contexts.Count > 0 ? Contexts.Dequeue() : new ReadResult<InstanceContext>(ReadOutcome.Unreachable));

        public Task<ReadResult<LivePollPage>> PollLiveAsync(ServerPairing pairing, string instanceId, string? after, int waitSeconds, CancellationToken cancellationToken)
            => Task.FromResult(Live.Count > 0 ? Live.Dequeue() : new ReadResult<LivePollPage>(ReadOutcome.NothingWaiting, Elapsed: TimeSpan.FromSeconds(waitSeconds)));

        public Task<ReadResult<UserSummary>> GetUserAsync(ServerPairing pairing, string subjectId, CancellationToken cancellationToken)
        {
            UsersAsked.Add(subjectId);
            return Task.FromResult(Users.TryGetValue(subjectId, out var user) ? user : new ReadResult<UserSummary>(ReadOutcome.Unreachable));
        }
    }

    private static InstanceLocation Location(string instance = Instance)
    {
        Assert.True(InstanceLocation.TryParse($"wrld_4b34:{instance}~group({Group})~groupAccessType(members)~region(use)", out var location));
        return location;
    }

    private static InstanceContext Roster(params string[] names) => new(
        Instance,
        [.. names.Select(n => new RosterMember($"usr_{n}", n, RosterStanding.Member, 0, []))]);

    private static (OverlayDriver Driver, RecordingPresenter Presenter, Reads Reads) Build()
    {
        var presenter = new RecordingPresenter();
        var reads = new Reads();
        var driver = new OverlayDriver(presenter, reads, new FakeClock());
        driver.Add(
            new ServerPairing("cats", new Uri("https://cats.example"), "token", Group)
            {
                ManagedGroupName = "Cat Lounge",
                ManagedGroupIconUrl = "https://cats.example/icon.png",
            },
            "Cat Lounge");
        driver.EnteredInstance(Location());
        reads.Contexts.Enqueue(new ReadResult<InstanceContext>(ReadOutcome.Fetched, Roster("Rin", "Kai", "Mira")));
        return (driver, presenter, reads);
    }

    [Fact]
    public async Task TappingARowOpensThatPersonWithTheRowsFactsThenTheServers()
    {
        var (driver, presenter, reads) = Build();
        await driver.TickAsync(TestContext.Current.CancellationToken);
        reads.Users["usr_Kai"] = new ReadResult<UserSummary>(
            ReadOutcome.Fetched,
            new UserSummary("usr_Kai", "Kai", RosterStanding.Staff, 1, DateTimeOffset.UnixEpoch, [], ["Mod"]));

        await driver.OpenPersonAsync("usr_Kai");
        await driver.TickAsync(TestContext.Current.CancellationToken);

        Assert.Equal(["usr_Kai"], reads.UsersAsked);
        Assert.NotNull(presenter.Last.Person);
        Assert.Equal("usr_Kai", presenter.Last.Person.SubjectId);
        Assert.Equal(RosterStanding.Staff, presenter.Last.Person.Standing);
        Assert.Equal(["Mod"], presenter.Last.Person.Roles);
    }

    [Fact]
    public async Task AnUnreachableServerStillOpensTheCardFromTheRow()
    {
        var (driver, presenter, _) = Build();
        await driver.TickAsync(TestContext.Current.CancellationToken);

        await driver.OpenPersonAsync("usr_Rin");
        await driver.TickAsync(TestContext.Current.CancellationToken);

        Assert.Equal("Rin", presenter.Last.Person?.DisplayName);
        Assert.Equal(RosterStanding.Member, presenter.Last.Person?.Standing);
    }

    [Fact]
    public async Task BackClosesTheCardAndReturnsToTheRoster()
    {
        // A tap on the empty panel used to close the card, because the card sat above the roster.
        // The person is now a screen of its own, so it takes Back to leave it -- and a stray tap
        // on nothing must not throw the moderator out of the card they just opened.
        var (driver, presenter, _) = Build();
        await driver.TickAsync(TestContext.Current.CancellationToken);
        await driver.OpenPersonAsync("usr_Rin");

        driver.Tap(null);
        await driver.TickAsync(TestContext.Current.CancellationToken);
        Assert.NotNull(presenter.Last.Person);
        Assert.Equal(OverlayPage.Person, presenter.Last.Page);

        driver.Tap(new OverlayTarget.ClosePerson());
        await driver.TickAsync(TestContext.Current.CancellationToken);

        Assert.Null(presenter.Last.Person);
        Assert.Equal(OverlayPage.Instance, presenter.Last.Page);
    }

    [Fact]
    public async Task TappingTheAlertDismissesIt()
    {
        var (driver, presenter, reads) = Build();
        reads.Live.Enqueue(new ReadResult<LivePollPage>(
            ReadOutcome.Fetched,
            new LivePollPage(
            [
                new LiveEvent("a1", "a1", LiveEventKinds.FlaggedJoin, DateTimeOffset.UnixEpoch, Instance,
                    new LivePerson("usr_Rin", "Rin", null, RosterStanding.Flagged, 2, ["kicked before"]),
                    true, "kicked before", false),
            ], "a1", false)));

        // The first tick starts the poll; the second harvests it.
        await driver.TickAsync(TestContext.Current.CancellationToken);
        await driver.TickAsync(TestContext.Current.CancellationToken);
        Assert.NotNull(presenter.Last.Alert);

        driver.Tap(new OverlayTarget.DismissAlert());
        await driver.TickAsync(TestContext.Current.CancellationToken);

        Assert.Null(presenter.Last.Alert);
    }

    [Fact]
    public async Task TheEventsTabShowsTheEventsScreenAndTheInstanceTabComesBack()
    {
        // The tab is the only way to reach the Events screen now that the feed under the window
        // over VRChat is gone, so the press reaching the loop is worth pinning down.
        var (driver, presenter, _) = Build();
        await driver.TickAsync(TestContext.Current.CancellationToken);
        Assert.Equal(OverlayPage.Instance, presenter.Last.Page);

        driver.Tap(new OverlayTarget.GoTo(OverlayPage.Events));
        await driver.TickAsync(TestContext.Current.CancellationToken);
        Assert.Equal(OverlayPage.Events, driver.Page);
        Assert.Equal(OverlayPage.Events, presenter.Last.Page);

        driver.Tap(new OverlayTarget.GoTo(OverlayPage.Instance));
        await driver.TickAsync(TestContext.Current.CancellationToken);
        Assert.Equal(OverlayPage.Instance, presenter.Last.Page);
    }

    [Fact]
    public async Task ThePersonTabWithNobodyOpenShowsTheInstanceRatherThanABlankCard()
    {
        var (driver, presenter, _) = Build();
        await driver.TickAsync(TestContext.Current.CancellationToken);

        driver.Tap(new OverlayTarget.GoTo(OverlayPage.Person));
        await driver.TickAsync(TestContext.Current.CancellationToken);

        Assert.Equal(OverlayPage.Instance, presenter.Last.Page);
    }

    [Fact]
    public async Task ThePanelSaysWhoseCommunityItIsAndWhereItsIconIs()
    {
        // The group's name and its icon, never the address of the machine the server runs on.
        var (driver, presenter, _) = Build();
        await driver.TickAsync(TestContext.Current.CancellationToken);

        Assert.Equal("Cat Lounge", presenter.Last.GroupLabel);
        Assert.Equal("https://cats.example/icon.png", presenter.Last.GroupIconUrl);
    }

    [Fact]
    public async Task TheRosterScrollsWithinItsRowsAndLeavingResetsEverything()
    {
        var (driver, presenter, _) = Build();
        await driver.TickAsync(TestContext.Current.CancellationToken);

        driver.ScrollRoster(5);
        Assert.Equal(2, driver.RosterSkip);
        driver.ScrollRoster(-10);
        Assert.Equal(0, driver.RosterSkip);
        driver.ScrollRoster(1);
        await driver.OpenPersonAsync("usr_Rin");
        await driver.TickAsync(TestContext.Current.CancellationToken);
        Assert.Equal(1, presenter.Last.RosterSkip);

        driver.EnteredInstance(Location("40000"));

        Assert.Equal(0, driver.RosterSkip);
        Assert.Null(driver.Person);
    }
}
