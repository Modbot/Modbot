using Modbot.Companion.Ingest;
using Modbot.Companion.Instances;
using Modbot.Companion.Overlay;
using Modbot.Overlay.Driving;
using Modbot.Overlay.Interaction;
using Modbot.Overlay.Views;
using Modbot.Shared.HeadsUps;
using Modbot.TestSupport;

namespace Modbot.Overlay.Tests.Driving;

/// <summary>
/// Heads-ups on the panel: the "+" opens a strip, Place sends exactly what was written and only
/// then, a refusal stays on the strip, and cards go up for other moderators' heads-ups and for a
/// kept-an-eye-on person walking back in.
/// </summary>
public class OverlayDriverHeadsUpTests
{
    private const string Group = "grp_cats";
    private const string Instance = "39911";
    private const string World = "wrld_4b34";

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

        public Task<ReadResult<InstanceContext>> GetContextAsync(ServerPairing pairing, string instanceId, string? worldId, CancellationToken cancellationToken)
            => Task.FromResult(Contexts.Count > 0 ? Contexts.Dequeue() : new ReadResult<InstanceContext>(ReadOutcome.Unreachable));

        public Task<ReadResult<LivePollPage>> PollLiveAsync(ServerPairing pairing, string instanceId, string? worldId, string? after, int waitSeconds, CancellationToken cancellationToken)
            => Task.FromResult(Live.Count > 0 ? Live.Dequeue() : new ReadResult<LivePollPage>(ReadOutcome.NothingWaiting, Elapsed: TimeSpan.FromSeconds(waitSeconds)));

