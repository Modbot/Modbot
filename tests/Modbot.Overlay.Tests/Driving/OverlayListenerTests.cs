using Modbot.Companion.Ingest;
using Modbot.Companion.Instances;
using Modbot.Companion.Overlay;
using Modbot.Overlay.Driving;
using Modbot.Overlay.Views;
using Modbot.TestSupport;

namespace Modbot.Overlay.Tests.Driving;

/// <summary>
/// What the loop tells somebody other than the panel — the companion's voice — and that it tells
/// them exactly what the panel shows: an alert that became a card, a token that was rejected, and
/// nothing the loop itself dropped.
/// </summary>
public class OverlayListenerTests
{
    private const string Group = "grp_cats";
    private const string Instance = "39911";

    private sealed class Heard : IOverlayListener
    {
        public List<string> Alerts { get; } = [];

        public List<string> Rejections { get; } = [];

        public void AlertShown(FlaggedJoinAlert alert) => Alerts.Add(alert.SubjectId);

        public void TokenRejected(string label) => Rejections.Add(label);
    }

    private sealed class QuietPresenter : IOverlayPresenter
    {
        public bool Update(OverlayScreen screen) => false;

        public void Show() { }

        public void Hide() { }
    }

    private sealed class ScriptedReads : IOverlayReadClient
    {
        public Queue<ReadResult<InstanceContext>> Contexts { get; } = new();

        public Queue<ReadResult<FlaggedJoinAlert>> Alerts { get; } = new();

        public Task<ReadResult<InstanceContext>> GetContextAsync(ServerPairing pairing, string instanceId, CancellationToken cancellationToken)
            => Task.FromResult(Contexts.Count > 0 ? Contexts.Dequeue() : new ReadResult<InstanceContext>(ReadOutcome.Unreachable));

        public Task<ReadResult<FlaggedJoinAlert>> WaitForAlertAsync(ServerPairing pairing, int waitSeconds, CancellationToken cancellationToken)
            => Task.FromResult(Alerts.Count > 0
                ? Alerts.Dequeue()
                : new ReadResult<FlaggedJoinAlert>(ReadOutcome.NothingWaiting, Elapsed: TimeSpan.FromSeconds(30)));

        public Task<ReadResult<UserSummary>> GetUserAsync(ServerPairing pairing, string subjectId, CancellationToken cancellationToken)
            => throw new NotSupportedException();
    }

    private static InstanceLocation Location(string instance = Instance)
    {
        Assert.True(InstanceLocation.TryParse($"wrld_4b34:{instance}~group({Group})~groupAccessType(members)~region(use)", out var location));
        return location;
    }

    private static FlaggedJoinAlert Alert(string subject, string instance = Instance) => new(
        $"alert-{subject}", subject, "Trouble", instance, "2 prior actions", 2,
        new DateTimeOffset(2026, 9, 12, 20, 0, 0, TimeSpan.Zero));

    private static (OverlayDriver Driver, ScriptedReads Reads, Heard Heard, FakeClock Clock) Build()
    {
        var clock = new FakeClock();
        var reads = new ScriptedReads();
        var heard = new Heard();
        var driver = new OverlayDriver(new QuietPresenter(), reads, clock, heard);

        driver.Add(new ServerPairing("cats", new Uri("https://cats.example"), "token", Group), "Cat Lounge");
        driver.EnteredInstance(Location());
        reads.Contexts.Enqueue(new ReadResult<InstanceContext>(ReadOutcome.Fetched, new InstanceContext(Instance, [])));

        return (driver, reads, heard, clock);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task AnAlertThatBecomesACardIsPassedOn()
    {
        var (driver, reads, heard, _) = Build();
        reads.Alerts.Enqueue(new ReadResult<FlaggedJoinAlert>(ReadOutcome.Fetched, Alert("usr_flag"), TimeSpan.FromSeconds(1)));

        await driver.TickAsync(Ct);
        await driver.TickAsync(Ct);

        Assert.Equal(["usr_flag"], heard.Alerts);
    }

    [Fact]
    public async Task AnAlertTheLoopDropsIsNotPassedOn()
    {
        // Not this room, or the same person again within the cooldown: no card, so no word.
        var (driver, reads, heard, _) = Build();
        reads.Alerts.Enqueue(new ReadResult<FlaggedJoinAlert>(ReadOutcome.Fetched, Alert("usr_elsewhere", instance: "11111"), TimeSpan.FromSeconds(1)));
        reads.Alerts.Enqueue(new ReadResult<FlaggedJoinAlert>(ReadOutcome.Fetched, Alert("usr_flag"), TimeSpan.FromSeconds(1)));
        reads.Alerts.Enqueue(new ReadResult<FlaggedJoinAlert>(ReadOutcome.Fetched, Alert("usr_flag"), TimeSpan.FromSeconds(1)));

        for (var i = 0; i < 8; i++)
            await driver.TickAsync(Ct);

        Assert.Equal(["usr_flag"], heard.Alerts);
    }

    [Fact]
    public async Task ARejectedTokenIsPassedOnOnce()
    {
        var (driver, reads, heard, _) = Build();
        reads.Contexts.Clear();
        reads.Contexts.Enqueue(new ReadResult<InstanceContext>(ReadOutcome.Unauthorised));
        reads.Alerts.Enqueue(new ReadResult<FlaggedJoinAlert>(ReadOutcome.Unauthorised));

        for (var i = 0; i < 4; i++)
            await driver.TickAsync(Ct);

        Assert.Equal(["Cat Lounge"], heard.Rejections);
    }

    [Fact]
    public async Task NoListenerIsFine()
    {
        var clock = new FakeClock();
        var reads = new ScriptedReads();
        var driver = new OverlayDriver(new QuietPresenter(), reads, clock);
        driver.Add(new ServerPairing("cats", new Uri("https://cats.example"), "token", Group), "Cat Lounge");
        driver.EnteredInstance(Location());
        reads.Alerts.Enqueue(new ReadResult<FlaggedJoinAlert>(ReadOutcome.Fetched, Alert("usr_flag"), TimeSpan.FromSeconds(1)));

        await driver.TickAsync(Ct);
        var tick = await driver.TickAsync(Ct);

        Assert.True(tick.AlertShown);
    }
}
