using System.Numerics;
using Modbot.Companion.Overlay;
using Modbot.Overlay.Interaction;

namespace Modbot.Overlay.Tests.Interaction;

/// <summary>
/// The hand a panel is worn on is left out of everything that can be done to the panel, and the
/// other hand is not.
/// </summary>
/// <remarks>
/// A panel on a wrist sits where that hand is, so that hand's ray lands on it constantly and every
/// squeeze of that hand — and a moderator squeezes their grip all day for reasons of their own —
/// was taking hold of a panel already travelling with it (overlay OpenXR and interaction design
/// §6.3).
/// </remarks>
public class WornHandTests
{
    private static TimeSpan At(int ms) => TimeSpan.FromMilliseconds(ms);

    /// <summary>Where each controller is held: far enough apart that neither is inside the other's panel.</summary>
    private static Pose Wrist(Hand hand) => new(
        new Vector3(hand == Hand.Left ? -0.25f : 0.25f, 1.1f, -0.4f),
        Quaternion.Identity);

    private static OverlayPlacement WornOn(Hand hand) => OverlayPlacement.Default with
    {
        Anchor = hand == Hand.Left ? OverlayAnchor.LeftHand : OverlayAnchor.RightHand,
        Offset = OverlayPlacement.WristOffset,
        Width = OverlayPlacement.WristWidth,
    };

    /// <summary>Where the panel actually hangs, and a point out in front of its face to aim from.</summary>
    private static (Pose Panel, Vector3 Eye) Panel(OverlayPlacement placement, OverlayTracking tracking)
    {
        var panel = PanelGeometry.PanelPose(placement, tracking)!.Value;
        return (panel, panel.Position + (Vector3.Transform(Vector3.UnitZ, panel.Rotation) * 0.4f));
    }

    /// <summary>
    /// Both controllers held where they are held, with the worn hand aimed straight at its own
    /// panel — which is roughly what a hand wearing a panel does all the time — and the other hand
    /// aimed at it too.
    /// </summary>
    private static OverlayTracking Both(
        OverlayPlacement placement,
        Hand worn,
        bool wornGrab = false,
        bool wornClick = false,
        Vector2 wornScroll = default,
        bool otherGrab = false)
    {
        var resting = new OverlayTracking(
            Pose.Identity,
            Hands.Hand(Wrist(Hand.Left)),
            Hands.Hand(Wrist(Hand.Right)));

        var (panel, eye) = Panel(placement, resting);
        var other = worn == Hand.Left ? Hand.Right : Hand.Left;

        // The worn hand's own ray, pushed onto its panel: the controller stays where it is and is
        // turned to face what is strapped to it.
        var wornHand = Hands.Hand(
            new Pose(Wrist(worn).Position, Hands.Facing(panel.Position - Wrist(worn).Position)),
            grab: wornGrab,
            click: wornClick,
            scroll: wornScroll,
            device: Wrist(worn));

        var otherHand = Hands.Hand(Hands.AimingAt(eye, panel.Position), grab: otherGrab, device: Wrist(other));

        return worn == Hand.Left
            ? new OverlayTracking(Pose.Identity, wornHand, otherHand)
            : new OverlayTracking(Pose.Identity, otherHand, wornHand);
    }

    [Theory]
    [InlineData(OverlayAnchor.LeftHand, Hand.Left)]
    [InlineData(OverlayAnchor.RightHand, Hand.Right)]
    public void AWristAnchorIgnoresThatHand(OverlayAnchor anchor, Hand ignored)
    {
        var interaction = new OverlayInteraction(OverlayPlacement.Default with { Anchor = anchor });

        Assert.Equal(ignored, interaction.Ignoring);
    }

    [Theory]
    [InlineData(OverlayAnchor.Head)]
    [InlineData(OverlayAnchor.World)]
    public void APanelNobodyIsWearingIgnoresNeitherHand(OverlayAnchor anchor)
    {
        var interaction = new OverlayInteraction(OverlayPlacement.Default with { Anchor = anchor });

        Assert.Null(interaction.Ignoring);
    }

    [Theory]
    [InlineData(Hand.Left)]
    [InlineData(Hand.Right)]
    public void TheHandWearingThePanelDoesNotPointAtIt(Hand worn)
    {
        var placement = WornOn(worn);
        var interaction = new OverlayInteraction(placement);

        var result = interaction.Update(Both(placement, worn), At(0));

        // The other hand is aimed at it too, so a pointer is found — it is simply never the
        // hand the panel is strapped to.
        Assert.NotNull(result.Pointer);
        Assert.NotEqual(worn, result.Pointer.Value.Hand);
    }