        public Task<ReadResult<UserSummary>> GetUserAsync(ServerPairing pairing, string subjectId, CancellationToken cancellationToken)
            => Task.FromResult(new ReadResult<UserSummary>(ReadOutcome.Unreachable));
    }

    private sealed class Sender : IHeadsUpClient
    {
        public List<(string Instance, string? World, HeadsUpDraft Draft)> Placed { get; } = [];

        public List<string> Cleared { get; } = [];

        public HeadsUpSent Answer { get; set; } = new(HeadsUpSendOutcome.Done);

        public Task<HeadsUpSent> PlaceAsync(ServerPairing pairing, string instanceId, string? worldId, HeadsUpDraft draft, CancellationToken cancellationToken)
        {
            Placed.Add((instanceId, worldId, draft));
            return Task.FromResult(Answer);
        }

        public Task<HeadsUpSent> ClearAsync(ServerPairing pairing, string id, CancellationToken cancellationToken)
        {
            Cleared.Add(id);
            return Task.FromResult(Answer);
        }
    }

    private static InstanceLocation Location()
    {
        Assert.True(InstanceLocation.TryParse($"{World}:{Instance}~group({Group})~groupAccessType(members)~region(use)", out var location));
        return location;
    }

    private static InstanceContext Roster(params HeadsUp[] headsUps) => new(
        Instance,
        [new RosterMember("usr_Rin", "Rin", RosterStanding.Member, 0, []), new RosterMember("usr_Kai", "Kai", RosterStanding.Member, 0, [])],
        headsUps);

    private static HeadsUp Standing(string id, string kind, string? subject = null, string? text = null, string? place = null, bool mine = false)
        => new(id, kind, subject, subject is null ? null : "Kai", text, place, "Mira", DateTimeOffset.UnixEpoch, mine);

    private static (OverlayDriver Driver, RecordingPresenter Presenter, Reads Reads, Sender Sender, PopUps PopUps, FakeClock Clock) Build()
    {
        var presenter = new RecordingPresenter();
        var reads = new Reads();
        var sender = new Sender();
        var clock = new FakeClock();
        var popUps = new PopUps(clock);
        var driver = new OverlayDriver(presenter, reads, clock, popUps: popUps, headsUps: sender);
        driver.Add(new ServerPairing("cats", new Uri("https://cats.example"), "token", Group), "Cat Lounge");
        driver.EnteredInstance(Location());
        return (driver, presenter, reads, sender, popUps, clock);
    }

    [Fact]
    public async Task ThePlusOpensAStripForThatRowAndNothingIsSentUntilPlace()
    {
        var (driver, presenter, reads, sender, _, _) = Build();
        reads.Contexts.Enqueue(new ReadResult<InstanceContext>(ReadOutcome.Fetched, Roster()));
        await driver.TickAsync(TestContext.Current.CancellationToken);

        driver.Tap(new OverlayTarget.AddHeadsUp("usr_Kai"));
        driver.Tap(new OverlayTarget.HeadsUpKindPick(HeadsUpKind.KeepAnEye));
        driver.SetHeadsUpText("loud, watch him");
        await driver.TickAsync(TestContext.Current.CancellationToken);

        Assert.Equal("usr_Kai", presenter.Last.Draft?.SubjectId);
        Assert.Equal("Kai", presenter.Last.Draft?.SubjectName);
        Assert.Empty(sender.Placed);

        await driver.PlaceHeadsUpAsync();

        var placed = Assert.Single(sender.Placed);
        Assert.Equal(Instance, placed.Instance);
        Assert.Equal(World, placed.World);
        Assert.Equal(HeadsUpKind.KeepAnEye, placed.Draft.Kind);
        Assert.Equal("loud, watch him", placed.Draft.Text);
        Assert.Null(driver.Draft);
    }

    [Fact]
    public async Task SomethingMissingIsSaidOnTheStripAndNothingIsSent()
    {
        var (driver, _, reads, sender, _, _) = Build();
        reads.Contexts.Enqueue(new ReadResult<InstanceContext>(ReadOutcome.Fetched, Roster()));
        await driver.TickAsync(TestContext.Current.CancellationToken);

        driver.Tap(new OverlayTarget.AddHeadsUp("usr_Kai"));
        driver.Tap(new OverlayTarget.HeadsUpKindPick(HeadsUpKind.AskForHelp));
        await driver.PlaceHeadsUpAsync();

        Assert.Empty(sender.Placed);
        Assert.NotNull(driver.Draft?.Problem);

        driver.Tap(new OverlayTarget.HeadsUpPlacePick("Bar"));
        await driver.PlaceHeadsUpAsync();

        Assert.Equal("Bar", Assert.Single(sender.Placed).Draft.Place);
    }

    [Fact]
    public async Task ARefusalStaysOnTheStripInTheServersWords()
    {
        var (driver, _, reads, sender, _, _) = Build();
        reads.Contexts.Enqueue(new ReadResult<InstanceContext>(ReadOutcome.Fetched, Roster()));
        await driver.TickAsync(TestContext.Current.CancellationToken);
        sender.Answer = new HeadsUpSent(HeadsUpSendOutcome.Refused, "Links are not allowed.");

        driver.Tap(new OverlayTarget.AddHeadsUp("usr_Kai"));
        driver.SetHeadsUpText("hello there");
        await driver.PlaceHeadsUpAsync();

        Assert.Equal("Links are not allowed.", driver.Draft?.Problem);
        Assert.False(driver.Draft?.Sending);
    }

    [Fact]
    public async Task AnotherModeratorsHeadsUpRaisesOneCardAndOneFromHereRaisesNone()
    {
        var (driver, _, reads, _, popUps, _) = Build();
        reads.Contexts.Enqueue(new ReadResult<InstanceContext>(
            ReadOutcome.Fetched,
            Roster(Standing("a", "ask_for_help", place: "Bar", text: "need backup"), Standing("b", "pin", text: "mine", mine: true))));

        await driver.TickAsync(TestContext.Current.CancellationToken);

        var card = Assert.Single(popUps.Current());
        Assert.Equal("heads-up:a", card.Id);
        Assert.Equal("Ask for help · Mira", card.Heading);
        Assert.Equal("Bar", card.Body);
        Assert.Equal("need backup", card.Detail);
    }

    [Fact]
    public async Task TheSameHeadsUpOnTheNextReadRaisesNoSecondCard()
    {
        var (driver, _, reads, _, popUps, clock) = Build();
        var roster = Roster(Standing("a", "pin", text: "event at 9"));
        reads.Contexts.Enqueue(new ReadResult<InstanceContext>(ReadOutcome.Fetched, roster));
        await driver.TickAsync(TestContext.Current.CancellationToken);
        popUps.ClearAll();

        clock.Advance(OverlayDriver.ContextRefreshInterval);
        reads.Contexts.Enqueue(new ReadResult<InstanceContext>(ReadOutcome.Fetched, roster));
        await driver.TickAsync(TestContext.Current.CancellationToken);

        Assert.Empty(popUps.Current());
    }

    [Fact]
    public async Task APersonUnderKeepAnEyeWalkingBackInRaisesACard()
    {
        var (driver, _, reads, _, popUps, _) = Build();
        reads.Contexts.Enqueue(new ReadResult<InstanceContext>(
            ReadOutcome.Fetched,
            Roster(Standing("w", "keep_an_eye", subject: "usr_Kai", text: "was rude", mine: true))));
        await driver.TickAsync(TestContext.Current.CancellationToken);

        reads.Live.Enqueue(new ReadResult<LivePollPage>(
            ReadOutcome.Fetched,
            new LivePollPage(
                [new LiveEvent("1", "1", LiveEventKinds.PersonJoined, DateTimeOffset.UnixEpoch, Instance,
                    new LivePerson("usr_Kai", "Kai", null, RosterStanding.Member, 0, []), false, null, false, World)],
                "1",
                false)));

        // The link polls (no socket factory), so the next ticks pick the join up.
        await driver.TickAsync(TestContext.Current.CancellationToken);
        await driver.TickAsync(TestContext.Current.CancellationToken);

        Assert.Contains(popUps.Current(), p => p.Id == "heads-up-joined:usr_Kai" && p.Detail == "was rude");
    }

    [Fact]
    public async Task ClearSendsWhichHeadsUpAndReadsTheListAgain()
    {
        var (driver, _, reads, sender, _, _) = Build();
        reads.Contexts.Enqueue(new ReadResult<InstanceContext>(ReadOutcome.Fetched, Roster(Standing("a", "pin", text: "x"))));
        await driver.TickAsync(TestContext.Current.CancellationToken);

        await driver.ClearHeadsUpAsync("a");
        reads.Contexts.Enqueue(new ReadResult<InstanceContext>(ReadOutcome.Fetched, Roster()));
        var tick = await driver.TickAsync(TestContext.Current.CancellationToken);

        Assert.Equal(["a"], sender.Cleared);
        Assert.True(tick.ContextRefreshed);
    }

    [Fact]
    public async Task LeavingTheInstanceDropsTheStrip()
    {
        var (driver, _, reads, _, _, _) = Build();
        reads.Contexts.Enqueue(new ReadResult<InstanceContext>(ReadOutcome.Fetched, Roster()));
        await driver.TickAsync(TestContext.Current.CancellationToken);
        driver.Tap(new OverlayTarget.AddHeadsUp("usr_Rin"));

        driver.EnteredInstance(null);

        Assert.Null(driver.Draft);
    }
}
