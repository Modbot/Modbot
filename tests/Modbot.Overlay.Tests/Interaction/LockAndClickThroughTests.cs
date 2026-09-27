using System.Numerics;
using Modbot.Companion.Overlay;
using Modbot.Overlay.Interaction;

namespace Modbot.Overlay.Tests.Interaction;

/// <summary>
/// The two switches on the bar under a panel. Locked, nothing can move or size it, from either
/// hand or either stick, while taps still land. Click-through, a ray on it does nothing at all
/// except on the bar, so the switch can always be turned back off.
/// </summary>
public class LockAndClickThroughTests
{
    private static readonly Vector3 Centre = new(0.35f, -0.28f, -1.0f);

    private static TimeSpan At(int ms) => TimeSpan.FromMilliseconds(ms);

    private static readonly Pose Right = Hands.AimingAt(Vector3.Zero, Centre);

    private static readonly Pose Left = Hands.AimingAt(new Vector3(-0.2f, 0, 0), Centre);

    private static OverlayPlacement Locked => OverlayPlacement.Default with { Locked = true };

    private static OverlayPlacement Through => OverlayPlacement.Default with { ClickThrough = true };

    [Fact]
    public void ALockedPanelIsNotPickedUp()
    {
        var interaction = new OverlayInteraction(Locked);

        var result = interaction.Update(Hands.RightOnly(Hands.Hand(Right, grab: true)), At(0));

        Assert.Null(result.Holding);
        Assert.False(result.PlacementChanged);
        Assert.Equal(Locked, interaction.Placement);
    }

    [Fact]
    public void NeitherStickMovesOrSizesALockedPanel()
    {
        // Carried in one hand and pushed with the other's stick is the owner's own way of moving
        // it; locked, that does nothing, and neither does the carrying hand's stick.
        var interaction = new OverlayInteraction(Locked);

        for (var i = 0; i < 10; i++)
        {
            interaction.Update(
                Hands.Both(
                    Hands.Hand(Left, grab: true, scroll: new Vector2(1, 1)),
                    Hands.Hand(Right, grab: true, scroll: new Vector2(-1, -1))),
                At(i * 33));
        }

        Assert.Equal(Locked, interaction.Placement);
    }

    [Fact]
    public void TwoHandsDoNotStretchALockedPanel()
    {
        var interaction = new OverlayInteraction(Locked);
        interaction.Update(Hands.Both(Hands.Hand(Left), Hands.Hand(Right, grab: true)), At(0));
        interaction.Update(Hands.Both(Hands.Hand(Left, grab: true), Hands.Hand(Right, grab: true)), At(33));

        var apart = Hands.AimingAt(new Vector3(-0.6f, 0, 0), Centre);
        var result = interaction.Update(Hands.Both(Hands.Hand(apart, grab: true), Hands.Hand(Right, grab: true)), At(66));

        Assert.Equal(OverlayPlacement.Default.Width, result.Placement.Width, 4);
    }

    [Fact]
    public void TwoQuickGripsDoNotSendALockedPanelHome()
    {
        var elsewhere = Locked with { Anchor = OverlayAnchor.World, Offset = new OverlayPose(0.35f, -0.28f, -1.0f) };
        var interaction = new OverlayInteraction(elsewhere);

        interaction.Update(Hands.RightOnly(Hands.Hand(Right, grab: true)), At(0));
        interaction.Update(Hands.RightOnly(Hands.Hand(Right)), At(100));
        interaction.Update(Hands.RightOnly(Hands.Hand(Right, grab: true)), At(200));

        Assert.Equal(elsewhere, interaction.Placement);
    }

    [Fact]
    public void ALockedPanelStillTakesTapsAndScrolling()
    {
        var interaction = new OverlayInteraction(Locked);

        var tapped = interaction.Update(Hands.RightOnly(Hands.Hand(Right, click: true)), At(0));
        Assert.Single(tapped.Clicks);

        var scrolled = interaction.Update(Hands.RightOnly(Hands.Hand(Right, scroll: new Vector2(0, 1))), At(33));
        Assert.NotEqual(Vector2.Zero, scrolled.Scroll);
    }

    [Fact]
    public void ARayOnAPanelLettingRaysThroughDoesNothing()
    {
        var interaction = new OverlayInteraction(Through) { IsOnBar = (_, _) => false };

        var result = interaction.Update(Hands.RightOnly(Hands.Hand(Right, click: true, grab: true, scroll: new Vector2(0, 1))), At(0));

        Assert.Null(result.Pointer);
        Assert.Empty(result.Clicks);
        Assert.Equal(Vector2.Zero, result.Scroll);
        Assert.Null(result.Holding);

        // But the ray is known to be there, which is what puts the bar up.
        Assert.NotNull(result.Passing);
        Assert.True(result.RayOnPanel);
    }

    [Fact]
    public void TheBarStillAnswersWhileRaysGoThrough()
    {
        var interaction = new OverlayInteraction(Through) { IsOnBar = (_, _) => true };

        var result = interaction.Update(Hands.RightOnly(Hands.Hand(Right, click: true)), At(0));

        Assert.NotNull(result.Pointer);
        Assert.Single(result.Clicks);
        Assert.Null(result.Passing);
    }

    [Fact]
    public void APanelLettingRaysThroughIsNotPickedUpEvenByItsBar()
    {
        var interaction = new OverlayInteraction(Through) { IsOnBar = (_, _) => true };

        var result = interaction.Update(Hands.RightOnly(Hands.Hand(Right, grab: true)), At(0));

        Assert.Null(result.Holding);
        Assert.Equal(Through, interaction.Placement);
    }

    [Fact]
    public void AnUnlockedPanelIsPickedUpFromItsBar()
    {
        var interaction = new OverlayInteraction(OverlayPlacement.Default) { IsOnBar = (_, _) => true };

        var result = interaction.Update(Hands.RightOnly(Hands.Hand(Right, grab: true)), At(0));

        Assert.Equal(Hand.Right, result.Holding);
    }

    [Fact]
    public void AHandBusyWithAnotherPanelIsLeftOut()
    {
        var interaction = new OverlayInteraction(OverlayPlacement.Default);

        var result = interaction.Update(Hands.RightOnly(Hands.Hand(Right, click: true)), At(0), elsewhere: Hand.Right);

        Assert.Null(result.Pointer);
        Assert.Empty(result.Clicks);
    }

    [Fact]
    public void TheSwitchesSurviveBeingSentHome()
    {
        var elsewhere = OverlayPlacement.Default with
        {
            Anchor = OverlayAnchor.World,
            Offset = new OverlayPose(0.35f, -0.28f, -1.0f),
        };
        var interaction = new OverlayInteraction(elsewhere);

        // Unlocked, so two grips work; the switches it carries are both off here, and a home
        // placement never turns either on.
        interaction.Update(Hands.RightOnly(Hands.Hand(Right, grab: true)), At(0));
        interaction.Update(Hands.RightOnly(Hands.Hand(Right)), At(100));
        interaction.Update(Hands.RightOnly(Hands.Hand(Right, grab: true)), At(200));

        Assert.Equal(OverlayAnchor.Head, interaction.Placement.Anchor);
        Assert.False(interaction.Placement.Locked);
        Assert.False(interaction.Placement.ClickThrough);
    }
}
