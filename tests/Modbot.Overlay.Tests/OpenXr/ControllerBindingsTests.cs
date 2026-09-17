using Modbot.Overlay.Interaction;
using Modbot.Overlay.OpenXr;

namespace Modbot.Overlay.Tests.OpenXr;

/// <summary>
/// The binding tables (overlay OpenXR and interaction design, 4.1): every controller offers aim,
/// grab and click for both hands, every controller with a thumbstick or trackpad offers scroll,
/// and the simple controller does not.
/// </summary>
public class ControllerBindingsTests
{
    public static TheoryData<string> ProfileNames()
        => new(ControllerBindings.Profiles.Select(p => p.Name));

    private static ControllerProfile Profile(string name) => ControllerBindings.Profiles.Single(p => p.Name == name);

    [Fact]
    public void TheFourControllersAreOfferedUnderTheirOpenXrPaths()
    {
        Assert.Equal(
            [
                "/interaction_profiles/valve/index_controller",
                "/interaction_profiles/oculus/touch_controller",
                "/interaction_profiles/htc/vive_controller",
                "/interaction_profiles/khr/simple_controller",
            ],
            ControllerBindings.Profiles.Select(p => p.Path));
    }

    [Fact]
    public void TheActionSetAndItsActionsHaveTheNamesTheSpecGives()
    {
        Assert.Equal("modbot", ControllerBindings.ActionSet);
        Assert.Equal(["aim", "grab", "click", "scroll"], ControllerBindings.Actions);
    }

    [Theory]
    [MemberData(nameof(ProfileNames))]
    public void EveryControllerOffersAimGrabAndClickForBothHands(string name)
    {
        var profile = Profile(name);

        foreach (var hand in new[] { Hand.Left, Hand.Right })
        {
            var bindings = profile.BindingsFor(hand).ToList();
            var actions = bindings.Select(b => b.Action).ToList();

            Assert.Contains("aim", actions);
            Assert.Contains("grab", actions);
            Assert.Contains("click", actions);
            Assert.Equal(actions.Count, actions.Distinct().Count());

            var user = hand == Hand.Left ? "/user/hand/left/input/" : "/user/hand/right/input/";
            Assert.All(bindings, b => Assert.StartsWith(user, b.Path));
            Assert.Equal(user + "aim/pose", bindings.Single(b => b.Action == "aim").Path);
        }
    }

    [Theory]
    [InlineData("Valve Index", "/user/hand/left/input/thumbstick")]
    [InlineData("Oculus Touch", "/user/hand/left/input/thumbstick")]
    [InlineData("HTC Vive", "/user/hand/left/input/trackpad")]
    public void ControllersWithAThumbstickOrTrackpadScrollOnIt(string name, string expected)
    {
        var scroll = Profile(name).BindingsFor(Hand.Left).Single(b => b.Action == "scroll");

        Assert.Equal(expected, scroll.Path);
    }

    [Fact]
    public void TheSimpleControllerCannotScroll()
    {
        var simple = Profile("simple controller");

        Assert.Null(simple.Scroll);
        Assert.DoesNotContain("scroll", simple.BindingsFor(Hand.Left).Select(b => b.Action));
        Assert.DoesNotContain("scroll", simple.BindingsFor(Hand.Right).Select(b => b.Action));
        Assert.Equal(3, simple.BindingsFor(Hand.Right).Count());
    }

    [Fact]
    public void TheSimpleControllerTapsWithSelectAndHoldsWithMenu()
    {
        var bindings = Profile("simple controller").BindingsFor(Hand.Right).ToDictionary(b => b.Action, b => b.Path);

        Assert.Equal("/user/hand/right/input/select/click", bindings["click"]);
        Assert.Equal("/user/hand/right/input/menu/click", bindings["grab"]);
    }

    [Fact]
    public void TheIndexGripIsItsForceSensorNotItsCapacitiveValue()
    {
        // The capacitive value reads as held whenever the controller is simply in the hand.
        Assert.Equal("/user/hand/left/input/squeeze/force", Profile("Valve Index").BindingsFor(Hand.Left).Single(b => b.Action == "grab").Path);
    }
}
