using Modbot.Overlay.OpenVr;

namespace Modbot.Overlay.Tests.OpenVr;

/// <summary>
/// Taking hold of the panel with an ordinary squeeze rather than a hard one, and the gap between
/// taking hold and letting go that stops a hand near the line flickering the panel on and off.
/// </summary>
public class GripHoldTests
{
    [Fact]
    public void AnOrdinaryHoldTakesThePanel()
    {
        var grip = new GripHold();

        Assert.True(grip.Squeeze(button: false, GripHold.Take));
        Assert.True(grip.Held);
    }

    [Fact]
    public void ALighterTouchThanThatDoesNotTakeIt()
    {
        var grip = new GripHold();

        Assert.False(grip.Squeeze(button: false, GripHold.Take - 0.01f));
        Assert.False(grip.Held);
    }

    /// <summary>
    /// The point of the two numbers: once the panel is held, a squeeze between the two keeps it,
    /// so a hand wavering around the line does not take and drop the panel over and over.
    /// </summary>
    [Fact]
    public void ThereIsAGapBetweenTakingHoldAndLettingGo()
    {
        Assert.True(GripHold.LetGo < GripHold.Take);

        var grip = new GripHold();
        var between = (GripHold.Take + GripHold.LetGo) / 2f;

        // Between the two from a standing start is not enough to take it.
        Assert.False(grip.Squeeze(button: false, between));

        // Take it properly, then fall back to the same squeeze: it is still held.
        Assert.True(grip.Squeeze(button: false, GripHold.Take));
        Assert.True(grip.Squeeze(button: false, between));
        Assert.True(grip.Squeeze(button: false, GripHold.LetGo + 0.001f));

        // Below the lower number it is let go.
        Assert.False(grip.Squeeze(button: false, GripHold.LetGo));
    }

    /// <summary>A Vive wand's grip is a switch with no number behind it, and the switch still counts.</summary>
    [Fact]
    public void AControllerWithNoSqueezeToReadIsHeldByItsButton()
    {
        var grip = new GripHold();

        Assert.True(grip.Squeeze(button: true, 0f));
        Assert.False(grip.Squeeze(button: false, 0f));
    }

    [Fact]
    public void ANonsenseReadingIsTakenAsNothing()
    {
        var grip = new GripHold();

        Assert.False(grip.Squeeze(button: false, float.NaN));
    }

    [Fact]
    public void ForgettingLetsGo()
    {
        var grip = new GripHold();
        grip.Squeeze(button: true, 1f);

        grip.Forget();

        Assert.False(grip.Held);
    }
}
