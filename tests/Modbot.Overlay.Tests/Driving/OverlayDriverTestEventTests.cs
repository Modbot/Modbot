using Modbot.Companion.Ingest;
using Modbot.Companion.Instances;
using Modbot.Companion.Overlay;
using Modbot.Overlay.Driving;
using Modbot.Overlay.Views;
using Modbot.TestSupport;

namespace Modbot.Overlay.Tests.Driving;

/// <summary>
/// The Debug page's test events reach the overlay's loop as if a paired server had sent them: on
/// the main panel and the notification overlay, with nothing asked of any server.
/// </summary>
public class OverlayDriverTestEventTests
{
    private const string Group = "grp_cats";
    private const string Instance = "39911";

    private sealed class Heard : IOverlayListener
    {
        public List<string> Alerts { get; } = [];

        public void AlertShown(FlaggedJoinAlert alert) => Alerts.Add(alert.SubjectId);

        public void TokenRejected(string label) { }
    }

    private sealed class LastScreen : IOverlayPresenter
    {
        public OverlayScreen? Screen { get; private set; }

        public bool Update(OverlayScreen screen)
        {
            Screen = screen;
            return true;
        }

        public void Show() { }

        public void Hide() { }
    }

    /// <summary>Counts every read, so a test event can be shown to ask the server nothing.</summary>
    private sealed class CountedReads : IOverlayReadClient
    {
        public int ContextReads { get; private set; }

        public Task<ReadResult<InstanceContext>> GetContextAsync(ServerPairing pairing, string instanceId, string? worldId, CancellationToken cancellationToken)
        {
            ContextReads++;
            return Task.FromResult(new ReadResult<InstanceContext>(ReadOutcome.Fetched, new InstanceContext(instanceId, [])));
        }

        public Task<ReadResult<LivePollPage>> PollLiveAsync(ServerPairing pairing, string instanceId, string? worldId, string? after, int waitSeconds, CancellationToken cancellationToken)
            => Task.FromResult(new ReadResult<LivePollPage>(ReadOutcome.NothingWaiting, Elapsed: TimeSpan.FromSeconds(waitSeconds)));

        public int UserReads { get; private set; }

        public Task<ReadResult<UserSummary>> GetUserAsync(ServerPairing pairing, string subjectId, CancellationToken cancellationToken)
        {
            UserReads++;
            return Task.FromResult(new ReadResult<UserSummary>(ReadOutcome.Unreachable));
        }
    }

    private sealed class CountedHeadsUps : IHeadsUpClient
    {
        public int Sent { get; private set; }

        public Task<HeadsUpSent> PlaceAsync(ServerPairing pairing, string instanceId, string? worldId, HeadsUpDraft draft, CancellationToken cancellationToken)
        {
            Sent++;
            return Task.FromResult(new HeadsUpSent(HeadsUpSendOutcome.Done));
        }

        public Task<HeadsUpSent> ClearAsync(ServerPairing pairing, string id, CancellationToken cancellationToken)
        {
            Sent++;
            return Task.FromResult(new HeadsUpSent(HeadsUpSendOutcome.Done));
        }
    }

    private static async Task<(OverlayDriver Driver, CountedReads Reads, CountedHeadsUps HeadsUps)> InACoveredInstance()
    {
        var clock = new FakeClock();
        var reads = new CountedReads();
        var headsUps = new CountedHeadsUps();
        var driver = new OverlayDriver(new LastScreen(), reads, clock, new Heard(), headsUps: headsUps);
        driver.Add(new ServerPairing("cats", new Uri("https://cats.example"), "token", Group), "Cat Lounge");
        driver.EnteredInstance(Location());
        await driver.TickAsync(Ct);
        return (driver, reads, headsUps);
    }

    [Fact]
    public async Task TappingATestPersonNeverAsksTheServer()
    {
        var (driver, reads, _) = await InACoveredInstance();
        driver.TakeTestEvent(TestFlagged());

        driver.Tap(new Modbot.Overlay.Interaction.OverlayTarget.Person(TestPeople.Prefix + "rin"));
        driver.Tap(new Modbot.Overlay.Interaction.OverlayTarget.RefreshPerson());
        await driver.OpenPersonAsync(TestPeople.Prefix + "rin");

        Assert.Equal(0, reads.UserReads);

        // The card is what the Events list already holds.
        Assert.Equal("Rin", driver.Person?.DisplayName);
        Assert.Equal(OverlayPage.Person, driver.Page);
    }

