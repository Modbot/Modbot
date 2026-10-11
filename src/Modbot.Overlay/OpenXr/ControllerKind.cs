using Modbot.Overlay.Interaction;

namespace Modbot.Overlay.OpenXr;

/// <summary>
/// A runtime that knows which kind of controller each hand holds, so the panel can name the
/// buttons by what is on that controller.
/// </summary>
/// <remarks>
/// OpenXR says which controller the runtime settled on for each hand. SteamVR's OpenVR does not
/// say in a form the bindings can be matched against, so it does not implement this and the panel
/// uses plain words.
/// </remarks>
public interface IControllerKind
{
    /// <summary>The controller in this hand, or null when it is not known.</summary>
    ControllerProfile? ControllerOf(Hand hand);
}
