using Modbot.Companion.Overlay;
using Modbot.Overlay.OpenVr;
using Modbot.Overlay.Rendering;
using Modbot.Overlay.Views;

namespace Modbot.Overlay.Tests;

/// <summary>
/// The host's own rules: the first screen is drawn even when it is the idle one, a pinned screen
/// wins over the live one until it is let go, and the last frame is kept only when asked for.
/// </summary>
/// <remarks>
/// The first of those was a real bug. The host started out believing it was already showing the
/// idle screen, so a moderator who attached SteamVR outside a group instance got an overlay with
/// no texture at all: nothing in the headset, and nothing in the log to say why.
/// </remarks>
public class OverlayHostTests
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

    private static OverlayScreen Roster(string name) => new(
        "Cat Lounge",
        new Cached<InstanceContext>(
            new InstanceContext("39911", [new RosterMember("usr_1", name, RosterStanding.Member, 0, [])]),
            Freshness.Fresh,
            TimeSpan.Zero),
        Freshness.Fresh);

    private static string? FirstName(OverlayScreen screen) => screen.Roster.Value?.Members[0].DisplayName;

    [Fact]
    public void TheIdleScreenIsDrawnOnceSoAnAttachedOverlayIsNotBlank()
    {
        AvaloniaTestHost.Run(() =>
        {
            var runtime = new HeadlessOverlayRuntime();
            var surface = new FakeSurface();
            using var host = new OverlayHost(runtime, surface, new AvaloniaFrameRenderer(Size, Size));

            Assert.True(host.Update(OverlayScreen.Idle));
            Assert.Equal(1, host.FramesDrawn);
            Assert.Equal(1, surface.Uploads);
            Assert.Equal(1, runtime.Submissions);

            Assert.False(host.Update(OverlayScreen.Idle));
            Assert.Equal(1, host.FramesDrawn);
        });
    }

    [Fact]
    public void APinnedScreenIsDrawnInsteadOfTheLiveOneUntilItIsLetGo()
    {
        AvaloniaTestHost.Run(() =>
        {
            using var host = new OverlayHost(new HeadlessOverlayRuntime(), new FakeSurface(), new AvaloniaFrameRenderer(Size, Size));

            host.Update(Roster("Rin"));
            Assert.Equal(1, host.FramesDrawn);

            host.Pinned = Roster("Sample");
            Assert.Equal(2, host.FramesDrawn);
            Assert.Equal("Sample", FirstName(host.Showing));

            // The live screen moves on underneath; the pinned one stays up and nothing is drawn.
            Assert.False(host.Update(Roster("Kai")));
            Assert.Equal(2, host.FramesDrawn);
            Assert.Equal("Sample", FirstName(host.Showing));

            host.Pinned = null;
            Assert.Equal(3, host.FramesDrawn);
            Assert.Equal("Kai", FirstName(host.Showing));
        });
    }

    [Fact]
    public void TheLastFrameIsKeptOnlyWhenAsked()
    {
        AvaloniaTestHost.Run(() =>
        {
            using var host = new OverlayHost(new HeadlessOverlayRuntime(), new FakeSurface(), new AvaloniaFrameRenderer(Size, Size));

            host.Update(OverlayScreen.Idle);
            Assert.True(host.LastFrame.IsEmpty);

            host.KeepLastFrame = true;
            host.Update(Roster("Rin"));
            Assert.Equal(Size * Size * 4, host.LastFrame.Length);
        });
    }

    [Fact]
    public void ShowAndHideReachThePanelWithoutTakingItDown()
    {
        // This is what "Modbot, show overlay" and "Modbot, hide overlay" arrive at. It is the show
        // and hide the panel is already put up with when it is built, rather than the settings
        // switch beside it: the switch builds the panel, connects to the headset and drops all of
        // it again, and a curtain should not do that. So the host stays, its texture stays, and a
        // screen pushed while it is hidden is still drawn -- it comes back showing what is
        // happening now rather than what was happening when it went away.
        AvaloniaTestHost.Run(() =>
        {
            var runtime = new HeadlessOverlayRuntime();
            using var host = new OverlayHost(runtime, new FakeSurface(), new AvaloniaFrameRenderer(Size, Size));

            host.Show();
            Assert.True(runtime.IsShowing);

            host.Hide();
            Assert.False(runtime.IsShowing);

            Assert.True(host.Update(Roster("Rin")));
            Assert.Equal("Rin", FirstName(host.Showing));
            Assert.False(runtime.IsShowing);

            host.Show();
            Assert.True(runtime.IsShowing);
        });
    }
}
