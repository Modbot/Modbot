using Modbot.Companion.Overlay;
using Modbot.Overlay.OpenVr;
using Modbot.Overlay.Rendering;
using Modbot.Overlay.Views;

namespace Modbot.Overlay.Tests;

/// <summary>
/// The notification host's own rules: nothing to say draws nothing, the same pop-ups again draw
/// nothing, and a different pop-up draws once.
/// </summary>
/// <remarks>
/// This panel is pinned to the corner of somebody's eye for the whole time they are in VRChat, so
/// "usually empty" has to cost nothing at all — not a blank frame every tick.
/// </remarks>
public class NotificationHostTests
{
    private const int Size = 64;

    private sealed class FakeSurface : IOverlaySurface
    {
        public int Width => Size;

        public int Height => Size;

        public nint TextureHandle => 1;

        public ReadOnlyMemory<byte> Pixels => ReadOnlyMemory<byte>.Empty;

        public int Uploads { get; private set; }

        public void Upload(ReadOnlySpan<byte> bgra) => Uploads++;

        public void Dispose()
        {
        }
    }

    private static NotificationScreen Screen(params string[] ids)
        => new([.. ids.Select(id => new PopUp(id, "Cat Lounge", "Somebody", null, PopUpTone.Flagged))]);

    [Fact]
    public void TheFirstScreenIsDrawnEvenWhenItIsEmpty()
    {
        // The same bug the main panel had: a host that believed it was already showing nothing
        // left an attached overlay with no texture at all.
        AvaloniaTestHost.Run(() =>
        {
            var runtime = new HeadlessOverlayRuntime();
            using var host = new NotificationHost(runtime, new FakeSurface(), new AvaloniaFrameRenderer(Size, Size));

            Assert.True(host.Update(NotificationScreen.Empty));
            Assert.Equal(1, host.FramesDrawn);
        });
    }

    [Fact]
    public void TheSamePopUpsAgainDrawNothing()
    {
        AvaloniaTestHost.Run(() =>
        {
            var runtime = new HeadlessOverlayRuntime();
            using var host = new NotificationHost(runtime, new FakeSurface(), new AvaloniaFrameRenderer(Size, Size));

            host.Update(Screen("a1"));
            var drawn = host.FramesDrawn;

            Assert.False(host.Update(Screen("a1")));
            Assert.Equal(drawn, host.FramesDrawn);
        });
    }

    [Fact]
    public void ANewPopUpDrawsOnce()
    {
        AvaloniaTestHost.Run(() =>
        {
            var runtime = new HeadlessOverlayRuntime();
            using var host = new NotificationHost(runtime, new FakeSurface(), new AvaloniaFrameRenderer(Size, Size));

            host.Update(Screen("a1"));
            var drawn = host.FramesDrawn;

            Assert.True(host.Update(Screen("a2", "a1")));
            Assert.Equal(drawn + 1, host.FramesDrawn);
            Assert.Equal(2, host.Showing.PopUps.Count);
        });
    }

    [Fact]
    public void ThePanelGoesWhereTheSettingsSayWithNoHeadsetAtAll()
    {
        AvaloniaTestHost.Run(() =>
        {
            var runtime = new HeadlessOverlayRuntime();
            var settings = NotificationSettings.Default with { Spot = ScreenSpot.BottomLeft, Distance = 1.5f };
            using var host = new NotificationHost(runtime, new FakeSurface(), new AvaloniaFrameRenderer(Size, Size), settings.ToPlacement());

            Assert.Equal(OverlayAnchor.Head, host.Placement.Anchor);
            Assert.True(host.Placement.Offset.X < 0f, "A left spot puts the panel left of centre.");
            Assert.True(host.Placement.Offset.Y < 0f, "A bottom spot puts the panel below the eye line.");
            Assert.Equal(runtime.Placement, host.Placement);
        });
    }
}
