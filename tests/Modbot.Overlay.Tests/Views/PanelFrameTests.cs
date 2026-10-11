using Avalonia;
using Avalonia.Controls;
using Modbot.Companion.Overlay;
using Modbot.Overlay.Interaction;
using Modbot.Overlay.Rendering;
using Modbot.Overlay.Views;

namespace Modbot.Overlay.Tests.Views;

/// <summary>
/// The bar under a headset panel, as drawn: where it sits, what can be pressed on it, and that a
/// hidden bar is laid out but draws nothing.
/// </summary>
public class PanelFrameTests
{
    private const int Main = 1024;

    private const int PopUps = NotifyOverlaySettings.PanelPixels;

    private static OverlayScreen Roster(int people) => new(
        "Cat Lounge",
        new Cached<InstanceContext>(
            new InstanceContext("39911", [.. Enumerable.Range(0, people).Select(i => new RosterMember($"usr_{i}", $"Person {i}", RosterStanding.Member, 0, []))]),
            Freshness.Fresh,
            TimeSpan.Zero),
        Freshness.Fresh);

    private static (byte[] Pixels, IReadOnlyList<PlacedTarget> Targets) Draw(Func<Control> build, int size) => AvaloniaTestHost.Run(() =>
    {
        using var renderer = new AvaloniaFrameRenderer(size, size);
        var root = build();
        var pixels = renderer.Render(root).ToArray();
        return (pixels, OverlayTargets.Find(root));
    });

    private static byte Alpha(byte[] pixels, int size, Point at) => pixels[(((int)at.Y * size) + (int)at.X) * 4 + 3];

    private static Rect Bounds<T>(IReadOnlyList<PlacedTarget> targets) where T : OverlayTarget
        => targets.Single(t => t.Target is T).Bounds;

    [Fact]
    public void TheBarCarriesTheLockAndTheHandAndNothingElse()
    {
        var (_, targets) = Draw(() => PanelFrame.Main(OverlayView.Build(Roster(3)), new PanelBar(true, false, false), null, Main), Main);

        Assert.Single(targets, t => t.Target is OverlayTarget.Lock);
        Assert.Single(targets, t => t.Target is OverlayTarget.ClickThrough);
        Assert.Single(targets, t => t.Target is OverlayTarget.Bar);

        // The whole list of target kinds a framed headset panel has.
        Assert.All(targets, t => Assert.True(
            t.Target is OverlayTarget.GoTo or OverlayTarget.Person or OverlayTarget.ClosePerson
                or OverlayTarget.RefreshPerson or OverlayTarget.Roster or OverlayTarget.Events
                or OverlayTarget.DismissAlert or OverlayTarget.SaveClip
                or OverlayTarget.Bar or OverlayTarget.Lock or OverlayTarget.ClickThrough
                or OverlayTarget.Filter or OverlayTarget.Pick or OverlayTarget.ClearFilters
                or OverlayTarget.TypeName,
            $"Unexpected overlay target {t.Target.GetType().Name}."));
    }

    [Fact]
    public void TheBarSitsUnderTheCards()
    {
        var (_, targets) = Draw(() => PanelFrame.Main(OverlayView.Build(Roster(3)), new PanelBar(true, false, false), null, Main), Main);

        var roster = Bounds<OverlayTarget.Roster>(targets);
        var bar = Bounds<OverlayTarget.Bar>(targets);

        Assert.True(bar.Top >= roster.Bottom, "The bar is under the roster, not over it.");
        Assert.True(bar.Top - roster.Bottom < 60, "The bar is right under the cards, not at the far end of the panel.");
        Assert.Equal(Main / 2.0, bar.Center.X, 0);
    }

    [Fact]
    public void ALongRosterStopsAboveTheBarAndTheBarStaysOnThePanel()
    {
        var (pixels, targets) = Draw(() => PanelFrame.Main(OverlayView.Build(Roster(60)), new PanelBar(true, false, false), null, Main), Main);

        var bar = Bounds<OverlayTarget.Bar>(targets);
        Assert.True(bar.Bottom <= Main, "The bar is inside the texture.");

        // Between the bar's two buttons is the bar's own ground, not a roster row showing through.
        var lockButton = Bounds<OverlayTarget.Lock>(targets);
        Assert.Equal(255, Alpha(pixels, Main, lockButton.Center));
    }

    [Fact]
    public void AHiddenBarIsLaidOutButDrawsNothing()
    {
        var (pixels, targets) = Draw(() => PanelFrame.Main(OverlayView.Build(Roster(3)), new PanelBar(false, false, false), null, Main), Main);

        // Still there to be found, so a ray landing on it finds it on the first poll.
        var lockButton = Bounds<OverlayTarget.Lock>(targets);

        Assert.Equal(0, Alpha(pixels, Main, lockButton.Center));
    }

