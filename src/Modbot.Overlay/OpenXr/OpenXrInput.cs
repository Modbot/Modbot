using System.Numerics;
using Modbot.Overlay.Interaction;
using Serilog;
using Silk.NET.OpenXR;
using XrAction = Silk.NET.OpenXR.Action;

namespace Modbot.Overlay.OpenXr;

/// <summary>
/// The controllers, through OpenXR actions: the <c>modbot</c> action set, its four actions, the
/// two aim spaces, and one read a frame.
/// </summary>
/// <remarks>
/// <para><strong>What this reads.</strong> Where each hand points and whether its grip, trigger
/// and thumbstick are pressed, and where the head is, all in the session's <c>LOCAL</c> space at
/// the frame's predicted display time. It reads nothing about any other program, and nothing is
/// reported anywhere: the poses decide where the panel's cursor is and whether it is being held,
/// and are dropped.</para>
/// <para><strong>Order of work.</strong> The set and the actions are made against the instance,
/// bindings are suggested for each controller in <see cref="ControllerBindings.Profiles"/>, the
/// aim spaces are made, and the set is attached to the session, all before the session is begun.
/// A controller the runtime does not know is skipped with a log line; a runtime that refuses the
/// whole thing leaves the overlay showing, just not holdable.</para>
/// <para><strong>Each frame</strong>, <c>xrSyncActions</c> brings the states up to date and the
/// spaces are located. A hand counts as tracked when its aim space has a valid position and a
/// valid orientation; a controller that is off, or an action the runtime has not given this
/// session, has neither and is reported missing.</para>
/// </remarks>
internal sealed unsafe class OpenXrInput : IDisposable
{
    private readonly XR _xr;
    private readonly Instance _instance;
    private readonly Session _session;
    private readonly string _runtimeName;
    private readonly ILogger _log;

    private ActionSet _set;
    private XrAction _aim;
    private XrAction _device;
    private XrAction _grab;
    private XrAction _click;
    private XrAction _scroll;
    private readonly ulong[] _hands = new ulong[2];
    private readonly Space[] _aimSpaces = new Space[2];
    private readonly Space[] _deviceSpaces = new Space[2];
    private bool _unfocusedLogged;

    private OpenXrInput(XR xr, Instance instance, Session session, string runtimeName, ILogger log)
    {
        _xr = xr;
        _instance = instance;
        _session = session;
        _runtimeName = runtimeName;
        _log = log;
    }

    /// <summary>Makes and attaches everything. Throws <see cref="OverlayStartFailure"/> when the runtime refuses a step.</summary>
    public static OpenXrInput Create(XR xr, Instance instance, Session session, string runtimeName, ILogger log)
    {
        var input = new OpenXrInput(xr, instance, session, runtimeName, log);
        try
        {
            input.Build();
            return input;
        }
        catch
        {
            input.Dispose();
            throw;
        }
    }

    /// <summary>The hand's aim action space: where it points.</summary>
    public Space AimSpace(Hand hand) => _aimSpaces[hand == Hand.Left ? 0 : 1];

    /// <summary>
    /// The hand's own action space, for a panel worn on that hand. Not the aim space: a panel hung
    /// off where the controller points would swing with the ray rather than sit on the wrist.
    /// </summary>
    public Space DeviceSpace(Hand hand) => _deviceSpaces[hand == Hand.Left ? 0 : 1];

    private void Build()
    {
        var setInfo = new ActionSetCreateInfo { Type = StructureType.ActionSetCreateInfo, Priority = 0 };
        OpenXrCalls.WriteFixedString(setInfo.ActionSetName, (int)XR.MaxActionSetNameSize, ControllerBindings.ActionSet);
        OpenXrCalls.WriteFixedString(setInfo.LocalizedActionSetName, (int)XR.MaxLocalizedActionSetNameSize, "Modbot");

        ActionSet set;
        Check(_xr.CreateActionSet(_instance, &setInfo, &set), "xrCreateActionSet");
        _set = set;
        _log.Debug("Action set {ActionSet} created", ControllerBindings.ActionSet);

        _hands[0] = PathOf(ControllerBindings.LeftHand);
        _hands[1] = PathOf(ControllerBindings.RightHand);

        _aim = CreateAction(ControllerBindings.Aim, "Aim", ActionType.PoseInput);
        _device = CreateAction(ControllerBindings.Device, "Controller", ActionType.PoseInput);
        _grab = CreateAction(ControllerBindings.Grab, "Grab", ActionType.BooleanInput);
        _click = CreateAction(ControllerBindings.Click, "Click", ActionType.BooleanInput);
        _scroll = CreateAction(ControllerBindings.Scroll, "Scroll", ActionType.Vector2fInput);
        _log.Debug("Actions {Actions} created for both hands", ControllerBindings.Actions);

        foreach (var profile in ControllerBindings.Profiles)
            Suggest(profile);

        for (var i = 0; i < 2; i++)
        {
            _aimSpaces[i] = MakeSpace(_aim, _hands[i]);
            _deviceSpaces[i] = MakeSpace(_device, _hands[i]);
        }

        _log.Debug("Aim and controller spaces created for the left and right hands");

        fixed (ActionSet* sets = &_set)
        {
            var attach = new SessionActionSetsAttachInfo
            {
                Type = StructureType.SessionActionSetsAttachInfo,
                CountActionSets = 1,
                ActionSets = sets,
            };
            Check(_xr.AttachSessionActionSets(_session, &attach), "xrAttachSessionActionSets");
        }

        _log.Debug("Action set {ActionSet} attached to the overlay session", ControllerBindings.ActionSet);
    }

