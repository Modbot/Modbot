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

        // The slot's centre is about x 336; the left edge is where a normal pill's would be.
        var centre = plan.ScreenX + (plan.Metrics.PillMinWidth / 2);
        Assert.InRange(centre, 334, 338);

        // Icons from y about 41 to 43; label pills about y 84 to 104.
        Assert.InRange(plan.ScreenY, 41, 44);
        Assert.InRange(plan.ScreenY + plan.Metrics.PillTop, 82, 86);
        Assert.InRange(plan.ScreenY + plan.Metrics.Height, 102, 106);
    }

    [Fact]
    public void At2560By1440ItIsFurtherAlongAndBiggerByTheHeightsRatio()
    {
        var plan = EscapeBubblePlan.For(true, false, Window(2560, 1440), 0, 0, Shortcut)!;

        Assert.InRange(plan.ScreenX + (plan.Metrics.PillMinWidth / 2), 445, 449);
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
