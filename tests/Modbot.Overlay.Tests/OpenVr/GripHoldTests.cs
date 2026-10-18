using Modbot.Companion.Presentation;
using Modbot.Overlay.OpenVr;

namespace Modbot.Overlay.Tests.OpenVr;

/// <summary>
/// Taking hold of the panel with a squeeze the moderator chose (the Grip sensitivity slider), and
/// the gap between taking hold and letting go that stops a hand near the line flickering the panel
/// on and off.
/// </summary>
public class GripHoldTests
{
    [Fact]
    public void WithoutASettingTheSqueezeIsThreeTenthsLikeXsOverlay()
    {
        var grip = new GripHold();

        Assert.Equal(0.3f, grip.Take);
        Assert.Equal(GripHold.DefaultTake, grip.Take);
        Assert.Equal(0.2f, grip.LetGo, 3);
    }

    [Fact]
    public void AnOrdinaryHoldTakesThePanel()
    {
        var grip = new GripHold();

        Assert.True(grip.Squeeze(button: false, grip.Take));
        Assert.True(grip.Held);
    }

    [Fact]
    public void ALighterTouchThanThatDoesNotTakeIt()
    {
        var grip = new GripHold();

        Assert.False(grip.Squeeze(button: false, grip.Take - 0.01f));
        Assert.False(grip.Held);
    }

    /// <summary>The point of the two numbers: a squeeze between them keeps a held panel but never takes one.</summary>
    [Fact]
    public void ThereIsAGapBetweenTakingHoldAndLettingGo()
    {
        var grip = new GripHold();
        Assert.True(grip.LetGo < grip.Take);

        var between = (grip.Take + grip.LetGo) / 2f;

        // Between the two from a standing start is not enough to take it.
        Assert.False(grip.Squeeze(button: false, between));

        // Take it properly, then fall back to the same squeeze: it is still held.
        Assert.True(grip.Squeeze(button: false, grip.Take));
        Assert.True(grip.Squeeze(button: false, between));
        Assert.True(grip.Squeeze(button: false, grip.LetGo + 0.001f));

        // Below the lower number it is let go.
        Assert.False(grip.Squeeze(button: false, grip.LetGo));
    }

    [Theory]
    [InlineData(0.75f, 0.65f)]
    [InlineData(0.5f, 0.4f)]
    [InlineData(0.3f, 0.2f)]
    [InlineData(0.15f, 0.05f)]
    public void LettingGoIsATenthBelowTheSetting(float take, float letGo)
    {
        var grip = new GripHold { Take = take };

        Assert.Equal(letGo, grip.LetGo, 3);
    }

    /// <summary>At the lightest setting a tenth below would be nothing at all, and a resting hand would hold the panel for ever.</summary>
    [Theory]
    [InlineData(0.1f)]
    [InlineData(0.12f)]
    public void LettingGoNeverDropsBelowFiveHundredths(float take)
    {
        var grip = new GripHold { Take = take };

        Assert.Equal(GripHold.LowestLetGo, grip.LetGo, 3);

        Assert.True(grip.Squeeze(button: false, take));
        Assert.False(grip.Squeeze(button: false, 0f));
    }

    [Theory]
    [InlineData(0.75f)]
    [InlineData(0.1f)]
    public void ASettingMovesTheSqueezeThatTakesThePanel(float take)
    {
        var grip = new GripHold { Take = take };

        Assert.False(grip.Squeeze(button: false, take - 0.01f));
        Assert.True(grip.Squeeze(button: false, take));
    }

    [Fact]
    public void ASettingThatIsNotANumberLeavesTheSqueezeAlone()
    {
        var grip = new GripHold { Take = 0.5f };

        grip.Take = float.NaN;

        Assert.Equal(0.5f, grip.Take);
    }

    /// <summary>The slider's ends are the bounds the plan names: 0.1 at its lightest, 0.75 at its hardest, 0.3 at its default.</summary>
    [Fact]
    public void TheSlidersStepsRunFromThreeQuartersToATenth()
    {
        Assert.Equal(0.75f, CompanionSettings.GripTake(CompanionSettings.MinGripSensitivity));
        Assert.Equal(0.1f, CompanionSettings.GripTake(CompanionSettings.MaxGripSensitivity));
        Assert.Equal(GripHold.DefaultTake, CompanionSettings.GripTake(CompanionSettings.DefaultGripSensitivity));

        // Each step to the right is a lighter squeeze.
        for (var level = CompanionSettings.MinGripSensitivity; level < CompanionSettings.MaxGripSensitivity; level++)
            Assert.True(CompanionSettings.GripTake(level + 1) < CompanionSettings.GripTake(level));
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