    [Theory]
    [InlineData(Hand.Left)]
    [InlineData(Hand.Right)]
    public void TheHandWearingThePanelDoesNotTakeIt(Hand worn)
    {
        var placement = WornOn(worn);
        var interaction = new OverlayInteraction(placement);

        // Nothing in the room but the worn hand, squeezing hard, aimed at its own panel.
        var only = Both(placement, worn, wornGrab: true);
        var alone = worn == Hand.Left
            ? new OverlayTracking(Pose.Identity, only.Left, HandState.Missing)
            : new OverlayTracking(Pose.Identity, HandState.Missing, only.Right);

        var result = interaction.Update(alone, At(0));

        Assert.Null(result.Holding);
        Assert.Null(result.Pointer);
        Assert.False(result.PlacementChanged);
        Assert.Equal(placement, result.Placement);
    }

    [Theory]
    [InlineData(Hand.Left)]
    [InlineData(Hand.Right)]
    public void TheHandWearingThePanelDoesNotTapOrScrollIt(Hand worn)
    {
        var placement = WornOn(worn);
        var interaction = new OverlayInteraction(placement);

        var only = Both(placement, worn, wornClick: true, wornScroll: new Vector2(0, -1f));
        var alone = worn == Hand.Left
            ? new OverlayTracking(Pose.Identity, only.Left, HandState.Missing)
            : new OverlayTracking(Pose.Identity, HandState.Missing, only.Right);

        var result = interaction.Update(alone, At(0));

        Assert.Empty(result.Clicks);
        Assert.Equal(Vector2.Zero, result.Scroll);
    }

    /// <summary>
    /// The double grip that sends the panel back in front of the head is reached through pointing
    /// like everything else, so the worn hand cannot do it either.
    /// </summary>
    [Theory]
    [InlineData(Hand.Left)]
    [InlineData(Hand.Right)]
    public void TheHandWearingThePanelCannotDoubleGripItHome(Hand worn)
    {
        var placement = WornOn(worn);
        var interaction = new OverlayInteraction(placement);

        var only = Both(placement, worn, wornGrab: true);
        var squeezing = worn == Hand.Left
            ? new OverlayTracking(Pose.Identity, only.Left, HandState.Missing)
            : new OverlayTracking(Pose.Identity, HandState.Missing, only.Right);
        var open = worn == Hand.Left
            ? new OverlayTracking(Pose.Identity, only.Left with { Grab = false }, HandState.Missing)
            : new OverlayTracking(Pose.Identity, HandState.Missing, only.Right with { Grab = false });

        interaction.Update(squeezing, At(0));
        interaction.Update(open, At(100));
        interaction.Update(squeezing, At(200));

        Assert.Equal(placement.Anchor, interaction.Placement.Anchor);
    }

    /// <summary>The way a panel leaves a wrist: the other hand points at it and grips.</summary>
    [Theory]
    [InlineData(Hand.Left)]
    [InlineData(Hand.Right)]
    public void TheOtherHandTakesThePanelOffTheWrist(Hand worn)
    {
        var placement = WornOn(worn);
        var interaction = new OverlayInteraction(placement);
        var other = worn == Hand.Left ? Hand.Right : Hand.Left;

        var taken = interaction.Update(Both(placement, worn, otherGrab: true), At(0));

        Assert.Equal(other, taken.Holding);
        Assert.Equal(other == Hand.Left ? OverlayAnchor.LeftHand : OverlayAnchor.RightHand, taken.Placement.Anchor);

        // And with it carried, the hand doing the carrying is not ignored, or the carry could
        // never be ended.
        Assert.Null(interaction.Ignoring);
    }

    /// <summary>
    /// Carrying is the one thing the anchored hand may do. While the panel is on its way somewhere
    /// it is anchored to the hand carrying it, and that hand has to go on being read.
    /// </summary>
    [Fact]
    public void TheHandCarryingThePanelIsNotIgnoredWhileItCarriesIt()
    {
        var interaction = new OverlayInteraction(OverlayPlacement.Default);
        var aim = Hands.AimingAt(Vector3.Zero, new Vector3(0.35f, -0.28f, -1.0f));

        var taken = interaction.Update(Hands.RightOnly(Hands.Hand(aim, grab: true)), At(0));

        // Taking it anchors the panel to that hand, which is exactly the anchor that would
        // otherwise be ignored.
        Assert.Equal(OverlayAnchor.RightHand, taken.Placement.Anchor);
        Assert.Equal(Hand.Right, taken.Holding);
        Assert.Null(interaction.Ignoring);

        // It goes on following that hand, all the way across the room.
        var moved = aim with { Position = new Vector3(0.5f, 0, 0) };
        var carried = interaction.Update(Hands.RightOnly(Hands.Hand(moved, grab: true)), At(100));

        Assert.Equal(Hand.Right, carried.Holding);
        Assert.Equal(OverlayAnchor.RightHand, carried.Placement.Anchor);
        Assert.Null(interaction.Ignoring);
    }

