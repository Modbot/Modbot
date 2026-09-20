using Modbot.Overlay.Interaction;

namespace Modbot.Overlay.OpenXr;

/// <summary>
/// One kind of controller, and which of its inputs each of the panel's actions is suggested on.
/// </summary>
/// <param name="Path">The interaction profile path OpenXR knows the controller by.</param>
/// <param name="Name">The controller's name, for the log.</param>
/// <param name="Aim">The pointing pose, under the hand's user path.</param>
/// <param name="Device">The controller's own pose, the one a panel worn on the hand hangs off.</param>
/// <param name="Grab">The input that holds the panel.</param>
/// <param name="Click">The input that taps the panel.</param>
/// <param name="Scroll">The two-axis input that scrolls, or null when the controller has none.</param>
public sealed record ControllerProfile(string Path, string Name, string Aim, string Device, string Grab, string Click, string? Scroll)
{
    /// <summary>Every binding this controller can offer for one hand: the action's name and the full input path.</summary>
    public IEnumerable<(string Action, string Path)> BindingsFor(Hand hand)
    {
        var user = hand == Hand.Left ? ControllerBindings.LeftHand : ControllerBindings.RightHand;

        yield return (ControllerBindings.Aim, user + Aim);
        yield return (ControllerBindings.Device, user + Device);
        yield return (ControllerBindings.Grab, user + Grab);
        yield return (ControllerBindings.Click, user + Click);
        if (Scroll is not null)
            yield return (ControllerBindings.Scroll, user + Scroll);
    }
}

/// <summary>
/// The panel's actions on OpenXR and the controllers they are suggested for (overlay OpenXR and
/// interaction design, 4.1).
/// </summary>
/// <remarks>
/// <para>One action set, <c>modbot</c>, with five actions that both hands share through the
/// <c>/user/hand/left</c> and <c>/user/hand/right</c> subaction paths: <c>aim</c> and
/// <c>device</c> (poses), <c>grab</c> and <c>click</c> (on or off) and <c>scroll</c> (two axes).
/// They are the same things the OpenVR runtime works out from <c>IVRSystem</c>, so everything
/// above the runtime sees one shape of input.</para>
/// <para><c>aim</c> is where the controller points and <c>device</c> is where the controller is.
/// OpenXR keeps them apart because they are not the same direction — a controller sits raked back
/// in the fist — and the panel needs both: the ray comes out of <c>aim</c>, and a panel worn on
/// the wrist hangs off <c>device</c>. <c>device</c> is bound to <c>/input/grip/pose</c>, which is
/// OpenXR's name for the controller's own pose.</para>
/// <para>Bindings are suggestions: the runtime, or the moderator through its own rebinding, has
/// the last word. A boolean action suggested on a <c>value</c> input (the Touch's trigger and
/// grip) is turned on and off by the runtime at its own threshold, which is what the spec
/// provides for and what every runtime does.</para>
/// <para><strong>The Index's grip was on its force sensor and is not any more</strong> (changed
/// 2026-09-19). The reasoning was that <c>/input/squeeze/value</c> reads as held whenever the
/// controller is merely in the hand, so the force sensor was safer. That was the wrong worry:
/// the resting reading is near zero, and what the force sensor costs is real — SteamVR thresholds
/// force for "pick a heavy thing up", which is the hard squeeze a moderator complained the panel
/// needed before it could be moved. The Index now takes <c>/input/squeeze/value</c> like every
/// other controller.</para>
/// <para>The simple controller has no thumbstick or trackpad, so it cannot scroll; its select
/// button taps and its menu button holds. Any controller the runtime maps onto it gets that.</para>
/// </remarks>
public static class ControllerBindings
{
    public const string ActionSet = "modbot";

    public const string Aim = "aim";

    /// <summary>The controller's own pose, as opposed to where it points.</summary>
    public const string Device = "device";

    public const string Grab = "grab";

    public const string Click = "click";

    public const string Scroll = "scroll";

    /// <summary>The five actions, in the order they are made.</summary>
    public static readonly IReadOnlyList<string> Actions = [Aim, Device, Grab, Click, Scroll];

    public const string LeftHand = "/user/hand/left";

    public const string RightHand = "/user/hand/right";

    /// <summary>OpenXR's name for the controller's own pose, the same on every controller.</summary>
    private const string GripPose = "/input/grip/pose";

    public static readonly ControllerProfile ValveIndex = new(
        "/interaction_profiles/valve/index_controller",
        "Valve Index",
        Aim: "/input/aim/pose",
        Device: GripPose,
        Grab: "/input/squeeze/value",
        Click: "/input/trigger/click",
        Scroll: "/input/thumbstick");

    public static readonly ControllerProfile OculusTouch = new(
        "/interaction_profiles/oculus/touch_controller",
        "Oculus Touch",
        Aim: "/input/aim/pose",
        Device: GripPose,
        Grab: "/input/squeeze/value",
        Click: "/input/trigger/value",
        Scroll: "/input/thumbstick");

    public static readonly ControllerProfile HtcVive = new(
        "/interaction_profiles/htc/vive_controller",
        "HTC Vive",
        Aim: "/input/aim/pose",
        Device: GripPose,
        Grab: "/input/squeeze/click",
        Click: "/input/trigger/click",
        Scroll: "/input/trackpad");

    public static readonly ControllerProfile Simple = new(
        "/interaction_profiles/khr/simple_controller",
        "simple controller",
        Aim: "/input/aim/pose",
        Device: GripPose,
        Grab: "/input/menu/click",
        Click: "/input/select/click",
        Scroll: null);

    /// <summary>Every controller bindings are suggested for, in the order they are offered.</summary>
    public static readonly IReadOnlyList<ControllerProfile> Profiles = [ValveIndex, OculusTouch, HtcVive, Simple];
}
