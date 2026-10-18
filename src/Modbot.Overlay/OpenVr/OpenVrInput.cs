using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using Modbot.Overlay.Interaction;

namespace Modbot.Overlay.OpenVr;

/// <summary>
/// SteamVR Input, through <c>IVRInput</c>: the grab, click, scroll and pointing the panels need,
/// read as actions from the manifest in <c>steamvr-input/</c> beside the exe.
/// </summary>
/// <remarks>
/// <para><strong>Where the numbers come from.</strong> The interface version, the slot order and
/// the struct layouts are Valve's own: <c>IVRInput_011</c> and <c>VR_IVRInput_FnTable</c> in
/// <c>headers/openvr_capi.h</c>, and <c>InputAnalogActionData_t</c>,
/// <c>InputDigitalActionData_t</c>, <c>InputPoseActionData_t</c> and <c>VRActiveActionSet_t</c> in
/// <c>headers/openvr.h</c>, both at tag v2.15.6, the version of the vendored <c>openvr_api</c>
/// (see <see cref="OpenVrInterop"/>). Same raw function-table style as <see cref="OpenVrSystem"/>;
/// no binding package.</para>
/// <para><strong>Why the moderator can rebind it.</strong> The actions are Modbot's own, so SteamVR
/// lists them under Modbot in its controller bindings, with the defaults in the binding files
/// beside the manifest, and a moderator who wants the grab on another input can move it there.</para>
/// <para><strong>It takes nothing from VRChat.</strong> The one action set is activated with
/// priority 0 and for every device. A priority is what makes SteamVR switch off the same inputs
/// for the game, and an overlay's set only does that when it is given a priority in Valve's
/// overlay range, so with none, VRChat keeps every input Modbot reads.</para>
/// <para>Nothing here is sent anywhere: the values decide where the cursor is and whether the
/// panel is held, and are dropped.</para>
/// </remarks>
internal sealed unsafe class OpenVrInput
{
    /// <summary><c>IVRInput_011</c>, the version in the pinned header.</summary>
    internal const string InterfaceVersion = "FnTable:IVRInput_011";

    // Slots in VR_IVRInput_FnTable for IVRInput_011, counted in headers/openvr_capi.h.
    private const int SetActionManifestPath = 0;
    private const int GetActionSetHandle = 1;
    private const int GetActionHandle = 2;
    private const int GetInputSourceHandle = 3;
    private const int UpdateActionState = 4;
    private const int GetDigitalActionData = 5;
    private const int GetAnalogActionData = 6;
    private const int GetPoseActionDataRelativeToNow = 7;

    private const string ActionSetName = "/actions/modbot";
    private const string GrabName = "/actions/modbot/in/grab";
    private const string GripName = "/actions/modbot/in/grip";
    private const string ClickName = "/actions/modbot/in/click";
    private const string ScrollName = "/actions/modbot/in/scroll";
    private const string AimName = "/actions/modbot/in/aim";
    private const string HandName = "/actions/modbot/in/hand";

    /// <summary>Where the manifest and the binding files are, beside the exe.</summary>
    internal static string ManifestPath => Path.Combine(AppContext.BaseDirectory, "steamvr-input", "actions.json");

    /// <summary><c>VRActiveActionSet_t</c>: 32 bytes, in the order the header declares them.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct ActiveActionSet
    {
        public ulong ActionSet;
        public ulong RestrictedToDevice;
        public ulong SecondaryActionSet;
        public uint Padding;
        public int Priority;
    }

    private readonly nint _table;
    private ulong _actionSet;
    private ulong _grab;
    private ulong _grip;
    private ulong _click;
    private ulong _scroll;
    private ulong _aim;
    private ulong _hand;
    private ulong _left;
    private ulong _right;

    private OpenVrInput(nint table)
    {
        _table = table;
    }

    /// <summary>
    /// The input interface with Modbot's manifest loaded and every handle got, or null with the
    /// reason, in which case the legacy read is used.
    /// </summary>
    public static OpenVrInput? Open(string manifestPath, out string reason)
    {
        var table = OpenVrInterop.GetGenericInterface(InterfaceVersion, out var error);
        if (table == 0 || error != VrInitError.None)
        {
            reason = $"SteamVR does not have {InterfaceVersion}";
            return null;
        }

        if (!File.Exists(manifestPath))
        {
            reason = "the action manifest is not beside the exe";
            return null;
        }

        var input = new OpenVrInput(table);

        // Before the first UpdateActionState, as the header asks.
        var manifestError = input.SetManifest(manifestPath);
        if (manifestError != 0)
        {
            reason = $"SteamVR would not load the action manifest (error {manifestError})";
            return null;
        }

        if (!input.GetHandles())
        {
            reason = "SteamVR did not know the actions in the manifest";
            return null;
        }

        reason = "";
        return input;
    }