    /// <summary>
    /// The other half of the same rule: once the panel has settled on a wrist, that hand goes back
    /// to being ignored, so the squeeze that put it there is not read as picking it up again.
    /// </summary>
    [Theory]
    [InlineData(Hand.Left)]
    [InlineData(Hand.Right)]
    public void AHandThatHasJustPutThePanelOnItsOwnWristIsIgnoredAgain(Hand hand)
    {
        var centre = new Vector3(0.35f, -0.28f, -1.0f);
        var interaction = new OverlayInteraction(OverlayPlacement.Default);

        // Reaching out: the hand is five centimetres in front of the panel, so what it takes hold
        // of is right at the hand and letting go leaves it on that wrist.
        var close = Hands.AimingAt(centre + new Vector3(0, 0, 0.05f), centre);
        var gripping = Hands.Hand(close, grab: true);
        var open = Hands.Hand(close);

        OverlayTracking Only(HandState state) => hand == Hand.Left
            ? Hands.Both(state, HandState.Missing)
            : Hands.RightOnly(state);

        interaction.Update(Only(gripping), At(0));
        Assert.Null(interaction.Ignoring);

        var released = interaction.Update(Only(open), At(100));

        Assert.Equal(hand == Hand.Left ? OverlayAnchor.LeftHand : OverlayAnchor.RightHand, released.Placement.Anchor);
        Assert.Equal(hand, interaction.Ignoring);

        // And it stays ignored: squeezing that hand again does nothing to the panel it is wearing.
        interaction.Update(Only(open), At(2000));
        var squeezed = interaction.Update(Only(gripping), At(2100));

        Assert.Null(squeezed.Holding);
        Assert.Null(squeezed.Pointer);
        Assert.Equal(released.Placement, squeezed.Placement);
    }

    /// <summary>
    /// A hand that was squeezing while it was being ignored is not stuck once the panel moves off
    /// it: it does not snatch the panel back the moment the rule lifts, and its next fresh squeeze
    /// works normally.
    /// </summary>
    [Theory]
    [InlineData(Hand.Left)]
    [InlineData(Hand.Right)]
    public void AHandSqueezingWhileIgnoredIsNotStuckAfterwards(Hand worn)
    {
        var placement = WornOn(worn);
        var interaction = new OverlayInteraction(placement);

        // Worn, with that hand squeezing away for its own reasons.
        interaction.Update(Both(placement, worn, wornGrab: true), At(0));
        interaction.Update(Both(placement, worn, wornGrab: true), At(100));
        Assert.Equal(placement.Anchor, interaction.Placement.Anchor);

        // The settings page puts it back in front of the head while that hand is still squeezing.
        interaction.Place(OverlayPlacement.Default);
        Assert.Null(interaction.Ignoring);

        var head = OverlayPlacement.Default;

        // Still squeezing, now aimed at the panel in front of the head: it must not leap into
        // that hand, because no grip has closed since it stopped being ignored.
        var aim = Hands.AimingAt(Vector3.Zero, new Vector3(0.35f, -0.28f, -1.0f));
        var held = Hands.Hand(aim, grab: true);
        var stillSqueezing = worn == Hand.Left
            ? new OverlayTracking(Pose.Identity, held, HandState.Missing)
            : new OverlayTracking(Pose.Identity, HandState.Missing, held);

        var after = interaction.Update(stillSqueezing, At(200));
        Assert.Null(after.Holding);
        Assert.Equal(head.Anchor, after.Placement.Anchor);

        // Let go, squeeze again: that hand works exactly as it always did.
        var open = Hands.Hand(aim);
        var opened = worn == Hand.Left
            ? new OverlayTracking(Pose.Identity, open, HandState.Missing)
            : new OverlayTracking(Pose.Identity, HandState.Missing, open);

        interaction.Update(opened, At(2000));
        var again = interaction.Update(stillSqueezing, At(2100));

        Assert.Equal(worn, again.Holding);
    }

    /// <summary>
    /// The rule is read off the anchor and nothing else, so it holds whichever wrist the panel is
    /// on and however the hands happen to be pointing.
    /// </summary>
    [Fact]
    public void MovingThePanelFromOneWristToTheOtherMovesWhichHandIsIgnored()
    {
        var interaction = new OverlayInteraction(WornOn(Hand.Left));
        Assert.Equal(Hand.Left, interaction.Ignoring);

        interaction.Place(WornOn(Hand.Right));
        Assert.Equal(Hand.Right, interaction.Ignoring);

        interaction.Place(OverlayPlacement.Default with { Anchor = OverlayAnchor.World });
        Assert.Null(interaction.Ignoring);
    }
}