    private Space MakeSpace(XrAction action, ulong hand)
    {
        var info = new ActionSpaceCreateInfo
        {
            Type = StructureType.ActionSpaceCreateInfo,
            Action = action,
            SubactionPath = hand,
            PoseInActionSpace = OpenXrCalls.IdentityPose,
        };

        Space space;
        Check(_xr.CreateActionSpace(_session, &info, &space), "xrCreateActionSpace");
        return space;
    }

    private XrAction CreateAction(string name, string localizedName, ActionType type)
    {
        fixed (ulong* hands = _hands)
        {
            var info = new ActionCreateInfo
            {
                Type = StructureType.ActionCreateInfo,
                ActionType = type,
                CountSubactionPaths = 2,
                SubactionPaths = hands,
            };
            OpenXrCalls.WriteFixedString(info.ActionName, (int)XR.MaxActionNameSize, name);
            OpenXrCalls.WriteFixedString(info.LocalizedActionName, (int)XR.MaxLocalizedActionNameSize, localizedName);

            XrAction action;
            Check(_xr.CreateAction(_set, &info, &action), $"xrCreateAction({name})");
            return action;
        }
    }

    /// <summary>
    /// Offers one controller's bindings. A controller this runtime does not know is a log line and
    /// nothing more: the others are still offered.
    /// </summary>
    private void Suggest(ControllerProfile profile)
    {
        ulong profilePath;
        var result = _xr.StringToPath(_instance, profile.Path, &profilePath);
        if (result < 0)
        {
            _log.Debug("{Runtime} does not know the {Controller} profile ({Result})", _runtimeName, profile.Name, result);
            return;
        }

        var bindings = new List<ActionSuggestedBinding>();
        foreach (var hand in new[] { Hand.Left, Hand.Right })
        {
            foreach (var (action, path) in profile.BindingsFor(hand))
            {
                ulong binding;
                result = _xr.StringToPath(_instance, path, &binding);
                if (result < 0)
                {
                    _log.Debug("{Runtime} does not know {Path} ({Result})", _runtimeName, path, result);
                    continue;
                }

                bindings.Add(new ActionSuggestedBinding { Action = ActionNamed(action), Binding = binding });
            }
        }

        var array = bindings.ToArray();
        if (array.Length == 0)
        {
            _log.Debug("{Runtime} knows none of the {Controller} inputs; nothing suggested", _runtimeName, profile.Name);
            return;
        }

        fixed (ActionSuggestedBinding* suggested = array)
        {
            var info = new InteractionProfileSuggestedBinding
            {
                Type = StructureType.InteractionProfileSuggestedBinding,
                InteractionProfile = profilePath,
                CountSuggestedBindings = (uint)array.Length,
                SuggestedBindings = suggested,
            };

            result = _xr.SuggestInteractionProfileBinding(_instance, &info);
        }

        if (result < 0)
            _log.Warning("{Runtime} refused the {Controller} bindings ({Result}); that controller cannot hold the panel", _runtimeName, profile.Name, result);
        else
            _log.Debug("{Count} bindings suggested for the {Controller}", array.Length, profile.Name);
    }

    private XrAction ActionNamed(string name) => name switch
    {
        ControllerBindings.Aim => _aim,
        ControllerBindings.Device => _device,
        ControllerBindings.Grab => _grab,
        ControllerBindings.Click => _click,
        ControllerBindings.Scroll => _scroll,
        _ => throw new ArgumentOutOfRangeException(nameof(name), name, "Not one of the panel's actions."),
    };

    /// <summary>
    /// One frame's look at the controllers: syncs the actions, then locates the head and each
    /// hand in <paramref name="localSpace"/> at <paramref name="time"/> and reads the buttons.
    /// Throws <see cref="OverlayStartFailure"/> when the runtime answers a call with a failure.
    /// </summary>
    public OverlayTracking Read(Space viewSpace, Space localSpace, long time)
    {
        var active = new ActiveActionSet { ActionSet = _set, SubactionPath = 0 };
        var sync = new ActionsSyncInfo
        {
            Type = StructureType.ActionsSyncInfo,
            CountActiveActionSets = 1,
            ActiveActionSets = &active,
        };

        var result = _xr.SyncAction(_session, &sync);
        Check(result, "xrSyncActions");

        // Not focused is an answer, not a failure: the runtime is keeping the controllers for
        // another session, and every action reads as inactive until it gives them back.
        if (result == Result.SessionNotFocused && !_unfocusedLogged)
        {
            _unfocusedLogged = true;
            _log.Debug("{Runtime} is not giving the overlay session the controllers yet (session not focused)", _runtimeName);
        }
        else if (result == Result.Success && _unfocusedLogged)
        {
            _unfocusedLogged = false;
            _log.Debug("{Runtime} is giving the overlay session the controllers", _runtimeName);
        }

        var head = Locate(viewSpace, localSpace, time) ?? Pose.Identity;
        return new OverlayTracking(head, ReadHand(0, localSpace, time), ReadHand(1, localSpace, time));
    }

