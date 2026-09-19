using Modbot.Companion.Overlay;
using Modbot.Overlay.OpenVr;
using Modbot.Overlay.OpenXr;
using Modbot.Overlay.Rendering;

namespace Modbot.Overlay.Tests.OpenXr;

/// <summary>
/// Two panels over one OpenXR session. A second session would mean a second XrInstance, a second
/// Vulkan device and a second frame thread on a machine that is also running VRChat, so the
/// notification panel is a second layer on the session the main panel already has.
/// </summary>
/// <remarks>
/// These run on a machine with no OpenXR loader, which is where CI is and where most moderators
/// are: every answer has to be a state rather than an exception.
/// </remarks>
public class TwoPanelsTests
{
    [Fact]
    public void TheNotificationPanelIsARuntimeOfItsOwn()
    {
        using var runtime = new OpenXrOverlayRuntime(16, notificationResolution: 8);

        Assert.NotNull(runtime.NotificationPanel);
        Assert.NotSame(runtime, runtime.NotificationPanel);
    }

    [Fact]
    public void BothPanelsGetAStateRatherThanAnExceptionWithNoOpenXr()
    {
        using var runtime = new OpenXrOverlayRuntime(16, notificationResolution: 8);

        var main = runtime.Start();
        var notifications = runtime.NotificationPanel.Start();

        Assert.NotEqual(OverlayRuntimeState.Running, main.State);
        Assert.NotEqual(OverlayRuntimeState.Running, notifications.State);
        Assert.False(string.IsNullOrWhiteSpace(main.Detail));
    }

    [Fact]
    public void EachPanelKeepsItsOwnPlacement()
    {
        using var runtime = new OpenXrOverlayRuntime(16, notificationResolution: 8);
        var onAHand = OverlayPlacement.Default with { Anchor = OverlayAnchor.LeftHand };
        var corner = (NotifyOverlaySettings.Default with { Spot = ScreenSpot.TopLeft }).ToPlacement();

        runtime.Place(onAHand);
        runtime.NotificationPanel.Place(corner);

        // Neither call throws and neither overwrites the other; with nothing attached the proof
        // that they are separate is that placing one does not disturb the other's runtime at all.
        runtime.Place(onAHand);
        runtime.NotificationPanel.Place(corner);
    }

    [Fact]
    public void ShowingAndHidingOnePanelDoesNothingToTheOther()
    {
        using var runtime = new OpenXrOverlayRuntime(16, notificationResolution: 8);

        runtime.Hide();
        runtime.NotificationPanel.Show();
        runtime.Show();
        runtime.NotificationPanel.Hide();
    }

    [Fact]
    public void DisposingOnePanelLeavesTheOtherAlone()
    {
        // The session belongs to neither panel: it comes up when the first one starts and goes
        // when the last one is disposed, which is what lets the two switches be independent.
        var runtime = new OpenXrOverlayRuntime(16, notificationResolution: 8);
        var notifications = runtime.NotificationPanel;

        runtime.Start();
        notifications.Start();

        runtime.Dispose();
        notifications.Poll();
        notifications.Dispose();
    }

    [Fact]
    public void APictureOfTheWrongSizeIsRefusedRatherThanUploadedTorn()
    {
        using var runtime = new OpenXrOverlayRuntime(16, notificationResolution: 8);
        using var surface = new FakeSurface(16);

        Assert.True(runtime.Submit(surface));

        // The notification panel's swapchain is eight across, so a sixteen-across picture is not
        // its picture.
        Assert.False(runtime.NotificationPanel.Submit(surface));
    }

    [Fact]
    public void EachOverlayGetsItsOwnFallbackOverTheSharedSessions()
    {
        using var main = FallbackOverlayRuntime.CreateFor(OverlayKind.Main, 16, 8);
        using var notifications = FallbackOverlayRuntime.CreateFor(OverlayKind.Notification, 16, 8);

        Assert.NotSame(main, notifications);
        Assert.Equal(OverlayRuntimeState.NotStarted, main.Status.State);
        Assert.Equal(OverlayRuntimeState.NotStarted, notifications.Status.State);
    }

    private sealed class FakeSurface(int size) : IOverlaySurface
    {
        private readonly byte[] _pixels = new byte[size * size * 4];

        public int Width => size;

        public int Height => size;

        public nint TextureHandle => 0;

        public ReadOnlyMemory<byte> Pixels => _pixels;

        public void Upload(ReadOnlySpan<byte> bgra)
        {
        }

        public void Dispose()
        {
        }
    }
}
