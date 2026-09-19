using Modbot.Overlay.Interaction;

namespace Modbot.Overlay.OpenVr;

/// <summary>
/// The one attachment to SteamVR that every Modbot overlay draws through.
/// </summary>
/// <remarks>
/// <para><strong>Why this is shared and the overlays are not.</strong> <c>VR_InitInternal</c> and
/// <c>VR_ShutdownInternal</c> are process-wide: a second init lands on top of the first, and the
/// first shutdown pulls the function table out from under everybody. But a process may own as
/// many <em>overlays</em> as it likes, each with its own handle. So the attachment — the knock on
/// the door, the init, the <c>IVROverlay</c> function table and the <c>IVRSystem</c> used for
/// poses — lives here and is counted in and out, and each panel owns only its own handle (two
/// overlay modes design §4.1).</para>
/// <para><strong>It never starts SteamVR.</strong> The first question is asked as a background
/// application, which a SteamVR that is not running simply refuses; only then is the overlay init
/// made. That is what lets the companion start with the computer without dragging SteamVR up with
/// it.</para>
/// <para><strong>This talks to SteamVR on the same machine and to nothing else.</strong> No
/// socket is opened and no file is read; what crosses it is a texture pointer, a position and the
/// poses of the headset and controllers, none of which is sent anywhere.</para>
/// </remarks>
public sealed class OpenVrSession
{
    private readonly Lock _gate = new();

    private nint _table;
    private OpenVrSystem? _system;
    private int _users;
    private int _generation;

    /// <summary>The session the client's overlays use. One process, one attachment.</summary>
    public static OpenVrSession Shared { get; } = new();

    /// <summary>
    /// Whether SteamVR is installed on this PC at all, asked without attaching to it.
    /// </summary>
    /// <remarks>
    /// The client itself does not need this -- <see cref="Open"/> answers with a state either way.
    /// It is here for the tests, which say what happens on a machine that has no SteamVR and
    /// cannot say it on a machine that has one.
    /// </remarks>
    public static bool IsSteamVrInstalled
    {
        get
        {
            try
            {
                return OpenVrInterop.IsRuntimeInstalled();
            }
            catch (DllNotFoundException)
            {
                return false;
            }
        }
    }

    /// <summary>Whether SteamVR is attached right now.</summary>
    public bool IsOpen
    {
        get
        {
            lock (_gate)
                return _table != 0;
        }
    }

    /// <summary>
    /// Which attachment this is. It moves every time SteamVR is let go, which is how an overlay
    /// knows its handle belongs to an attachment that no longer exists.
    /// </summary>
    public int Generation
    {
        get
        {
            lock (_gate)
                return _generation;
        }
    }

    /// <summary>How many overlays are holding this attachment open.</summary>
    public int Users
    {
        get
        {
            lock (_gate)
                return _users;
        }
    }

