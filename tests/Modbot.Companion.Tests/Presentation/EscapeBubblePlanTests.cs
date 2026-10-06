using Modbot.Companion.Clips;
using Modbot.Companion.Presentation;
using Xunit;

namespace Modbot.Companion.Tests.Presentation;

/// <summary>
/// When Modbot's bubble is on screen in VRChat's row, and where, with what label and lit or not
/// (Escape Menu design §3.4). Numbers at 1920 by 1080 are the owner's real window: the row's icons
/// centred at x 60, 129, 198 and 267, so the fifth slot's centre is about 336.
/// </summary>
public sealed class EscapeBubblePlanTests
{
    private const string Shortcut = "mod+alt+m";

    private static GameWindow Window(int width, int height, bool inFront = true, bool minimised = false)
        => new(Found: true, InFront: inFront, Minimised: minimised, Width: width, Height: height);

    [Fact]
    public void WithTheOverlayOffThereIsNoBubble()
        => Assert.Null(EscapeBubblePlan.For(false, false, Window(1920, 1080), 0, 0, Shortcut));

    [Fact]
    public void WithNoVRChatWindowThereIsNoBubble()
        => Assert.Null(EscapeBubblePlan.For(true, false, GameWindow.Missing, 0, 0, Shortcut));

    [Fact]
    public void WithAMinimisedVRChatThereIsNoBubble()
        => Assert.Null(EscapeBubblePlan.For(true, false, Window(1920, 1080, minimised: true), 0, 0, Shortcut));

    [Fact]
    public void WithAWindowTooNarrowForThePanelToStartInsideItThereIsNoBubble()
    {
        // 300 by 1400: the row is drawn at the height's scale (1.39), so the panel would start at
        // about x 395, past the window's right edge.
        Assert.Null(EscapeBubblePlan.For(true, false, Window(300, 1400), 0, 0, Shortcut));
    }

    [Fact]
    public void ANarrowTallWindowWhoseRowStillFitsStillGetsABubbleInsideIt()
    {
        // 600 by 1400: the panel starts at about x 395, inside the window.
        var plan = EscapeBubblePlan.For(true, false, Window(600, 1400), 0, 0, Shortcut)!;

        Assert.True(plan.ScreenX < 600);
    }

    [Theory]
    [InlineData(100, 30)]
    [InlineData(1000, 30)]
    [InlineData(40, 20)]
    public void ATinyWindowWhosePanelWouldBeASliverHasNoBubble(int width, int height)
        => Assert.Null(EscapeBubblePlan.For(true, false, Window(width, height), 0, 0, Shortcut));

    [Fact]
    public void WithAWindowThatHasNoSizeThereIsNoBubble()
        => Assert.Null(EscapeBubblePlan.For(true, false, Window(0, 0), 0, 0, Shortcut));

    [Fact]
    public void VRChatDoesNotHaveToBeInFrontForTheBubbleToStay()
    {
        var front = EscapeBubblePlan.For(true, false, Window(1920, 1080, inFront: true), 100, 50, Shortcut);
        var behind = EscapeBubblePlan.For(true, false, Window(1920, 1080, inFront: false), 100, 50, Shortcut);

        Assert.NotNull(front);
        Assert.Equal(front, behind);
    }

    [Fact]
    public void At1920By1080ItIsInTheFifthSlotAfterY()
    {
        var plan = EscapeBubblePlan.For(true, false, Window(1920, 1080), 0, 0, Shortcut)!;

        // The window is the backing panel, so the bubble is inside it, where the row's slot is.
        // The slot's centre is about x 336; the left edge is where a normal pill's would be.
        var pillLeft = plan.ScreenX + plan.Metrics.PillOffsetX;
        Assert.InRange(pillLeft + (plan.Metrics.PillMinWidth / 2), 334, 338);

        // Icons from y about 41 to 43; label pills about y 84 to 104.
        var iconTop = plan.ScreenY + plan.Metrics.PillOffsetY;
        Assert.InRange(iconTop, 41, 44);
        Assert.InRange(iconTop + plan.Metrics.PillTop, 82, 86);
        Assert.InRange(iconTop + plan.Metrics.Height, 102, 106);
    }

    [Theory]
    [InlineData(1920, 1080)]
    [InlineData(2560, 1440)]
    public void TheBackingPanelIsVRChatsOwnTopAndBottomAndStartsAfterItsRightEdge(int width, int height)
    {
        var plan = EscapeBubblePlan.For(true, false, Window(width, height), 0, 0, Shortcut)!;
        var s = plan.Metrics.Scale;

        // VRChat's panel at scale 1 is y 24.3 to 114.9 and ends at x 281.2; ours is the window.
        Assert.InRange(plan.ScreenY, (24.3 * s) - 1, (24.3 * s) + 1);
        Assert.InRange(plan.ScreenY + plan.Metrics.PanelHeight, (114.9 * s) - 1, (114.9 * s) + 1);
        Assert.True(plan.ScreenX >= (281.2 * s) + (3 * s), "overlaps VRChat's own panel");
    }

