using System.Buffers.Binary;
using System.Numerics;
using Modbot.Overlay.Interaction;

namespace Modbot.Overlay.OpenVr;

/// <summary>
/// The byte layouts <c>IVRInput</c> fills in for an analogue, a digital and a pose action, read by
/// offset like <see cref="OpenVrLayouts"/>.
/// </summary>
/// <remarks>
/// Taken from <c>InputAnalogActionData_t</c>, <c>InputDigitalActionData_t</c> and
/// <c>InputPoseActionData_t</c> in <c>openvr.h</c> at v2.15.6, the tag of the vendored
/// <c>openvr_api</c>. None of the three sits inside one of that header's <c>pack(4)</c> regions
/// (those are the Linux-only ones around <c>VREvent_t</c> and two others), so a field is at its
/// natural offset on every platform: a one-byte <c>bActive</c>, padding up to the 8-byte
/// <c>activeOrigin</c> at 8, then the payload. The pose action's payload is the same 80-byte
/// <c>TrackedDevicePose_t</c> that <see cref="OpenVrLayouts.ReadPose"/> already reads.
/// </remarks>
public static class InputActionLayouts
{
    public const int AnalogSize = 48;
    public const int DigitalSize = 24;
    public const int PoseSize = 96;

    private const int Payload = 16;

    /// <summary>Whether the action is bound and live, and its x and y (a one-value action uses x only).</summary>
    public static (bool Active, Vector2 Value) ReadAnalog(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < AnalogSize || bytes[0] == 0)
            return (false, Vector2.Zero);

        return (true, new Vector2(F(bytes, Payload), F(bytes, Payload + 4)));
    }

    /// <summary>Whether the action is bound and live, and whether it is pressed.</summary>
    public static (bool Active, bool State) ReadDigital(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < DigitalSize || bytes[0] == 0)
            return (false, false);

        return (true, bytes[Payload] != 0);
    }

    /// <summary>Whether the action is bound and live, and the pose (null when SteamVR says it is not valid).</summary>
    public static (bool Active, Pose? Pose) ReadPose(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < PoseSize || bytes[0] == 0)
            return (false, null);

        return (true, OpenVrLayouts.ReadPose(bytes[Payload..]));
    }

    private static float F(ReadOnlySpan<byte> bytes, int offset)
        => BinaryPrimitives.ReadSingleLittleEndian(bytes[offset..]);
}

/// <summary>
/// One hand's actions as SteamVR Input reported them in a poll, before they are turned into the
/// <see cref="HandState"/> everything above reads.
/// </summary>
/// <param name="HasData">Whether any of the hand's actions was bound and live. False for a hand SteamVR has nothing bound for.</param>
/// <param name="Grab">How far the grip is squeezed, 0 to 1. Zero where the controller has no analogue grip.</param>
/// <param name="Grip">The grip as a button, for a controller whose grip is a switch.</param>
/// <param name="Click">The trigger.</param>
/// <param name="Scroll">Thumbstick or touchpad.</param>
/// <param name="Device">Where the controller is, along its body; null when it is not tracked.</param>
/// <param name="Aim">SteamVR's pointing pose, or null when the binding gives none.</param>
public readonly record struct ActionHand(
    bool HasData, float Grab, bool Grip, bool Click, Vector2 Scroll, Pose? Device, Pose? Aim)
{
    /// <summary>
    /// The same hand data the legacy read produces. The grab goes through the hand's
    /// <see cref="GripHold"/> as the squeeze, with the grip button beside it, exactly as the legacy
    /// axis and button do. Where SteamVR gives no pointing pose, the legacy tilt off the device
    /// pose stands in for it.
    /// </summary>
    public HandState ToHandState(GripHold hold)
    {
        if (Device is not { } device)
        {
            hold.Forget();
            return HandState.Missing;
        }

        return new HandState(
            true,
            Aim ?? ControllerPointing.Aim(device),
            device,
            hold.Squeeze(Grip, Grab),
            Click,
            Scroll);
    }
}

/// <summary>Where the controller data comes from.</summary>
public enum ControllerInputSource
{
    /// <summary><c>IVRSystem::GetControllerState</c>, the way the overlay always read it.</summary>
    Legacy,

    /// <summary>SteamVR Input actions, the ones Modbot's action manifest names.</summary>
    SteamVrInput,
}

/// <summary>
/// Which of the two sources the controllers are read from. It starts on the legacy read and
/// moves to SteamVR Input for good the first time an action reports data.
/// </summary>
/// <remarks>
/// <para>Legacy first, because nothing is lost by it while SteamVR is still loading Modbot's
/// bindings, and an action manifest that loaded but has no binding for the controller on the head
/// reports nothing at all: no input source reporting data is the signal that SteamVR Input is
/// not going to work and the legacy read is the answer.</para>
/// <para>Nothing here reads a clock. How long "no data" has gone on is counted in polls with a
/// controller in view, and only to say so once in the log (<see cref="Settled"/>); the choice
/// itself is never made on a timer, so a binding that loads late still takes over.</para>
/// </remarks>
/// <param name="actionsAvailable">False when <c>IVRInput</c> could not be got or the manifest did not load.</param>
public sealed class InputSourceChoice(bool actionsAvailable)
{
    /// <summary>Polls with a controller in view and no action data before SteamVR Input is written off in the log.</summary>
    public const int QuietPollsBeforeLegacy = 1800;

    private bool _proven;
    private int _quiet;

    public ControllerInputSource Source => _proven ? ControllerInputSource.SteamVrInput : ControllerInputSource.Legacy;

    /// <summary>Whether the source is worth reporting: SteamVR Input works, or it is plain it is not going to.</summary>
    public bool Settled => _proven || !actionsAvailable || _quiet >= QuietPollsBeforeLegacy;

    /// <param name="actionsReportData">Whether any hand's action was bound and live this poll.</param>
    /// <param name="controllerSeen">Whether SteamVR has a controller in either hand this poll.</param>
    public ControllerInputSource Next(bool actionsReportData, bool controllerSeen)
    {
        if (actionsAvailable && !_proven)
        {
            if (actionsReportData)
                _proven = true;
            else if (controllerSeen && _quiet < QuietPollsBeforeLegacy)
                _quiet++;
        }

        return Source;
    }
}
