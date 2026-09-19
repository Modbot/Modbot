using Avalonia;
using Avalonia.Controls;
using Modbot.Companion.Clips;
using Modbot.Companion.Overlay;
using Modbot.Overlay.Interaction;
using Modbot.Overlay.Views;

namespace Modbot.Overlay.Tests.Views;

/// <summary>
/// The Save a clip control on the panel: where it is, when it is there at all, and when a tap can
/// land on it.
/// </summary>
/// <remarks>
/// It sits under the tabs, above whichever screen is showing, so it is one press away from the
/// roster, the events list and a person's card alike rather than behind a page. A moderator
/// reaches for it in the middle of something happening, which is the worst possible moment to have
/// to navigate (clips design spec §11).
/// </remarks>
public class SaveClipControlTests
{
    private const int Width = 480;
    private const int Height = 640;

    private static InstanceContext Roster() => new(
        "39911",
        [.. Enumerable.Range(0, 3).Select(i => new RosterMember($"usr_{i}", $"Person {i}", RosterStanding.Member, 0, []))]);

    private static OverlayScreen Screen(ClipButton clips = default, OverlayPage page = OverlayPage.Instance) => new(
        "Cat Lounge",
        new Cached<InstanceContext>(Roster(), Freshness.Fresh, TimeSpan.Zero),
        Freshness.Fresh,
        Page: page,
        Clips: clips);

    private static ClipButton Ready => new(ClipButtonState.Ready, "Save a clip", CanPress: true);

    private static ClipButton Stopped => new(ClipButtonState.Stopped, "Waiting for VRChat", CanPress: false);

    private static Control LaidOut(OverlayScreen screen)
    {
        var root = OverlayView.Build(screen);
        root.Measure(new Size(Width, Height));
        root.Arrange(new Rect(0, 0, Width, Height));
        return root;
    }

    private static IReadOnlyList<PlacedTarget> Targets(OverlayScreen screen)
        => AvaloniaTestHost.Run(() => OverlayTargets.Find(LaidOut(screen)));

    [Fact]
    public void WithClipsOffThereIsNoControlOnThePanel()
    {
        var targets = Targets(Screen());

        Assert.DoesNotContain(targets, t => t.Target is OverlayTarget.SaveClip);
    }

    [Theory]
    [InlineData(OverlayPage.Instance)]
    [InlineData(OverlayPage.Events)]
    public void ItIsOnEveryScreenRatherThanBehindAPage(OverlayPage page)
    {
        var targets = Targets(Screen(Ready, page));

        Assert.Contains(targets, t => t.Target is OverlayTarget.SaveClip);
    }

    [Fact]
    public void WhileNothingIsBeingKeptThereIsNothingToTap()
    {
        // It is still drawn — the caption says why nothing is being kept — but no tap can land on
        // it, so a press cannot look like it saved something.
        var targets = Targets(Screen(Stopped));

        Assert.DoesNotContain(targets, t => t.Target is OverlayTarget.SaveClip);
    }

    [Fact]
    public void ItIsBigEnoughForAHandInAHeadset()
    {
        var placed = Assert.Single(Targets(Screen(Ready)), t => t.Target is OverlayTarget.SaveClip);

        Assert.True(placed.Bounds.Width >= 200, $"Too narrow to hit in VR: {placed.Bounds.Width}");
        Assert.True(placed.Bounds.Height >= 40, $"Too short to hit in VR: {placed.Bounds.Height}");
    }

    [Fact]
    public void ItSitsNearTheTopWhereTheTabsAre()
    {
        // Under the tabs rather than at the bottom of a list that can grow past the panel.
        var placed = Assert.Single(Targets(Screen(Ready)), t => t.Target is OverlayTarget.SaveClip);

        Assert.True(placed.Bounds.Top < Height / 3.0, $"Too far down the panel: {placed.Bounds.Top}");
    }

    [Fact]
    public void OutsideAGroupInstanceTheControlIsTheOnlyThingDrawn()
    {
        // The panel still says nothing about a group it is not in, but the recorder runs wherever
        // VRChat does, so the one control a moderator might need there travels with it.
        var idle = OverlayScreen.Idle with { Clips = Ready };
        var targets = Targets(idle);

        Assert.Contains(targets, t => t.Target is OverlayTarget.SaveClip);
        Assert.DoesNotContain(targets, t => t.Target is OverlayTarget.Roster);
        Assert.DoesNotContain(targets, t => t.Target is OverlayTarget.GoTo);
    }

    [Fact]
    public void OutsideAGroupInstanceWithClipsOffThePanelIsStillBlank()
    {
        var targets = Targets(OverlayScreen.Idle);

        Assert.Empty(targets);
    }
}
