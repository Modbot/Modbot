using Modbot.Overlay.OpenVr;
using Serilog;
using SharpGen.Runtime;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

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
/// duplication it opens belongs to that card. This device is made on the card SteamVR draws on
/// where SteamVR can say which (and on no other), else on the strongest real card (<see cref="GraphicsCardChoice"/>),
/// and once made it does not move.</para>
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
                var (device, context) = Make(OpenVrSession.Shared.GraphicsCard());

                _device = device;
                _context = context;
                _users = 0;
            }

            _users++;
            return (_device!, _context!);
        }
    }

    /// <summary>How long to wait before the next card after one said it was out of memory.</summary>
    private static readonly TimeSpan OutOfMemoryWait = TimeSpan.FromMilliseconds(500);

    /// <summary><c>E_OUTOFMEMORY</c>, the answer that came and went between starts on one PC.</summary>
    private const int OutOfMemory = unchecked((int)0x8007000E);

    /// <summary>
    /// The card the device was last made on, so the log names it once per change rather than every
    /// time a headset comes and goes.
    /// </summary>
    private static string? _lastCard;

    /// <summary>
    /// Makes the device on the best card that will take it, in <see cref="GraphicsCardChoice"/>'s
    /// order. Throws the last card's answer when none will.
    /// </summary>
    /// <param name="steamVrCard">The id of the card SteamVR draws on, or null when it cannot say.</param>
    private static (ID3D11Device Device, ID3D11DeviceContext Context) Make(long? steamVrCard)
    {
        using var factory = ListCards(out var cards);
        var order = GraphicsCardChoice.Order(cards, steamVrCard);

        var waited = false;
        var last = Result.Ok;

        foreach (var card in order)
        {
            IDXGIAdapter1? adapter = null;
            try
            {
                if (card is not null && (factory is null || factory.EnumAdapters1(card.Index, out adapter).Failure))
                    continue;

                // BgraSupport is required for the BGRA format Avalonia hands over; feature level
                // 11_0 is the floor SteamVR itself requires, so anything that can run VRChat can
                // run this. A card named outright is asked for as Unknown, which is what Direct3D
                // requires when it is handed one.
                last = D3D11.D3D11CreateDevice(
                    adapter,
                    adapter is null ? DriverType.Hardware : DriverType.Unknown,
                    DeviceCreationFlags.BgraSupport,
                    [FeatureLevel.Level_11_1, FeatureLevel.Level_11_0],
                    out var device,
                    out _,
                    out var context);

                if (last.Success && device is not null && context is not null)
                {
                    var name = card?.Name ?? "the card Windows chose";
                    if (name != _lastCard)
                    {
                        _lastCard = name;
                        Log.Information("The headset overlay draws on {Card}", name);
                    }

                    return (device, context);
                }

                context?.Dispose();
                device?.Dispose();
            }
            finally
            {
                adapter?.Dispose();
            }

            var outOfMemory = last.Code == OutOfMemory;
            Log.Warning(
                "The headset overlay could not make its graphics device on {Card}: {Answer}",
                card?.Name ?? "the card Windows chose",
                outOfMemory ? "out of memory" : last.ToString());

            if (GraphicsCardChoice.WaitBeforeNext(outOfMemory, waited))
            {
                waited = true;
                Thread.Sleep(OutOfMemoryWait);
            }
        }

        // SteamVR named the card, so no other was tried: a texture on another card is one the
        // headset does not show, and the failure is said here rather than hidden behind one.
        if (order is [{ } only, ..] && order.All(card => card == only))
        {
            Log.Warning(
                "The headset overlay could not make its graphics device on {Card}, the card SteamVR draws on; no other card was tried, because SteamVR decides the card",
                only.Name);
        }

        last.CheckError();
        throw new InvalidOperationException("No graphics card would make the headset overlay's device.");
    }

    /// <summary>
    /// Every card Windows lists, and the factory that listed them (to ask for one again by its
    /// place). Null and an empty list when Windows would not list them; then only Windows' own
    /// choice is tried, as before.
    /// </summary>
    private static IDXGIFactory1? ListCards(out List<GraphicsCard> cards)
    {
        cards = [];

        IDXGIFactory1? factory = null;
        try
        {
            factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();

            for (uint index = 0; factory.EnumAdapters1(index, out var adapter).Success && adapter is not null; index++)
            {
                using (adapter)
                {
                    var told = adapter.Description1;
                    cards.Add(new GraphicsCard(
                        index,
                        told.Description,
                        told.DedicatedVideoMemory,
                        told.Luid,
                        Software: (told.Flags & AdapterFlags.Software) != 0,
                        Remote: (told.Flags & AdapterFlags.Remote) != 0));
                }
            }

            return factory;
        }
        catch (SharpGenException ex)
        {
            Log.Warning(ex, "Windows would not list this PC's graphics cards; the headset overlay lets Windows choose");
            factory?.Dispose();
            cards.Clear();
            return null;
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
