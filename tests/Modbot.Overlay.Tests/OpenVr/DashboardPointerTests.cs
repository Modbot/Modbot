using System.Runtime.InteropServices;
using Modbot.Overlay.OpenVr;

namespace Modbot.Overlay.Tests.OpenVr;

/// <summary>
/// SteamVR's mouse events on the dashboard tab, read the way the header lays them out: the
/// bottom left of the texture at 0,0, which the page turns the right way up.
/// </summary>
public class DashboardPointerTests
{
    private const int Height = 1100;

    [Fact]
    public void APressIsReadFromTheTopTheWayThePageIsLaidOut()
    {
        var pressed = VrEvent.MouseEvent(301, 400, 1000, 1);

        var read = OpenVrDashboardRuntime.Read(pressed, Height);

        Assert.Equal(new DashboardPointer(DashboardPointerKind.Down, 400, 100), read);
    }

    [Fact]
    public void HowLongSteamVrHeldAPressIsKept()
    {
        var pressed = VrEvent.MouseEvent(301, 400, 1000, 1);
        pressed.EventAgeSeconds = 0.25f;

        Assert.Equal(TimeSpan.FromMilliseconds(250), OpenVrDashboardRuntime.Read(pressed, Height)?.Age);

        pressed.EventAgeSeconds = float.NaN;
        Assert.Equal(TimeSpan.Zero, OpenVrDashboardRuntime.Read(pressed, Height)?.Age);
    }

    [Fact]
    public void MovesAndReleasesAreReadToo()
    {
        Assert.Equal(DashboardPointerKind.Move, OpenVrDashboardRuntime.Read(VrEvent.MouseEvent(300, 1, 2, 0), Height)?.Kind);
        Assert.Equal(DashboardPointerKind.Up, OpenVrDashboardRuntime.Read(VrEvent.MouseEvent(302, 1, 2, 1), Height)?.Kind);
    }

    [Fact]
    public void OnlyTheTriggerPresses()
    {
        // VRMouseButton_Right: not a press on anything on the page.
        Assert.Null(OpenVrDashboardRuntime.Read(VrEvent.MouseEvent(301, 1, 2, 2), Height));
    }

    [Fact]
    public void TheTabGoingOutOfSightLetsGoOfWhateverWasHeld()
    {
        Assert.Equal(DashboardPointerKind.Gone, OpenVrDashboardRuntime.Read(new VrEvent { EventType = 501 }, Height)?.Kind);
    }

    [Fact]
    public void AnythingElseIsNotThePointer()
    {
        Assert.Null(OpenVrDashboardRuntime.Read(new VrEvent { EventType = 1202 }, Height));
    }

    [Fact]
    public void TheMouseIsWhereTheHeaderPutsIt()
    {
        // VREvent_t's data union starts after 16 bytes on Windows, where the header packs to 8,
        // and after 12 elsewhere. The mouse's x is the first thing in it.
        var made = VrEvent.MouseEvent(300, 12.5f, 0, 0);
        var bytes = new byte[Marshal.SizeOf<VrEvent>()];
        var handle = GCHandle.Alloc(bytes, GCHandleType.Pinned);
        try
        {
            Marshal.StructureToPtr(made, handle.AddrOfPinnedObject(), false);
        }
        finally
        {
            handle.Free();
        }

        var offset = OperatingSystem.IsWindows() ? 16 : 12;
        Assert.Equal(12.5f, BitConverter.ToSingle(bytes, offset));
        Assert.Equal(64, bytes.Length);
    }

    [Fact]
    public void TheTabsKeyIsStableAndNotThePanelsKey()
    {
        // SteamVR remembers the tab by this string, and two overlays cannot share one.
        Assert.Equal("moe.bin.modbot.dashboard", OpenVrDashboardRuntime.DashboardKey);
        Assert.NotEqual(OpenVrOverlayRuntime.OverlayKey, OpenVrDashboardRuntime.DashboardKey);
        Assert.NotEqual(OpenVrOverlayRuntime.NotificationOverlayKey, OpenVrDashboardRuntime.DashboardKey);
    }
}