    [Fact]
    public void TheCardsDoNotMoveWhenTheBarComesUp()
    {
        var (_, hidden) = Draw(() => PanelFrame.Main(OverlayView.Build(Roster(3)), new PanelBar(false, false, false), null, Main), Main);
        var (_, shown) = Draw(() => PanelFrame.Main(OverlayView.Build(Roster(3)), new PanelBar(true, true, true), null, Main), Main);

        Assert.Equal(Bounds<OverlayTarget.Roster>(hidden), Bounds<OverlayTarget.Roster>(shown));
        Assert.Equal(Bounds<OverlayTarget.Lock>(hidden), Bounds<OverlayTarget.Lock>(shown));
    }

    [Fact]
    public void TheHintsSitBesideTheBarAndMoveNothing()
    {
        var hints = ControlHints.For(null, holding: false);
        var (plain, without) = Draw(() => PanelFrame.Main(OverlayView.Build(Roster(3)), new PanelBar(true, false, false), null, Main), Main);
        var (drawn, with) = Draw(() => PanelFrame.Main(OverlayView.Build(Roster(3)), new PanelBar(true, false, false, hints), null, Main), Main);

        // The cards and the bar are exactly where they were, and the hints add no target.
        Assert.Equal(Bounds<OverlayTarget.Roster>(without), Bounds<OverlayTarget.Roster>(with));
        Assert.Equal(Bounds<OverlayTarget.Bar>(without), Bounds<OverlayTarget.Bar>(with));
        Assert.Equal(without.Count, with.Count);

        // Something is drawn left of the bar in its row that was not before. Two hints stack there,
        // so aim above the middle, where they leave a gap.
        var bar = Bounds<OverlayTarget.Bar>(with);
        var beside = new Point(bar.Left - 40, bar.Center.Y - 10);
        Assert.Equal(0, Alpha(plain, Main, beside));
        Assert.Equal(255, Alpha(drawn, Main, beside));
    }

    [Fact]
    public void HintsOnAHiddenBarStillDrawBecauseTheyAreNotTheBar()
    {
        var hints = ControlHints.For(null, holding: true);
        var (pixels, targets) = Draw(() => PanelFrame.Main(OverlayView.Build(Roster(3)), new PanelBar(false, false, false, hints), null, Main), Main);

        var bar = Bounds<OverlayTarget.Bar>(targets);
        Assert.Equal(0, Alpha(pixels, Main, bar.Center));
        Assert.Equal(255, Alpha(pixels, Main, new Point(bar.Right + 40, bar.Center.Y)));
    }

    [Fact]
    public void ALockedOrClickThroughPanelGetsNoHints()
    {
        var hints = ControlHints.For(null, holding: false);

        Assert.Equal(hints, PanelBar.For(OverlayPlacement.Default, true, hints).Hints);
        Assert.Null(PanelBar.For(OverlayPlacement.Default with { Locked = true }, true, hints).Hints);
        Assert.Null(PanelBar.For(OverlayPlacement.Default with { ClickThrough = true }, true, hints).Hints);
    }

    [Fact]
    public void AnEmptyUnlockedPopUpPanelShowsItsBox()
    {
        var (pixels, _) = Draw(() => PanelFrame.Notification(NotificationView.Build(NotificationScreen.Empty), new PanelBar(false, false, false), null, PopUps), PopUps);

        Assert.Contains(Enumerable.Range(0, PopUps * PopUps), i => pixels[(i * 4) + 3] != 0);
    }

    [Fact]
    public void AnEmptyLockedPopUpPanelWithNoRayOnItDrawsNothing()
    {
        var (pixels, _) = Draw(() => PanelFrame.Notification(NotificationView.Build(NotificationScreen.Empty), new PanelBar(false, true, false), null, PopUps), PopUps);

        Assert.All(Enumerable.Range(0, PopUps * PopUps), i => Assert.Equal(0, pixels[(i * 4) + 3]));
    }

    [Fact]
    public void AnEmptyLockedPopUpPanelStillShowsItsBarToARay()
    {
        var (pixels, targets) = Draw(() => PanelFrame.Notification(NotificationView.Build(NotificationScreen.Empty), new PanelBar(true, true, false), null, PopUps), PopUps);

        Assert.Equal(255, Alpha(pixels, PopUps, Bounds<OverlayTarget.Lock>(targets).Center));
    }

    [Fact]
    public void ThePopUpBarSitsUnderTheBoxWithThreePopUpsUp()
    {
        PopUp Card(string id) => new(id, "Flagged user joined · Cat Lounge", "Somebody", "kicked before", PopUpTone.Flagged);
        var screen = new NotificationScreen([Card("a"), Card("b"), Card("c")]);

        var (_, targets) = Draw(() => PanelFrame.Notification(NotificationView.Build(screen), new PanelBar(true, false, false), null, PopUps), PopUps);

        var bar = Bounds<OverlayTarget.Bar>(targets);
        Assert.True(bar.Top >= NotifyOverlaySettings.BoxPixels, "The bar is under the pop-ups' box.");
        Assert.True(bar.Bottom <= PopUps, "The bar is inside the texture.");
    }
}