    private HandState ReadHand(int hand, Space localSpace, long time)
    {
        if (Locate(_aimSpaces[hand], localSpace, time) is not { } aim)
            return HandState.Missing;

        // A runtime that gives an aim pose but no controller pose is not one anybody has seen, but
        // the aim pose is a better answer than nothing: the panel sits a little forward of the
        // wrist rather than vanishing.
        var device = Locate(_deviceSpaces[hand], localSpace, time) ?? aim;

        return new HandState(
            true,
            aim,
            device,
            ReadBoolean(_grab, _hands[hand]),
            ReadBoolean(_click, _hands[hand]),
            ReadVector2(_scroll, _hands[hand]));
    }

    /// <summary>The space's pose in the base space, or null when its position or orientation is not valid.</summary>
    private Pose? Locate(Space space, Space baseSpace, long time)
    {
        var location = new SpaceLocation { Type = StructureType.SpaceLocation };
        Check(_xr.LocateSpace(space, baseSpace, time, &location), "xrLocateSpace");

        const SpaceLocationFlags valid = SpaceLocationFlags.OrientationValidBit | SpaceLocationFlags.PositionValidBit;
        return (location.LocationFlags & valid) == valid ? OpenXrCalls.ToPose(location.Pose) : null;
    }

    private bool ReadBoolean(XrAction action, ulong hand)
    {
        var info = new ActionStateGetInfo { Type = StructureType.ActionStateGetInfo, Action = action, SubactionPath = hand };
        var state = new ActionStateBoolean { Type = StructureType.ActionStateBoolean };
        Check(_xr.GetActionStateBoolean(_session, &info, &state), "xrGetActionStateBoolean");
        return state.IsActive != 0 && state.CurrentState != 0;
    }

    private Vector2 ReadVector2(XrAction action, ulong hand)
    {
        var info = new ActionStateGetInfo { Type = StructureType.ActionStateGetInfo, Action = action, SubactionPath = hand };
        var state = new ActionStateVector2f { Type = StructureType.ActionStateVector2f };
        Check(_xr.GetActionStateVector2(_session, &info, &state), "xrGetActionStateVector2f");
        return state.IsActive != 0 ? new Vector2(state.CurrentState.X, state.CurrentState.Y) : Vector2.Zero;
    }

    /// <summary>Says which controller profile the runtime has settled on for each hand, when it tells us it changed.</summary>
    public void LogCurrentProfiles()
    {
        for (var i = 0; i < 2; i++)
        {
            var state = new InteractionProfileState { Type = StructureType.InteractionProfileState };
            var result = _xr.GetCurrentInteractionProfile(_session, _hands[i], &state);
            if (result < 0)
            {
                _log.Debug("{Runtime} answered {Result} to xrGetCurrentInteractionProfile", _runtimeName, result);
                continue;
            }

            var hand = i == 0 ? "left" : "right";
            _log.Debug("The {Hand} hand's controller is now {Profile}", hand, state.InteractionProfile == 0 ? "nothing" : PathText(state.InteractionProfile));
        }
    }

    private ulong PathOf(string path)
    {
        ulong result;
        Check(_xr.StringToPath(_instance, path, &result), $"xrStringToPath({path})");
        return result;
    }

    private string PathText(ulong path)
    {
        var buffer = new byte[XR.MaxPathLength];
        uint written = 0;
        fixed (byte* p = buffer)
        {
            if (_xr.PathToString(_instance, path, (uint)buffer.Length, &written, p) < 0)
                return $"path {path}";

            return OpenXrCalls.FixedString(p);
        }
    }

    private void Check(Result result, string call) => OpenXrCalls.Check(result, _runtimeName, call);

    public void Dispose()
    {
        for (var i = 0; i < 2; i++)
        {
            if (_aimSpaces[i].Handle != 0)
                _xr.DestroySpace(_aimSpaces[i]);
            _aimSpaces[i] = default;

            if (_deviceSpaces[i].Handle != 0)
                _xr.DestroySpace(_deviceSpaces[i]);
            _deviceSpaces[i] = default;
        }

        // Destroying the set destroys its actions with it.
        if (_set.Handle != 0)
            _xr.DestroyActionSet(_set);
        _set = default;
        _aim = _device = _grab = _click = _scroll = default;
    }
}
