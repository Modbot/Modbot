using Modbot.Overlay.Interaction;
using Modbot.Overlay.OpenXr;

namespace Modbot.Overlay.Tests.OpenXr;

/// <summary>
/// The binding tables (overlay OpenXR and interaction design, 4.1): every controller offers aim,
/// the controller's own pose, grab and click for both hands, every controller with a thumbstick or
/// trackpad offers scroll, and the simple controller does not.
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
        Assert.Equal(["aim", "device", "grab", "click", "scroll"], ControllerBindings.Actions);
    }

    /// <summary>
    /// Where a controller points and where it is are two different poses, and the panel needs
    /// both: the cursor's ray comes out of <c>aim</c>, and a panel worn on the wrist hangs off
    /// <c>device</c>. OpenXR's name for the second is the grip pose, on every controller.
    /// </summary>
    [Theory]
    [MemberData(nameof(ProfileNames))]
    public void EveryControllerOffersItsOwnPoseAsWellAsWhereItPoints(string name)
    {
        foreach (var hand in new[] { Hand.Left, Hand.Right })
        {
            var bindings = Profile(name).BindingsFor(hand).ToDictionary(b => b.Action, b => b.Path);
            var user = hand == Hand.Left ? "/user/hand/left/input/" : "/user/hand/right/input/";

            Assert.Equal(user + "aim/pose", bindings["aim"]);
            Assert.Equal(user + "grip/pose", bindings["device"]);
        }
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

        // Aim, the controller's own pose, grab and click, and nothing else.
        Assert.Equal(4, simple.BindingsFor(Hand.Right).Count());
    }

    [Fact]
    public void TheSimpleControllerTapsWithSelectAndHoldsWithMenu()
    {
        var bindings = Profile("simple controller").BindingsFor(Hand.Right).ToDictionary(b => b.Action, b => b.Path);

        Assert.Equal("/user/hand/right/input/select/click", bindings["click"]);
        Assert.Equal("/user/hand/right/input/menu/click", bindings["grab"]);
    }

    /// <summary>
    /// The Index grabs on how far its grip is squeezed, not on its force sensor.
    /// </summary>
    /// <remarks>
    /// This used to be the other way round, guarding the opposite decision: the force sensor was
    /// chosen because the squeeze value was thought to read as held whenever the controller is
    /// merely in the hand. That was the wrong worry — the resting reading is near zero — and the
    /// force sensor cost what a moderator wearing the headset then reported, because a runtime
    /// thresholds force for picking a heavy thing up and the panel needed a hard squeeze before it
    /// would move (overlay OpenXR and interaction design §6.1).
    /// </remarks>
    [Fact]
    public void TheIndexGripIsHowFarItIsSqueezedAndNotItsForceSensor()
    {
        var grab = Profile("Valve Index").BindingsFor(Hand.Left).Single(b => b.Action == "grab").Path;

        Assert.Equal("/user/hand/left/input/squeeze/value", grab);

        // Every controller now grabs on an ordinary squeeze; none is singled out for force.
        Assert.DoesNotContain(
            ControllerBindings.Profiles,
            p => p.BindingsFor(Hand.Left).Any(b => b.Action == "grab" && b.Path.EndsWith("/squeeze/force", StringComparison.Ordinal)));
    }
}
