using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;
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
            t.Target is OverlayTarget.GoTo or OverlayTarget.Person or OverlayTarget.BackFromPerson
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
        var (plain, without) = Draw(() => PanelFrame.Main(OverlayView.Build(Roster(3)), PanelBar.For(OverlayPlacement.Default, true), null, Main), Main);
        var (drawn, with) = Draw(() => PanelFrame.Main(OverlayView.Build(Roster(3)), PanelBar.For(OverlayPlacement.Default, true, hints), null, Main), Main);

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
        var (pixels, targets) = Draw(() => PanelFrame.Main(OverlayView.Build(Roster(3)), PanelBar.For(OverlayPlacement.Default, false, hints), null, Main), Main);

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
    public void AnUnlockedPopUpPanelsBoxIsOneTargetAndALockedOnesIsNone()
    {
        var (_, unlocked) = Draw(() => PanelFrame.Notification(NotificationView.Build(NotificationScreen.Empty), new PanelBar(false, false, false), null, PopUps), PopUps);
        var (_, locked) = Draw(() => PanelFrame.Notification(NotificationView.Build(NotificationScreen.Empty), new PanelBar(false, true, false), null, PopUps), PopUps);

        // The whole box, top to bottom, and the bar is outside it.
        var box = Bounds<OverlayTarget.PopUpBox>(unlocked);
        Assert.Equal(NotifyOverlaySettings.BoxPixels, box.Width);
        Assert.Equal(NotifyOverlaySettings.BoxPixels, box.Height);
        Assert.Equal(0, box.Top);
        Assert.True(Bounds<OverlayTarget.Bar>(unlocked).Top >= box.Bottom);
        Assert.DoesNotContain(locked, t => t.Target is OverlayTarget.PopUpBox);
    }

    [Fact]
    public void ThePopUpHintsAreDrawnInsideTheBoxAndAddNoTarget()
    {
        PopUp Card(string id) => new(id, "Flagged user joined · Cat Lounge", "Somebody", "kicked before", PopUpTone.Flagged);
        var screen = new NotificationScreen([Card("a")]);
        var hints = ControlHints.For(null, holding: true);

        // No width on these bars: scale 1, which is exactly what the pop-up panel draws at its default width.

        var (plain, without) = Draw(() => PanelFrame.Notification(NotificationView.Build(screen), new PanelBar(true, false, false), null, PopUps), PopUps);
        var (drawn, with) = Draw(() => PanelFrame.Notification(NotificationView.Build(screen), new PanelBar(true, false, false, hints), null, PopUps), PopUps);

        Assert.Equal(without.Count, with.Count);
        Assert.Equal(Bounds<OverlayTarget.PopUpBox>(without), Bounds<OverlayTarget.PopUpBox>(with));
        Assert.Equal(Bounds<OverlayTarget.Bar>(without), Bounds<OverlayTarget.Bar>(with));

        // Near the foot of the box, where the clear ground was before, a pill is now.
        var foot = new Point(PopUps / 2, NotifyOverlaySettings.BoxPixels - 20);
        Assert.Equal(0, Alpha(plain, PopUps, foot));
        Assert.Equal(255, Alpha(drawn, PopUps, foot));
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
    public void HintPillsAreTheSamePhysicalSizeOnEveryPanel()
    {
        var hints = ControlHints.For(null, holding: true);
        var popUps = PanelBar.For(OverlayPlacement.Default with { Width = NotifyOverlaySettings.DefaultWidth * NotifyOverlaySettings.PanelPerBox }, true, hints);
        var main = PanelBar.For(OverlayPlacement.Default with { Width = 0.45f }, true, hints);
        var narrow = PanelBar.For(OverlayPlacement.Default with { Width = OverlayPlacement.MinWidth }, true, hints);

        // The pop-up panel at its default width is the reference: its pills are not stretched at all.
        Assert.Equal(1, PanelFrame.HintScale(popUps, PopUps), 3);

        // Text height in metres: the scaled text over the panel's pixels per metre.
        double Metres(PanelBar bar, double side) => PanelFrame.HintScale(bar, side) / (side / bar.WidthInMetres);

        Assert.Equal(Metres(popUps, PopUps), Metres(main, Main), 6);

        // A very narrow panel keeps its pills from filling it: the scale is held back.
        var same = Main / narrow.WidthInMetres * (NotifyOverlaySettings.DefaultWidth / NotifyOverlaySettings.BoxPixels);
        Assert.True(PanelFrame.HintScale(narrow, Main) < same / 1.5, "The scale is capped, not left to grow with the pixels per metre.");
    }

    private static readonly OverlayPlacement PopUpPlacement = OverlayPlacement.Default with
    {
        Width = NotifyOverlaySettings.DefaultWidth * NotifyOverlaySettings.PanelPerBox,
    };

    private static readonly OverlayPlacement ButtonPlacement = OverlayPlacement.Default with { Width = OverlayButton.CornerWidth };

    /// <summary>The Lock button's width in metres: its pixels over the panel's pixels to a metre.</summary>
    private static double LockMetres(IReadOnlyList<PlacedTarget> targets, PanelBar bar, double canvas)
        => Bounds<OverlayTarget.Lock>(targets).Width / (canvas / bar.WidthInMetres);

    private static double MainLockMetres(OverlayPlacement placement)
    {
        var bar = PanelBar.For(placement, true);
        var width = Main * placement.Wide;
        var height = Main * placement.Tall;
        var canvas = (int)Math.Max(width, height);
        var (_, targets) = Draw(() => PanelFrame.Main(OverlayView.Build(Roster(3)), bar, null, height, width), canvas);
        return LockMetres(targets, bar, canvas);
    }

    private static double PopUpLockMetres(OverlayPlacement placement)
    {
        var bar = PanelBar.For(placement, true);
        var canvas = (int)(PopUps * placement.Longest);
        var (_, targets) = Draw(
            () => PanelFrame.Notification(NotificationView.Build(NotificationScreen.Empty), bar, null, PopUps, placement.Wide, placement.Tall),
            canvas);
        return LockMetres(targets, bar, canvas);
    }

    private static double ButtonLockMetres(OverlayPlacement placement)
    {
        // The bar is told the square the room draws, which is bigger than the card.
        var bar = PanelBar.For(OverlayButton.Drawn(placement), true);
        var (_, targets) = Draw(
            () => PanelFrame.Button(new Border(), bar, null, OverlayButton.PanelPixels),
            OverlayButton.PanelPixels);
        return LockMetres(targets, bar, OverlayButton.PanelPixels);
    }

    private static Rect ButtonCard(PanelBar? bar)
    {
        var (_, targets) = Draw(
            () => PanelFrame.Button(new Border { Tag = new OverlayTarget.PanelButton() }, bar, null, OverlayButton.PanelPixels),
            OverlayButton.PanelPixels);
        return Bounds<OverlayTarget.PanelButton>(targets);
    }

    [Fact]
    public void TheButtonCardKeepsItsSizeAndSpotWhetherTheBarIsThereOrNot()
    {
        var without = ButtonCard(null);
        var drawn = OverlayButton.Drawn(ButtonPlacement);

        Assert.Equal(new Rect(OverlayButton.EdgePixels, OverlayButton.EdgePixels, OverlayButton.CardPixels, OverlayButton.CardPixels), without);

        foreach (var bar in new[] { PanelBar.For(drawn, false), PanelBar.For(drawn, true), PanelBar.For(drawn with { Locked = true, ClickThrough = true }, true) })
            Assert.Equal(without, ButtonCard(bar));
    }

    [Fact]
    public void TheButtonBarHasItsOwnStripUnderTheCardInsideThePicture()
    {
        var bar = PanelBar.For(OverlayButton.Drawn(ButtonPlacement with { Width = OverlayPlacement.MinWidth }), true);
        var (_, targets) = Draw(
            () => PanelFrame.Button(new Border { Tag = new OverlayTarget.PanelButton() }, bar, null, OverlayButton.PanelPixels),
            OverlayButton.PanelPixels);

        var card = Bounds<OverlayTarget.PanelButton>(targets);
        var strip = Bounds<OverlayTarget.Bar>(targets);

        Assert.True(strip.Top >= card.Bottom, "The bar is under the card.");
        Assert.True(strip.Bottom <= OverlayButton.PanelPixels, "The bar is inside the picture, even at the narrowest the button can be.");
    }

    /// <summary>Within 5%: Avalonia lays a bar out on whole pixels, which is a pixel out on the smallest buttons.</summary>
    private static void SameSize(double expected, double actual) => Assert.InRange(actual, expected * 0.95, expected * 1.05);

    [Fact]
    public void TheBarButtonsAreTheSameSizeInTheRoomOnEveryPanel()
    {
        var metres = PanelFrame.ButtonMetres;

        Assert.Equal(0.0316, metres, 3);
        SameSize(metres, MainLockMetres(OverlayPlacement.Default));
        SameSize(metres, PopUpLockMetres(PopUpPlacement));
        SameSize(metres, ButtonLockMetres(ButtonPlacement));
    }

    [Fact]
    public void TheBarButtonsKeepTheirSizeWhenAPanelIsMadeBiggerOrItsWidthChanges()
    {
        var metres = PanelFrame.ButtonMetres;

        SameSize(metres, MainLockMetres(OverlayPlacement.Default with { Wide = 2f, Tall = 2f }));
        SameSize(metres, MainLockMetres(OverlayPlacement.Default with { Wide = 2f }));
        SameSize(metres, MainLockMetres(OverlayPlacement.Default with { Width = 0.9f }));
        SameSize(metres, MainLockMetres(OverlayPlacement.Default with { Width = OverlayPlacement.WristWidth }));

        SameSize(metres, PopUpLockMetres(PopUpPlacement with { Wide = 2f, Tall = 2f }));
        SameSize(metres, PopUpLockMetres(PopUpPlacement with { Width = 0.8f }));

        SameSize(metres, ButtonLockMetres(ButtonPlacement with { Width = 0.3f }));
    }

    [Fact]
    public void ANarrowPopUpPanelKeepsItsBarInsideTheTextureAtTheSizeTheRoomAllows()
    {
        var narrow = PopUpPlacement with { Width = NotifyOverlaySettings.MinWidth * NotifyOverlaySettings.PanelPerBox };
        var bar = PanelBar.For(narrow, true);
        var (_, targets) = Draw(() => PanelFrame.Notification(NotificationView.Build(NotificationScreen.Empty), bar, null, PopUps), PopUps);

        Assert.True(Bounds<OverlayTarget.Bar>(targets).Bottom <= PopUps, "The bar is inside the texture.");
        Assert.True(PopUpLockMetres(narrow) < PanelFrame.ButtonMetres, "The bar is held to the room under the box, so it is smaller than the others.");
    }

    [Fact]
    public void ANarrowTallPopUpPanelGetsTheBiggestBarTheStripUnderItsBoxHolds()
    {
        var narrow = PopUpPlacement with { Width = NotifyOverlaySettings.MinWidth * NotifyOverlaySettings.PanelPerBox };
        var tall = narrow with { Tall = 2f };
        var bar = PanelBar.For(tall, true);
        const int canvas = PopUps * 2;
        var (_, targets) = Draw(
            () => PanelFrame.Notification(NotificationView.Build(NotificationScreen.Empty), bar, null, PopUps, 1, 2),
            canvas);

        // The strip under a twice-as-tall box is 88 pixels, not the 44 of an ordinary one.
        Assert.True(Bounds<OverlayTarget.Bar>(targets).Bottom <= canvas, "The bar is inside the texture.");
        Assert.True(Bounds<OverlayTarget.Bar>(targets).Top >= Bounds<OverlayTarget.PopUpBox>(targets).Bottom, "The bar is under the box.");
        Assert.True(PopUpLockMetres(tall) > PopUpLockMetres(narrow), "The taller strip holds a bigger bar.");
    }

    [Fact]
    public void ANarrowMainPanelKeepsItsBarAtItsRealSizeInsideTheTextureAndClearOfTheHints()
    {
        var hints = ControlHints.For(null, holding: true);
        var bar = PanelBar.For(OverlayPlacement.Default with { Width = OverlayPlacement.MinWidth }, true, hints);

        var (targets, pills) = AvaloniaTestHost.Run(() =>
        {
            using var renderer = new AvaloniaFrameRenderer(Main, Main);
            var root = PanelFrame.Main(OverlayView.Build(Roster(3)), bar, null, Main);
            renderer.Render(root);
            var pillBounds = root.GetVisualDescendants()
                .OfType<Border>()
                .Where(b => b.Child is TextBlock)
                .Select(b => new Rect(b.TranslatePoint(default, root) ?? default, b.Bounds.Size))
                .ToList();
            return (OverlayTargets.Find(root), pillBounds);
        });

        SameSize(PanelFrame.ButtonMetres, LockMetres(targets, bar, Main));

        var strip = Bounds<OverlayTarget.Bar>(targets);
        Assert.True(strip.Bottom <= Main, "The bar is inside the texture.");
        Assert.NotEmpty(pills);
        Assert.All(pills, pill => Assert.False(pill.Intersects(strip), "A hint pill does not run over the bar."));
    }

    [Fact]
    public void HintPillsKeepTheirSizeInTheRoomWhenAPanelIsMadeBigger()
    {
        var hints = ControlHints.For(null, holding: true);
        var main = PanelBar.For(OverlayPlacement.Default, true, hints);
        var bigMain = PanelBar.For(OverlayPlacement.Default with { Wide = 2f, Tall = 2f }, true, hints);
        var popUps = PanelBar.For(PopUpPlacement, true, hints);
        var bigPopUps = PanelBar.For(PopUpPlacement with { Wide = 2f, Tall = 2f }, true, hints);

        double Metres(PanelBar bar, double canvas) => PanelFrame.HintScale(bar, canvas) / (canvas / bar.WidthInMetres);

        var reference = Metres(popUps, PopUps);
        Assert.Equal(reference, Metres(main, Main), 6);
        Assert.Equal(reference, Metres(bigMain, Main * 2), 6);
        Assert.Equal(reference, Metres(bigPopUps, PopUps * 2), 6);
    }

    [Fact]
    public void TheMainPanelsHintsAtTheirRealSizeMoveNothing()
    {
        var hints = ControlHints.For(null, holding: true);
        var bare = PanelBar.For(OverlayPlacement.Default, true);
        var withHints = PanelBar.For(OverlayPlacement.Default, true, hints);
        Assert.True(withHints.WidthInMetres > 0);

        var (plain, without) = Draw(() => PanelFrame.Main(OverlayView.Build(Roster(3)), bare, null, Main), Main);
        var (drawn, with) = Draw(() => PanelFrame.Main(OverlayView.Build(Roster(3)), withHints, null, Main), Main);

        Assert.Equal(Bounds<OverlayTarget.Roster>(without), Bounds<OverlayTarget.Roster>(with));
        Assert.Equal(Bounds<OverlayTarget.Bar>(without), Bounds<OverlayTarget.Bar>(with));
        Assert.Equal(without.Count, with.Count);

        // Level with the bar's middle, on both sides of it, there is a pill where there was nothing.
        var bar = Bounds<OverlayTarget.Bar>(with);
        Assert.Equal(0, Alpha(plain, Main, new Point(bar.Left - 40, bar.Center.Y)));
        Assert.Equal(255, Alpha(drawn, Main, new Point(bar.Left - 40, bar.Center.Y)));
        Assert.Equal(255, Alpha(drawn, Main, new Point(bar.Right + 40, bar.Center.Y)));
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

    [Fact]
    public void TheMainPanelHasAResizeCornerAtItsBottomRightOnlyWhenTheBarAsksForOne()
    {
        var (_, without) = Draw(() => PanelFrame.Main(OverlayView.Build(Roster(3)), new PanelBar(true, false, false), null, Main), Main);
        var (_, with) = Draw(() => PanelFrame.Main(OverlayView.Build(Roster(3)), new PanelBar(true, false, false, Corner: true), null, Main), Main);

        Assert.DoesNotContain(without, t => t.Target is OverlayTarget.ResizeCorner);

        var corner = Bounds<OverlayTarget.ResizeCorner>(with);
        Assert.Single(with, t => t.Target is OverlayTarget.ResizeCorner);
        Assert.True(corner.Right <= Main && corner.Bottom <= Main, "The corner is inside the texture.");
        Assert.True(corner.Center.X > Main * 0.9 && corner.Center.Y > Main * 0.9, "The corner is in the bottom right.");
    }

    [Fact]
    public void TheMainPanelsResizeCornerIsBigAndItsClearPartsAreStillTheCorner()
    {
        var (pixels, at, targets) = AvaloniaTestHost.Run(() =>
        {
            using var renderer = new AvaloniaFrameRenderer(Main, Main);
            var root = PanelFrame.Main(OverlayView.Build(Roster(3)), new PanelBar(true, false, false, Corner: true), null, Main);
            var drawn = renderer.Render(root).ToArray();
            var found = OverlayTargets.Find(root);
            var corner = found.Single(t => t.Target is OverlayTarget.ResizeCorner).Bounds;
            var clear = new Point(corner.Left + 4, corner.Top + 4);
            return (drawn, (Clear: clear, Target: OverlayTargets.At(root, clear), Seen: OverlayTargets.Drawn(root, clear)), found);
        });

        var corner = Bounds<OverlayTarget.ResizeCorner>(targets);
        Assert.Equal(128, corner.Width, 1);
        Assert.Equal(128, corner.Height, 1);
        Assert.IsType<OverlayTarget.ResizeCorner>(at.Target);
        Assert.Equal(0, Alpha(pixels, Main, at.Clear));
        Assert.False(at.Seen, "The clear part is not drawn, so the host has to count the corner itself.");

        var stroke = Alpha(pixels, Main, corner.Center);
        Assert.InRange((int)stroke, 40, 220);
    }

    [Fact]
    public void ThePopUpPanelsResizeCornerIsBigAndItsClearPartsAreStillTheCorner()
    {
        var (pixels, at, targets) = AvaloniaTestHost.Run(() =>
        {
            using var renderer = new AvaloniaFrameRenderer(PopUps, PopUps);
            var root = PanelFrame.Notification(NotificationView.Build(NotificationScreen.Empty), new PanelBar(true, false, false, Corner: true), null, PopUps);
            var drawn = renderer.Render(root).ToArray();
            var found = OverlayTargets.Find(root);
            var corner = found.Single(t => t.Target is OverlayTarget.ResizeCorner).Bounds;
            var clear = new Point(corner.Left + 2, corner.Top + 2);
            return (drawn, (Clear: clear, Target: OverlayTargets.At(root, clear)), found);
        });

        var corner = Bounds<OverlayTarget.ResizeCorner>(targets);
        var box = Bounds<OverlayTarget.PopUpBox>(targets);
        Assert.Equal(52, corner.Width, 1);
        Assert.Equal(52, corner.Height, 1);
        Assert.True(box.Contains(corner.TopLeft) && box.Contains(corner.BottomRight), "The corner fits the box.");
        Assert.IsType<OverlayTarget.ResizeCorner>(at.Target);
        Assert.Equal(0, Alpha(pixels, PopUps, at.Clear));
        Assert.InRange((int)Alpha(pixels, PopUps, corner.Center), 40, 220);
    }

    [Fact]
    public void TheResizeCornerMovesNoCardsAndNoBar()
    {
        var (_, without) = Draw(() => PanelFrame.Main(OverlayView.Build(Roster(3)), new PanelBar(true, false, false), null, Main), Main);
        var (_, with) = Draw(() => PanelFrame.Main(OverlayView.Build(Roster(3)), new PanelBar(true, false, false, Corner: true), null, Main), Main);

        Assert.Equal(Bounds<OverlayTarget.Roster>(without), Bounds<OverlayTarget.Roster>(with));
        Assert.Equal(Bounds<OverlayTarget.Bar>(without), Bounds<OverlayTarget.Bar>(with));
        Assert.Equal(without.Count + 1, with.Count);
    }

    [Fact]
    public void TheBarForAPlacementHasACornerOnlyWhenItIsOpenToEditing()
    {
        Assert.False(PanelBar.For(OverlayPlacement.Default, true).Corner);
        Assert.True(PanelBar.For(OverlayPlacement.Default, true, corner: true).Corner);
        Assert.False(PanelBar.For(OverlayPlacement.Default with { Locked = true }, true, corner: true).Corner);
        Assert.False(PanelBar.For(OverlayPlacement.Default with { ClickThrough = true }, true, corner: true).Corner);
    }

    [Fact]
    public void TheBarForAPlacementCarriesItsShapeSoResizingDrawsItAgain()
    {
        var shaped = PanelBar.For(OverlayPlacement.Default with { Wide = 1.5f, Tall = 1.25f }, true);

        Assert.Equal(1.5, shaped.Wide);
        Assert.Equal(1.25, shaped.Tall);
        Assert.NotEqual(PanelBar.For(OverlayPlacement.Default, true), shaped);
    }

    [Fact]
    public void TheHintsAreSizedFromTheSquareNotJustTheWidth()
    {
        var hints = ControlHints.For(null, holding: true);
        var plain = PanelBar.For(OverlayPlacement.Default, true, hints);
        var shaped = PanelBar.For(OverlayPlacement.Default with { Wide = 1.5f, Tall = 2f }, true, hints);

        Assert.Equal(OverlayPlacement.Default.Width, plain.WidthInMetres, 4);
        Assert.Equal(OverlayPlacement.Default.Width * 2.0, shaped.WidthInMetres, 4);
    }

    [Fact]
    public void AMainPanelMadeWiderDrawsItsBarInTheMiddleOfItsWidthAndItsCornerAtItsRightEdge()
    {
        const int wide = 1536;
        var (_, targets) = Draw(
            () => PanelFrame.Main(OverlayView.Build(Roster(3)), new PanelBar(true, false, false, Corner: true), null, Main, wide),
            wide);

        var bar = Bounds<OverlayTarget.Bar>(targets);
        var corner = Bounds<OverlayTarget.ResizeCorner>(targets);

        Assert.Equal(wide / 2.0, bar.Center.X, 0);
        Assert.True(bar.Bottom <= Main, "The bar is inside the panel's height.");
        Assert.True(corner.Right <= wide && corner.Right > wide - 40, "The corner is at the panel's right edge, not the square's.");
        Assert.True(corner.Bottom <= Main && corner.Bottom > Main - 40, "The corner is at the panel's foot.");
    }

    [Fact]
    public void AMainPanelMadeTallerStillCentresItsBarInItsWidth()
    {
        const int tall = 1536;
        var (_, targets) = Draw(
            () => PanelFrame.Main(OverlayView.Build(Roster(3)), new PanelBar(true, false, false), null, tall, Main),
            tall);

        var bar = Bounds<OverlayTarget.Bar>(targets);

        Assert.Equal(Main / 2.0, bar.Center.X, 0);
        Assert.True(bar.Top >= Bounds<OverlayTarget.Roster>(targets).Bottom, "The bar is under the roster.");
    }

    [Fact]
    public void ThePopUpPanelHasAResizeCornerAtTheBottomRightOfItsBoxOnlyWhenTheBarAsksForOne()
    {
        var (_, without) = Draw(() => PanelFrame.Notification(NotificationView.Build(NotificationScreen.Empty), new PanelBar(true, false, false), null, PopUps), PopUps);
        var (_, with) = Draw(() => PanelFrame.Notification(NotificationView.Build(NotificationScreen.Empty), new PanelBar(true, false, false, Corner: true), null, PopUps), PopUps);

        Assert.DoesNotContain(without, t => t.Target is OverlayTarget.ResizeCorner);

        var box = Bounds<OverlayTarget.PopUpBox>(with);
        var corner = Bounds<OverlayTarget.ResizeCorner>(with);
        Assert.Single(with, t => t.Target is OverlayTarget.ResizeCorner);
        Assert.True(box.Contains(corner.Center), "The corner is inside the box.");
        Assert.True(corner.Center.X > box.Center.X && corner.Center.Y > box.Center.Y, "The corner is in the box's bottom right.");
        Assert.Equal(Bounds<OverlayTarget.Bar>(without), Bounds<OverlayTarget.Bar>(with));
    }

    [Fact]
    public void AMadeBiggerPopUpBoxIsAsWideAndTallAsItWasMade()
    {
        const int canvas = PopUps * 2;
        var (_, targets) = Draw(
            () => PanelFrame.Notification(NotificationView.Build(NotificationScreen.Empty), new PanelBar(true, false, false, Corner: true), null, PopUps, 1.5, 2),
            canvas);

        var box = Bounds<OverlayTarget.PopUpBox>(targets);
        var corner = Bounds<OverlayTarget.ResizeCorner>(targets);

        Assert.Equal(NotifyOverlaySettings.BoxPixels * 1.5, box.Width, 1);
        Assert.Equal(NotifyOverlaySettings.BoxPixels * 2.0, box.Height, 1);
        Assert.Equal(0, box.Top);
        Assert.True(box.Contains(corner.Center), "The corner is inside the box.");
        Assert.True(Bounds<OverlayTarget.Bar>(targets).Top >= box.Bottom, "The bar is under the box.");
    }
}
