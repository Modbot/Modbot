using Vortice.Direct3D;
using Vortice.Direct3D11;

namespace Modbot.Overlay.Rendering;

/// <summary>
/// One Direct3D 11 device behind every overlay texture this process draws into.
/// </summary>
/// <remarks>
/// <para><strong>Why one, and not one each.</strong> Modbot draws two panels in the headset, the
/// main one and the notification one, and each used to make a device of its own. A device is not
/// free: the graphics driver keeps its own state, its own allocations and its own worker threads
/// behind each one. Both panels do the same small thing with it — copy a frame into a texture and
/// flush — on the same thread, a few times a minute, so one device carries both without either
/// waiting on the other, and the second device bought nothing at all.</para>
/// <para><strong>Counted in and out, like the attachment to SteamVR.</strong> Either panel can be
/// switched off, and either can be the last to let go, so neither can own the device. It is made
/// on the first ask and let go when the last texture is disposed — which is what gives the memory
/// back when the moderator takes the headset off, rather than holding it until they quit.</para>
/// <para><strong>What it is deliberately not shared with.</strong> Clip recording makes a device
/// of its own, and must: it picks the graphics card that drives the screen VRChat is on, which on
/// a laptop with two cards is routinely not the card Windows lists first, and the screen
/// duplication it opens belongs to that card. This device takes whichever card Windows lists
/// first and is not free to move.</para>
/// </remarks>
public static class SharedD3D11Device
{
    private static readonly Lock Gate = new();

    private static ID3D11Device? _device;
    private static ID3D11DeviceContext? _context;
    private static int _users;

    /// <summary>
    /// How many textures are holding the device open. Zero means there is no device. Public for
    /// the same reason <see cref="Modbot.Overlay.OpenVr.OpenVrSession.Users"/> is: counting in and
    /// out is the whole of the rule, and a rule nothing can check is a rule that quietly breaks.
    /// </summary>
    public static int Users
    {
        get
        {
            lock (Gate)
                return _users;
        }
    }

    /// <summary>Makes the device, or joins the one already made, and counts one more user against it.</summary>
    internal static (ID3D11Device Device, ID3D11DeviceContext Context) Open()
    {
        lock (Gate)
        {
            if (_device is null)
            {
                // BgraSupport is required for the BGRA format Avalonia hands over; feature level
                // 11_0 is the floor SteamVR itself requires, so anything that can run VRChat can
                // run this.
                D3D11.D3D11CreateDevice(
                    adapter: null,
                    DriverType.Hardware,
                    DeviceCreationFlags.BgraSupport,
                    [FeatureLevel.Level_11_1, FeatureLevel.Level_11_0],
                    out var device,
                    out _,
                    out var context).CheckError();

                _device = device;
                _context = context;
                _users = 0;
            }

            _users++;
            return (_device!, _context!);
        }
    }

    /// <summary>One texture has let go. The device goes when the last one does.</summary>
    internal static void Release()
    {
        lock (Gate)
        {
            if (_users > 0)
                _users--;

            if (_users > 0 || _device is null)
                return;

            _context?.Dispose();
            _device.Dispose();
            _context = null;
            _device = null;
        }
    }
}
