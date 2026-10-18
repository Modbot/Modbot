using Modbot.Companion.Ingest;
using Modbot.Companion.Instances;
using Modbot.Companion.Overlay;
using Modbot.Companion.Talk;
using Modbot.Overlay.Driving;
using Modbot.Overlay.Interaction;
using Modbot.Overlay.Views;
using Modbot.TestSupport;

namespace Modbot.Overlay.Tests.Driving;

/// <summary>
/// The Talk buttons on the overlay panel: a tap reaching the companion, only while the row is
/// there, and the row travelling with the screen the loop builds.
/// </summary>
/// <remarks>
/// The loop presses nothing itself. It says which button was tapped; the companion, which owns the
/// controller, decides what that does (the same shape as Save a clip).
/// </remarks>
public class OverlayDriverTalkTests
{
    private const string Group = "grp_cats";
    private const string Instance = "39911";

    private static readonly TalkRow Showing = new(true, TalkSide.Discord, false, false, null);

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

    private sealed class NothingReads : IOverlayReadClient
    {
        public Task<ReadResult<InstanceContext>> GetContextAsync(ServerPairing pairing, string instanceId, string? worldId, CancellationToken cancellationToken)
            => Task.FromResult(new ReadResult<InstanceContext>(ReadOutcome.Unreachable));

        public Task<ReadResult<LivePollPage>> PollLiveAsync(ServerPairing pairing, string instanceId, string? worldId, string? after, int waitSeconds, CancellationToken cancellationToken)
            => Task.FromResult(new ReadResult<LivePollPage>(ReadOutcome.NothingWaiting, Elapsed: TimeSpan.FromSeconds(waitSeconds)));

        public Task<ReadResult<UserSummary>> GetUserAsync(ServerPairing pairing, string subjectId, CancellationToken cancellationToken)
            => Task.FromResult(new ReadResult<UserSummary>(ReadOutcome.Unreachable));
    }

    private static OverlayDriver Build()
    {
        Assert.True(InstanceLocation.TryParse($"wrld_4b34:{Instance}~group({Group})~groupAccessType(members)~region(use)", out var location));

        var driver = new OverlayDriver(new RecordingPresenter(), new NothingReads(), new FakeClock());
        driver.Add(new ServerPairing("cats", new Uri("https://cats.example"), "token", Group), "Cat Lounge");
        driver.EnteredInstance(location);
        return driver;
    }

    [Theory]
    [InlineData(TalkButton.Swap)]
    [InlineData(TalkButton.DiscordMute)]
    [InlineData(TalkButton.DiscordDeafen)]
    public void ATapOnAButtonReachesTheCompanionWithThatButton(TalkButton button)
    {
        var driver = Build();
        var asked = new List<TalkButton>();
        driver.TalkAsked += asked.Add;
        driver.Talk = Showing;

        driver.Tap(new OverlayTarget.Talk(button));

        Assert.Equal([button], asked);
    }

    [Fact]
    public void WithTheRowOffTheTapDoesNothing()
    {
        // The panel redraws a few times a second, so a tap can land on a frame drawn just before
        // Talk in Discord or VRChat was switched off.
        var driver = Build();
        var asked = 0;
        driver.TalkAsked += _ => asked++;

        driver.Tap(new OverlayTarget.Talk(TalkButton.Swap));

        Assert.Equal(0, asked);
    }

    [Fact]
    public void ATapOnAButtonDoesNotDisturbTheRestOfThePanel()
    {
        var driver = Build();
        driver.Talk = Showing;
        driver.GoTo(OverlayPage.Events);

        driver.Tap(new OverlayTarget.Talk(TalkButton.Swap));

        Assert.Equal(OverlayPage.Events, driver.Page);
    }
}
