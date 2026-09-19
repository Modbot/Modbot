using Modbot.Companion.Clips;
using Modbot.Companion.Ingest;
using Modbot.Companion.Instances;
using Modbot.Companion.Overlay;
using Modbot.Overlay.Driving;
using Modbot.Overlay.Interaction;
using Modbot.Overlay.Views;
using Modbot.TestSupport;

namespace Modbot.Overlay.Tests.Driving;

/// <summary>
/// Save a clip on the overlay panel: the tap reaching the companion, and only when something is
/// actually being kept.
/// </summary>
/// <remarks>
/// The loop saves nothing itself. It has no recorder, no folder and no way to reach either; it
/// says that a moderator asked, and the companion — which owns all three — decides. That is the
/// same shape as every other target: the overlay shows things and reports taps, and never acts
/// (clips design spec §11).
/// </remarks>
public class OverlayDriverClipTests
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

    private sealed class NothingReads : IOverlayReadClient
    {
        public Task<ReadResult<InstanceContext>> GetContextAsync(ServerPairing pairing, string instanceId, CancellationToken cancellationToken)
            => Task.FromResult(new ReadResult<InstanceContext>(ReadOutcome.Unreachable));

        public Task<ReadResult<LivePollPage>> PollLiveAsync(ServerPairing pairing, string instanceId, string? after, int waitSeconds, CancellationToken cancellationToken)
            => Task.FromResult(new ReadResult<LivePollPage>(ReadOutcome.NothingWaiting, Elapsed: TimeSpan.FromSeconds(waitSeconds)));

        public Task<ReadResult<UserSummary>> GetUserAsync(ServerPairing pairing, string subjectId, CancellationToken cancellationToken)
            => Task.FromResult(new ReadResult<UserSummary>(ReadOutcome.Unreachable));
    }

    private static InstanceLocation Location()
    {
        Assert.True(InstanceLocation.TryParse($"wrld_4b34:{Instance}~group({Group})~groupAccessType(members)~region(use)", out var location));
        return location;
    }

    private static (OverlayDriver Driver, RecordingPresenter Presenter) Build(bool inAGroupInstance = true)
    {
        var presenter = new RecordingPresenter();
        var driver = new OverlayDriver(presenter, new NothingReads(), new FakeClock());
        driver.Add(new ServerPairing("cats", new Uri("https://cats.example"), "token", Group), "Cat Lounge");

        if (inAGroupInstance)
            driver.EnteredInstance(Location());

        return (driver, presenter);
    }

    [Fact]
    public void ATapOnSaveAClipReachesTheCompanion()
    {
        var (driver, _) = Build();
        var asked = 0;
        driver.SaveClipAsked += () => asked++;
        driver.Clips = new ClipButton(ClipButtonState.Ready, "Save a clip", CanPress: true);

        driver.Tap(new OverlayTarget.SaveClip());

        Assert.Equal(1, asked);
    }

    [Fact]
    public void ATapOnSaveAClipDoesNothingWhileNothingIsBeingKept()
    {
        // The panel redraws a few times a second, so a tap can land on a frame drawn just before
        // VRChat closed. It must not look like it saved anything.
        var (driver, _) = Build();
        var asked = 0;
        driver.SaveClipAsked += () => asked++;
        driver.Clips = new ClipButton(ClipButtonState.Stopped, "Waiting for VRChat", CanPress: false);

        driver.Tap(new OverlayTarget.SaveClip());

        Assert.Equal(0, asked);
    }

    [Fact]
    public void WithClipsOffTheTapDoesNothingEither()
    {
        var (driver, _) = Build();
        var asked = 0;
        driver.SaveClipAsked += () => asked++;

        driver.Tap(new OverlayTarget.SaveClip());

        Assert.Equal(0, asked);
    }

    [Fact]
    public void SavingAClipDoesNotDisturbTheRestOfThePanel()
    {
        // It is not a screen change: the person's card stays open, the roster stays where it was.
        var (driver, _) = Build();
        driver.Clips = new ClipButton(ClipButtonState.Ready, "Save a clip", CanPress: true);
        driver.GoTo(OverlayPage.Events);

        driver.Tap(new OverlayTarget.SaveClip());

        Assert.Equal(OverlayPage.Events, driver.Page);
    }

    [Fact]
    public async Task ThePanelCarriesWhatTheCompanionSaidAboutSaving()
    {
        var (driver, presenter) = Build();
        driver.Clips = new ClipButton(ClipButtonState.Saved, "Clip saved", CanPress: true);

        await driver.TickAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ClipButtonState.Saved, presenter.Last.Clips.State);
        Assert.Equal("Clip saved", presenter.Last.Clips.Caption);
    }

    [Fact]
    public async Task SaveAClipIsThereOutsideAGroupInstanceToo()
    {
        // The recorder runs wherever VRChat does, so a moment worth keeping can happen in a public
        // instance. The panel still says nothing else there.
        var (driver, presenter) = Build(inAGroupInstance: false);
        driver.Clips = new ClipButton(ClipButtonState.Ready, "Save a clip", CanPress: true);

        await driver.TickAsync(TestContext.Current.CancellationToken);

        Assert.True(presenter.Last.IsIdle);
        Assert.True(presenter.Last.Clips.IsVisible);
    }

    [Fact]
    public void AChangedSaveControlMakesThePanelRedraw()
    {
        // The confirmation is the point. A screen comparison that ignored it would leave the panel
        // showing "Save a clip" after the file landed.
        var before = OverlayScreen.Idle with { Clips = new ClipButton(ClipButtonState.Ready, "Save a clip", true) };
        var after = OverlayScreen.Idle with { Clips = new ClipButton(ClipButtonState.Saved, "Clip saved", true) };

        Assert.False(before.LooksTheSameAs(after));
        Assert.True(before.LooksTheSameAs(before with { }));
    }
}