    /// <summary>
    /// Attaches to SteamVR, or joins the attachment that is already there, and counts one more
    /// user against it. Only a <see cref="OverlayRuntimeState.Running"/> answer counts a user; on
    /// anything else nothing is held and the caller may ask again later.
    /// </summary>
    public OverlayRuntimeStatus Open()
    {
        lock (_gate)
        {
            if (_table != 0)
            {
                _users++;
                return new(OverlayRuntimeState.Running, Detail: "Attached to SteamVR.");
            }

            try
            {
                if (!OpenVrInterop.IsRuntimeInstalled())
                    return new(OverlayRuntimeState.NoRuntime, Detail: "SteamVR is not installed on this PC.");

                // The knock on the door. A background application is refused unless SteamVR is
                // already running, and refusing is all it does: nothing is launched.
                OpenVrInterop.InitInternal(out var probeError, OpenVrInterop.ApplicationTypeBackground);
                if (probeError != VrInitError.None)
                {
                    var state = probeError is VrInitError.Init_NoServerForBackgroundApp
                        or VrInitError.Init_HmdNotFound
                        or VrInitError.Init_PathRegistryNotFound
                        or VrInitError.Init_NotInitialized
                        ? OverlayRuntimeState.NotStarted
                        : OverlayRuntimeState.Refused;

                    return new(
                        state,
                        probeError,
                        state is OverlayRuntimeState.NotStarted ? "SteamVR is not running." : Refusal(probeError));
                }

                OpenVrInterop.ShutdownInternal();

                OpenVrInterop.InitInternal(out var initError, OpenVrInterop.ApplicationTypeOverlay);
                if (initError != VrInitError.None)
                    return new(OverlayRuntimeState.Refused, initError, Refusal(initError));

                var table = OpenVrInterop.GetGenericInterface(OpenVrInterop.OverlayInterfaceVersion, out var interfaceError);
                if (table == 0 || interfaceError != VrInitError.None)
                {
                    // A SteamVR whose IVROverlay is a version this build was not written against.
                    // Refusing is the only safe answer: the function table is positional, so
                    // guessing would call the wrong function rather than fail.
                    OpenVrInterop.ShutdownInternal();
                    return new(
                        OverlayRuntimeState.Refused,
                        interfaceError,
                        $"This build speaks {OpenVrInterop.OverlayInterfaceVersion}; SteamVR does not.");
                }

                _table = table;

                // The head and the controllers, for the cursor and for holding the panel. A
                // SteamVR without this interface version still shows the panels; they just cannot
                // be held.
                _system = OpenVrSystem.Open();
                _users = 1;

                return new(OverlayRuntimeState.Running, Detail: "Attached to SteamVR.");
            }
            catch (DllNotFoundException)
            {
                // openvr_api is shipped beside the overlay; its absence means a broken install,
                // not a missing headset, and saying so saves somebody a long wrong search.
                return new(
                    OverlayRuntimeState.NoRuntime,
                    Detail: "The OpenVR library is missing from the Modbot installation.");
            }
        }
    }

    /// <summary>One overlay has let go. SteamVR is let go when the last one does.</summary>
    public void Release()
    {
        lock (_gate)
        {
            if (_users > 0)
                _users--;

            if (_users == 0)
                LetGo();
        }
    }

    /// <summary>
    /// Everybody lets go at once: SteamVR said it is closing, so the function table is about to
    /// stop being a function table. <see cref="Generation"/> moves, which is how the other
    /// overlays learn their handles are gone.
    /// </summary>
    public void Close()
    {
        lock (_gate)
        {
            _users = 0;
            LetGo();
        }
    }

    /// <summary>One slot of <c>IVROverlay</c>'s function table. Throws when nothing is attached.</summary>
    internal nint Slot(int index)
    {
        lock (_gate)
        {
            if (_table == 0)
                throw new InvalidOperationException("SteamVR is not attached.");

            unsafe
            {
                return ((nint*)_table)[index];
            }
        }
    }

    /// <summary>Where the head and the controllers are now, and what is pressed.</summary>
    public OverlayTracking ReadTracking()
    {
        OpenVrSystem? system;
        lock (_gate)
            system = _table == 0 ? null : _system;

        return system?.Read() ?? OverlayTracking.None;
    }

    /// <summary>SteamVR's index for a hand's controller, or null when it has none right now.</summary>
    public uint? DeviceIndex(Hand hand)
    {
        OpenVrSystem? system;
        lock (_gate)
            system = _table == 0 ? null : _system;

        return system?.DeviceIndex(hand);
    }

    /// <summary>
    /// A refusal in words. Most are SteamVR's own and are named as they come; the one that is not
    /// SteamVR is xrizer, the OpenVR-on-OpenXR layer used with WiVRn and Monado on Linux, which
    /// runs games only and answers an overlay with InvalidApplicationType.
    /// </summary>
    internal static string Refusal(VrInitError error) => error switch
    {
        VrInitError.Init_InvalidApplicationType =>
            "This VR runtime runs games only and does not take overlay applications (xrizer answers this; SteamVR does not).",
        _ => $"SteamVR answered {error}.",
    };

    private void LetGo()
    {
        _system = null;

        if (_table != 0)
        {
            OpenVrInterop.ShutdownInternal();
            _table = 0;
        }

        _generation++;
    }
}