    /// <summary>
    /// Reads the state of the action set once. Every <see cref="Read"/> until the next call sees
    /// the same state.
    /// </summary>
    public void Update()
    {
        // Priority 0 and no restriction: see the remarks on why that leaves VRChat its input.
        var set = new ActiveActionSet { ActionSet = _actionSet };
        _ = ((delegate* unmanaged[Stdcall]<ActiveActionSet*, uint, uint, int>)Slot(UpdateActionState))(
            &set, (uint)sizeof(ActiveActionSet), 1);
    }

    /// <summary>One hand's actions as of the last <see cref="Update"/>.</summary>
    public ActionHand Read(Interaction.Hand side)
    {
        var source = side == Interaction.Hand.Left ? _left : _right;

        var (grabActive, grab) = AnalogAction(_grab, source);
        var (gripActive, grip) = DigitalAction(_grip, source);
        var (clickActive, click) = DigitalAction(_click, source);
        var (scrollActive, scroll) = AnalogAction(_scroll, source);
        var (aimActive, aim) = PoseAction(_aim, source);
        var (handActive, device) = PoseAction(_hand, source);

        return new ActionHand(
            grabActive || gripActive || clickActive || scrollActive || aimActive || handActive,
            grab.X,
            grip,
            click,
            scroll,
            device,
            aim);
    }

    private int SetManifest(string path)
    {
        var bytes = Utf8Z(path);
        fixed (byte* p = bytes)
            return ((delegate* unmanaged[Stdcall]<byte*, int>)Slot(SetActionManifestPath))(p);
    }

    private bool GetHandles()
    {
        return Handle(GetActionSetHandle, ActionSetName, out _actionSet)
            && Handle(GetActionHandle, GrabName, out _grab)
            && Handle(GetActionHandle, GripName, out _grip)
            && Handle(GetActionHandle, ClickName, out _click)
            && Handle(GetActionHandle, ScrollName, out _scroll)
            && Handle(GetActionHandle, AimName, out _aim)
            && Handle(GetActionHandle, HandName, out _hand)
            && Handle(GetInputSourceHandle, "/user/hand/left", out _left)
            && Handle(GetInputSourceHandle, "/user/hand/right", out _right);
    }

    private bool Handle(int slot, string name, out ulong handle)
    {
        var bytes = Utf8Z(name);
        ulong value = 0;
        int error;
        fixed (byte* p = bytes)
            error = ((delegate* unmanaged[Stdcall]<byte*, ulong*, int>)Slot(slot))(p, &value);

        handle = value;
        return error == 0 && value != 0;
    }

    private (bool Active, Vector2 Value) AnalogAction(ulong action, ulong source)
    {
        byte* data = stackalloc byte[InputActionLayouts.AnalogSize];
        var error = ((delegate* unmanaged[Stdcall]<ulong, byte*, uint, ulong, int>)Slot(GetAnalogActionData))(
            action, data, InputActionLayouts.AnalogSize, source);

        return error != 0
            ? (false, Vector2.Zero)
            : InputActionLayouts.ReadAnalog(new ReadOnlySpan<byte>(data, InputActionLayouts.AnalogSize));
    }

    private (bool Active, bool State) DigitalAction(ulong action, ulong source)
    {
        byte* data = stackalloc byte[InputActionLayouts.DigitalSize];
        var error = ((delegate* unmanaged[Stdcall]<ulong, byte*, uint, ulong, int>)Slot(GetDigitalActionData))(
            action, data, InputActionLayouts.DigitalSize, source);

        return error != 0
            ? (false, false)
            : InputActionLayouts.ReadDigital(new ReadOnlySpan<byte>(data, InputActionLayouts.DigitalSize));
    }

    private (bool Active, Pose? Pose) PoseAction(ulong action, ulong source)
    {
        byte* data = stackalloc byte[InputActionLayouts.PoseSize];
        var error = ((delegate* unmanaged[Stdcall]<ulong, int, float, byte*, uint, ulong, int>)Slot(GetPoseActionDataRelativeToNow))(
            action, OpenVrInterop.TrackingUniverseStanding, 0f, data, InputActionLayouts.PoseSize, source);

        return error != 0
            ? (false, null)
            : InputActionLayouts.ReadPose(new ReadOnlySpan<byte>(data, InputActionLayouts.PoseSize));
    }

    private static byte[] Utf8Z(string text) => Encoding.UTF8.GetBytes(text + "\0");

    private void* Slot(int index) => *((void**)_table + index);
}
