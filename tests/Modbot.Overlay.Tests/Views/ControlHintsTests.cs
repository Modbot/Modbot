using Modbot.Overlay.OpenXr;
using Modbot.Overlay.Views;

namespace Modbot.Overlay.Tests.Views;

/// <summary>
/// What the controller hints say. Each is checked against what the controls really do: a grip
/// takes the panel, the other hand's grip sizes it, and a stick pushes it only while it is held.
/// </summary>
public class ControlHintsTests
{
    [Fact]
    public void PointingSaysHowToTakeAndSizeItAndNothingAboutTheStick()
    {
        var hints = ControlHints.For(null, holding: false);

        Assert.Equal(new ControlHint("Grip", "move"), hints.Move);
        Assert.Equal(new ControlHint("Both grips", "larger / smaller"), hints.Resize);
        Assert.Null(hints.Distance);
    }

    [Fact]
    public void HoldingSaysHowToSizeAndPushItAndNotHowToTakeIt()
    {
        var hints = ControlHints.For(null, holding: true);

        Assert.Null(hints.Move);
        Assert.Equal(new ControlHint("Both grips", "larger / smaller"), hints.Resize);
        Assert.Equal(new ControlHint("Stick", "closer / farther"), hints.Distance);
    }

    [Fact]
    public void ControllersWithAStickAndAGripUsePlainWords()
    {
        foreach (var controller in new[] { ControllerBindings.ValveIndex, ControllerBindings.OculusTouch })
        {
            Assert.Equal("Grip", ControlHints.For(controller, holding: false).Move?.Control);
            Assert.Equal("Stick", ControlHints.For(controller, holding: true).Distance?.Control);
        }
    }

    [Fact]
    public void TheViveHasATrackpad()
    {
        Assert.Equal("Trackpad", ControlHints.For(ControllerBindings.HtcVive, holding: true).Distance?.Control);
        Assert.Equal("Grip", ControlHints.For(ControllerBindings.HtcVive, holding: false).Move?.Control);
    }

    [Fact]
    public void TheSimpleControllerHoldsWithItsMenuButtonAndHasNothingToPushWith()
    {
        var pointing = ControlHints.For(ControllerBindings.Simple, holding: false);
        var holding = ControlHints.For(ControllerBindings.Simple, holding: true);

        Assert.Equal(new ControlHint("Menu button", "move"), pointing.Move);
        Assert.Equal("Both menu buttons", holding.Resize?.Control);
        Assert.Null(holding.Distance);
    }

    [Fact]
    public void EveryControllerTheBindingsKnowGetsHintsThatMatchItsBindings()
    {
        // The wording follows the profile's own inputs, so a new profile cannot be named wrongly
        // without this failing.
        foreach (var controller in ControllerBindings.Profiles)
        {
            var hints = ControlHints.For(controller, holding: true);

            Assert.Equal(controller.Scroll is null, hints.Distance is null);
            Assert.Equal(controller.Grab.EndsWith("/menu/click", StringComparison.Ordinal), hints.Resize!.Control.Contains("menu", StringComparison.Ordinal));
        }
    }
}
