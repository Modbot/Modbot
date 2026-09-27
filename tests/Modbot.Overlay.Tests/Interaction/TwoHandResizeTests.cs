using System.Numerics;
using Modbot.Companion.Overlay;
using Modbot.Overlay.Interaction;

namespace Modbot.Overlay.Tests.Interaction;

/// <summary>
/// One hand carries the panel, the other grips on it and sizes it by how far apart the two are,
/// the way XSOverlay's windows are sized.
/// </summary>
public class TwoHandResizeTests
{
    private static readonly Vector3 Centre = new(0.35f, -0.28f, -1.0f);

    private static TimeSpan At(int ms) => TimeSpan.FromMilliseconds(ms);

    private static readonly Pose Right = Hands.AimingAt(Vector3.Zero, Centre);

    private static Pose LeftAt(float x) => Hands.AimingAt(new Vector3(x, 0, 0), Centre);

    /// <summary>The right hand has picked the panel up; the left is 20 cm to its left, not gripping.</summary>
    private static OverlayInteraction Carried()
    {
        var interaction = new OverlayInteraction(OverlayPlacement.Default);
        interaction.Update(Hands.Both(Hands.Hand(LeftAt(-0.2f)), Hands.Hand(Right, grab: true)), At(0));
        return interaction;
    }

    [Fact]
    public void PullingTheHandsApartMakesThePanelBigger()
    {
        var interaction = Carried();

        // The left hand takes hold on the panel, 20 cm from the right.
        var taken = interaction.Update(Hands.Both(Hands.Hand(LeftAt(-0.2f), grab: true), Hands.Hand(Right, grab: true)), At(33));
        Assert.Equal(OverlayPlacement.Default.Width, taken.Placement.Width, 3);

        // Twice as far apart: twice as wide.
        var stretched = interaction.Update(Hands.Both(Hands.Hand(LeftAt(-0.4f), grab: true), Hands.Hand(Right, grab: true)), At(66));
        Assert.Equal(OverlayPlacement.Default.Width * 2, stretched.Placement.Width, 3);
        Assert.Equal(Hand.Right, stretched.Holding);
    }

    [Fact]
    public void BringingThemTogetherMakesItSmaller()
    {
        var interaction = Carried();
        interaction.Update(Hands.Both(Hands.Hand(LeftAt(-0.2f), grab: true), Hands.Hand(Right, grab: true)), At(33));

        var squeezed = interaction.Update(Hands.Both(Hands.Hand(LeftAt(-0.1f), grab: true), Hands.Hand(Right, grab: true)), At(66));

        Assert.Equal(OverlayPlacement.Default.Width / 2, squeezed.Placement.Width, 3);
    }

    [Fact]
    public void TheSizeStaysInsideTheBounds()
    {
        var interaction = Carried();
        interaction.Update(Hands.Both(Hands.Hand(LeftAt(-0.2f), grab: true), Hands.Hand(Right, grab: true)), At(33));

        var huge = interaction.Update(Hands.Both(Hands.Hand(LeftAt(-5f), grab: true), Hands.Hand(Right, grab: true)), At(66));
        Assert.Equal(OverlayPlacement.MaxWidth, huge.Placement.Width, 3);

        var tiny = interaction.Update(Hands.Both(Hands.Hand(LeftAt(-0.02f), grab: true), Hands.Hand(Right, grab: true)), At(99));
        Assert.Equal(OverlayPlacement.MinWidth, tiny.Placement.Width, 3);
    }

    [Fact]
    public void LettingGoWithTheSecondHandKeepsTheSizeAndTheFirstStillCarries()
    {
        var interaction = Carried();
        interaction.Update(Hands.Both(Hands.Hand(LeftAt(-0.2f), grab: true), Hands.Hand(Right, grab: true)), At(33));
        interaction.Update(Hands.Both(Hands.Hand(LeftAt(-0.4f), grab: true), Hands.Hand(Right, grab: true)), At(66));

        var released = interaction.Update(Hands.Both(Hands.Hand(LeftAt(-0.2f)), Hands.Hand(Right, grab: true)), At(99));

        Assert.Equal(OverlayPlacement.Default.Width * 2, released.Placement.Width, 3);
        Assert.Equal(Hand.Right, released.Holding);

        // Moving the left hand back after letting go changes nothing.
        var after = interaction.Update(Hands.Both(Hands.Hand(LeftAt(-0.1f)), Hands.Hand(Right, grab: true)), At(132));
        Assert.Equal(OverlayPlacement.Default.Width * 2, after.Placement.Width, 3);
    }

    [Fact]
    public void AGripThatWasAlreadyClosedDoesNotStretch()
    {
        // The left hand was squeezing before the right picked the panel up: that squeeze is about
        // something else, not a second hold.
        var interaction = new OverlayInteraction(OverlayPlacement.Default);
        interaction.Update(Hands.Both(Hands.Hand(LeftAt(-0.2f), grab: true), Hands.Hand(Right)), At(0));
        interaction.Update(Hands.Both(Hands.Hand(LeftAt(-0.2f), grab: true), Hands.Hand(Right, grab: true)), At(33));

        var moved = interaction.Update(Hands.Both(Hands.Hand(LeftAt(-0.4f), grab: true), Hands.Hand(Right, grab: true)), At(66));

        Assert.Equal(OverlayPlacement.Default.Width, moved.Placement.Width, 3);
    }

    [Fact]
    public void AGripWithTheRayOffThePanelDoesNotStretch()
    {
        var interaction = Carried();
        var away = Hands.AimingAt(new Vector3(-0.2f, 0, 0), new Vector3(-3f, 0, 0));

        interaction.Update(Hands.Both(Hands.Hand(away, grab: true), Hands.Hand(Right, grab: true)), At(33));
        var moved = interaction.Update(
            Hands.Both(Hands.Hand(away with { Position = new Vector3(-0.4f, 0, 0) }, grab: true), Hands.Hand(Right, grab: true)),
            At(66));

        Assert.Equal(OverlayPlacement.Default.Width, moved.Placement.Width, 3);
    }
}
