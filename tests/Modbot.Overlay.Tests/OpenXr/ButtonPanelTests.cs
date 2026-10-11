using Modbot.Companion.Overlay;
using Modbot.Overlay.OpenVr;
using Modbot.Overlay.OpenXr;
using Modbot.Overlay.Rendering;
using Modbot.Overlay.Tests.OpenVr;

namespace Modbot.Overlay.Tests.OpenXr;

/// <summary>
/// The show and hide button as the third panel over one OpenXR session, and as its own overlay in
/// SteamVR: a runtime of its own, its own placement, its own key, and a swapchain of its own size.
/// </summary>
/// <remarks>
/// These run on a machine with no OpenXR loader, which is where CI is: every answer has to be a
/// state rather than an exception.
/// </remarks>
[Collection(OpenVrCollection.Name)]
public class ButtonPanelTests
{
    [Fact]
    public void TheButtonPanelIsARuntimeOfItsOwn()
    {
        using var runtime = new OpenXrOverlayRuntime(16, notificationResolution: 8, buttonResolution: 4);

        Assert.NotNull(runtime.ButtonPanel);
        Assert.NotSame(runtime, runtime.ButtonPanel);
        Assert.NotSame(runtime.NotificationPanel, runtime.ButtonPanel);
    }

    [Fact]
    public void TheButtonPanelGetsAStateRatherThanAnExceptionWithNoOpenXr()
    {
        using var runtime = new OpenXrOverlayRuntime(16, notificationResolution: 8, buttonResolution: 4);

        var status = runtime.ButtonPanel.Start();

        Assert.NotEqual(OverlayRuntimeState.Running, status.State);
        Assert.False(string.IsNullOrWhiteSpace(status.Detail));
    }

    [Fact]
    public void PlacingShowingAndHidingTheButtonDoNothingToTheOtherPanels()
    {
        using var runtime = new OpenXrOverlayRuntime(16, notificationResolution: 8, buttonResolution: 4);

        runtime.ButtonPanel.Place(OverlayButton.ToPlacement(ButtonPlace.Wrist));
        runtime.ButtonPanel.Hide();
        runtime.ButtonPanel.Show();
        runtime.Place(OverlayPlacement.Default);
        runtime.NotificationPanel.Hide();
    }

    [Fact]
    public void APictureOfTheWrongSizeIsRefusedByTheButtonPanel()
    {
        using var runtime = new OpenXrOverlayRuntime(16, notificationResolution: 8, buttonResolution: 4);
        using var surface = new FakeSurface(16);

        Assert.True(runtime.Submit(surface));
        Assert.False(runtime.ButtonPanel.Submit(surface));
    }

    [Fact]
    public void EachKindGetsItsOwnFallbackAndTheButtonIsOneOfThem()
    {
        using var main = FallbackOverlayRuntime.CreateFor(OverlayKind.Main, 16, 8, 4);
        using var button = FallbackOverlayRuntime.CreateFor(OverlayKind.Button, 16, 8, 4);

        Assert.NotSame(main, button);
        Assert.Equal(OverlayRuntimeState.NotStarted, button.Status.State);
    }

    [Fact]
    public void TheButtonIsItsOwnOverlayInSteamVrWithItsOwnKeyAndName()
    {
        using var main = new OpenVrOverlayRuntime();
        using var notifications = new OpenVrOverlayRuntime(OverlayKind.Notification);
        using var button = new OpenVrOverlayRuntime(OverlayKind.Button);

        Assert.Equal("moe.bin.modbot.button", button.Key);
        Assert.NotEqual(main.Key, button.Key);
        Assert.NotEqual(notifications.Key, button.Key);
        Assert.Equal(OverlayKind.Button, button.Kind);
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