    [Fact]
    public async Task ARealPersonIsStillAskedAbout()
    {
        var (driver, reads, _) = await InACoveredInstance();

        await driver.OpenPersonAsync("usr_real");

        Assert.Equal(1, reads.UserReads);
    }

    [Fact]
    public async Task AHeadsUpOnATestPersonIsNeverSent()
    {
        var (driver, _, headsUps) = await InACoveredInstance();

        driver.Tap(new Modbot.Overlay.Interaction.OverlayTarget.AddHeadsUp(TestPeople.Prefix + "rin"));
        driver.SetHeadsUpText("watch the mirror");
        await driver.PlaceHeadsUpAsync();

        Assert.Equal(0, headsUps.Sent);
        Assert.NotNull(driver.Draft?.Problem);
    }

    private static InstanceLocation Location()
    {
        Assert.True(InstanceLocation.TryParse($"wrld_4b34:{Instance}~group({Group})~groupAccessType(members)~region(use)", out var location));
        return location;
    }

    /// <summary>A test flagged join, placed nowhere in particular: the driver puts it where the moderator is.</summary>
    private static LiveEvent TestFlagged(string id = "test-1") => new(
        id, id, LiveEventKinds.FlaggedJoin, new DateTimeOffset(2026, 10, 4, 20, 0, 0, TimeSpan.Zero), "00000",
        new LivePerson(TestPeople.Prefix + "rin", "Rin", "User", RosterStanding.Flagged, 0, [], true),
        true, "two kicks", false, "wrld_test");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void WithNoPairedServerHereItIsNotTaken()
    {
        var clock = new FakeClock();
        var popUps = new PopUps(clock);
        var driver = new OverlayDriver(new LastScreen(), new CountedReads(), clock, new Heard(), popUps: popUps);

        Assert.False(driver.TakeTestEvent(TestFlagged()));

        driver.EnteredInstance(Location());
        Assert.False(driver.TakeTestEvent(TestFlagged()));
        Assert.Empty(popUps.Current());
    }

    [Fact]
    public async Task AFlaggedJoinIsShownLikeOneTheServerSent()
    {
        var clock = new FakeClock();
        var popUps = new PopUps(clock);
        var heard = new Heard();
        var screen = new LastScreen();
        var reads = new CountedReads();
        var driver = new OverlayDriver(screen, reads, clock, heard, popUps: popUps);
        driver.Add(new ServerPairing("cats", new Uri("https://cats.example"), "token", Group), "Cat Lounge");
        driver.EnteredInstance(Location());
        await driver.TickAsync(Ct);
        var readsBefore = reads.ContextReads;

        Assert.True(driver.TakeTestEvent(TestFlagged()));

        // The sound and voice, the notification overlay's card with the rank and mark, and the
        // Events screen.
        Assert.Equal([TestPeople.Prefix + "rin"], heard.Alerts);
        var card = Assert.Single(popUps.Current());
        Assert.Equal("Flagged user joined · Cat Lounge", card.Heading);
        Assert.Equal("two kicks", card.Detail);
        Assert.Equal(Modbot.Core.Users.TrustRank.User, card.Rank);
        Assert.True(card.EighteenPlus);
        Assert.Equal(Instance, Assert.Single(driver.Events).InstanceId);

        // The main panel's alert.
        await driver.TickAsync(Ct);
        Assert.Equal(TestPeople.Prefix + "rin", screen.Screen?.Alert?.SubjectId);

        // A real join makes the roster due for a read; a test one asks the server nothing.
        Assert.Equal(readsBefore, reads.ContextReads);
    }

    [Fact]
    public async Task ASecondTestFlaggedJoinIsNotHeldBackByTheFiveMinuteRule()
    {
        var clock = new FakeClock();
        var heard = new Heard();
        var driver = new OverlayDriver(new LastScreen(), new CountedReads(), clock, heard);
        driver.Add(new ServerPairing("cats", new Uri("https://cats.example"), "token", Group), "Cat Lounge");
        driver.EnteredInstance(Location());
        await driver.TickAsync(Ct);

        driver.TakeTestEvent(TestFlagged("test-1"));
        driver.TakeTestEvent(TestFlagged("test-2"));

        Assert.Equal(2, heard.Alerts.Count);
    }
}