    [Fact]
    public void At1920By1080ThePanelIsWhereVRChatsOwnWasMeasuredPlusTheGap()
    {
        var plan = EscapeBubblePlan.For(true, false, Window(1920, 1080), 0, 0, Shortcut)!;

        // VRChat's: x 26 to 301, y 26 to 123. Ours starts a few pixels after 301 and is as tall.
        Assert.InRange(plan.ScreenX, 304, 306);
        Assert.InRange(plan.ScreenY, 25, 27);
        Assert.InRange(plan.Metrics.PanelHeight, 96, 98);

        // The room between the panels, the padding right of the pill, and above it.
        Assert.InRange(plan.Metrics.PillOffsetX, 8, 11);
        Assert.InRange(plan.Metrics.PillOffsetY, 16, 18);
    }

    [Fact]
    public void ALongLabelGrowsThePanelToTheRightAndKeepsTheBubbleWhereItIs()
    {
        var m = EscapeBubbleLayout.For(1920, 1080)!;

        Assert.Equal(m.PillOffsetX + m.PillMinWidth + 12, m.PanelWidth(5));
        Assert.True(m.PanelWidth(120) > m.PanelWidth(5));

        // The bubble's own left edge is the row's slot at any label width: only the right side grows.
        Assert.Equal(m.Place().X, m.PanelLeft + m.PillOffsetX);
    }

    [Fact]
    public void At2560By1440ItIsFurtherAlongAndBiggerByTheHeightsRatio()
    {
        var plan = EscapeBubblePlan.For(true, false, Window(2560, 1440), 0, 0, Shortcut)!;

        // The slot's centre scales with the height: 336 at 1920 by 1080 (scale 1.0704) is 313.9 at
        // scale 1 and 448 at 1.4272. The window is the backing panel, so the pill's left edge is
        // the window's plus PillOffsetX, as in the 1920 test: 406 + 12 + 59 / 2 = 447.
        var pillLeft = plan.ScreenX + plan.Metrics.PillOffsetX;
        Assert.InRange(pillLeft + (plan.Metrics.PillMinWidth / 2), 445, 449);
        Assert.InRange(plan.Metrics.Scale, 1.42, 1.43);
    }

    [Fact]
    public void TheWindowsOwnPositionOnTheDesktopIsAddedToTheSlot()
    {
        var atOrigin = EscapeBubblePlan.For(true, false, Window(1920, 1080), 0, 0, Shortcut)!;
        var moved = EscapeBubblePlan.For(true, false, Window(1920, 1080), 1920, 30, Shortcut)!;

        Assert.Equal(atOrigin.ScreenX + 1920, moved.ScreenX);
        Assert.Equal(atOrigin.ScreenY + 30, moved.ScreenY);
    }

    [Fact]
    public void ItSaysTheShortcutTheWayVRChatWritesItsOwnLabels()
        => Assert.Equal("Ctrl+Alt+M", EscapeBubblePlan.For(true, false, Window(1920, 1080), 0, 0, Shortcut)!.Label);

    [Fact]
    public void ChangingTheShortcutChangesTheLabelAndNothingElse()
    {
        var before = EscapeBubblePlan.For(true, false, Window(1920, 1080), 0, 0, Shortcut)!;
        var after = EscapeBubblePlan.For(true, false, Window(1920, 1080), 0, 0, "f1")!;

        Assert.Equal("F1", after.Label);
        Assert.Equal(before with { Label = "F1" }, after);
    }

    [Fact]
    public void ItIsLitWhileTheOverlayIsOpenAndGreyWhileItIsNot()
    {
        Assert.True(EscapeBubblePlan.For(true, true, Window(1920, 1080), 0, 0, Shortcut)!.Lit);
        Assert.False(EscapeBubblePlan.For(true, false, Window(1920, 1080), 0, 0, Shortcut)!.Lit);
    }

    [Fact]
    public void AResizedWindowGivesADifferentPlanSoTheBubbleIsMoved()
    {
        var small = EscapeBubblePlan.For(true, false, Window(1920, 1080), 0, 0, Shortcut)!;
        var large = EscapeBubblePlan.For(true, false, Window(2560, 1440), 0, 0, Shortcut)!;

        Assert.NotEqual(small, large);
    }
}
