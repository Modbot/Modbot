using Modbot.Companion.Overlay;
using Modbot.Overlay.OpenVr;
using Modbot.Overlay.Rendering;
using Modbot.Overlay.Views;

namespace Modbot.Overlay.Tests;

/// <summary>
/// <strong>Put it back in front of me</strong>, at the host: the panel goes straight ahead, is
/// shown again if it was hidden, and draws its card for a few seconds even where it would
/// otherwise draw nothing.
/// </summary>
/// <remarks>
/// Outside a group instance the panel draws a fully see-through frame. A moderator who pressed
/// the old button there got a panel in front of them that could not be seen, which is the report
/// this button was rebuilt for.
/// </remarks>
public class OverlayHostPutBackTests
{
    private const int Size = 64;

    private sealed class FakeSurface : IOverlaySurface
    {
        public int Width => Size;

        public int Height => Size;

        public nint TextureHandle => 1;

        public ReadOnlyMemory<byte> Pixels => ReadOnlyMemory<byte>.Empty;

        public void Upload(ReadOnlySpan<byte> bgra)
        {
        }

        public void Dispose()
        {
        }
    }

    private static readonly OverlayPlacement Lost = new(
        OverlayAnchor.World,
        new OverlayPose(4f, -2f, 3f),
        0.2f,
        Opacity: 0.1f,
        Curve: 1f,
        Locked: true,
        ClickThrough: true);

    private static OverlayScreen Roster() => new(
        "Cat Lounge",
        new Cached<InstanceContext>(
            new InstanceContext("39911", [new RosterMember("usr_1", "Rin", RosterStanding.Member, 0, [])]),
            Freshness.Fresh,
            TimeSpan.Zero),
        Freshness.Fresh);

    private static OverlayHost Build(HeadlessOverlayRuntime runtime)
        => new(runtime, new FakeSurface(), new AvaloniaFrameRenderer(Size, Size), Lost);

    [Fact]
    public void ThePanelGoesStraightAheadWithNothingLeftThatHidesIt()
    {
        AvaloniaTestHost.Run(() =>
        {
            var runtime = new HeadlessOverlayRuntime();
            using var host = Build(runtime);

            OverlayPlacement? saved = null;
            host.PlacementChanged += placement => saved = placement;

            host.PutBack(OverlayPlacement.StraightAhead);

            Assert.Equal(OverlayPlacement.StraightAhead, host.Placement);
            Assert.Equal(OverlayPlacement.StraightAhead, runtime.Placement);
            Assert.Equal(OverlayPlacement.StraightAhead, saved);
        });
    }

    [Fact]
    public void AHiddenPanelIsShownAgain()
    {
        AvaloniaTestHost.Run(() =>
        {
            var runtime = new HeadlessOverlayRuntime();
            using var host = Build(runtime);

            host.Hide();
            Assert.False(runtime.IsShowing);

            host.PutBack(OverlayPlacement.StraightAhead);

            Assert.True(runtime.IsShowing);
        });
    }

    [Fact]
    public void AnIdlePanelDrawsItsCardForAFewSecondsThenNothingAgain()
    {
        AvaloniaTestHost.Run(() =>
        {
            using var host = Build(new HeadlessOverlayRuntime());

            host.Update(OverlayScreen.Idle);
            Assert.Equal(1, host.FramesDrawn);
            Assert.False(host.Showing.ShowIdleCard);

            host.PutBack(OverlayPlacement.StraightAhead);
            Assert.Equal(2, host.FramesDrawn);
            Assert.True(host.Showing.ShowIdleCard);
            Assert.True(host.PuttingBack);

            // The drive loop goes on handing over the idle screen; the card stays up.
            Assert.False(host.Update(OverlayScreen.Idle));
            Assert.True(host.Showing.ShowIdleCard);

            var start = TimeSpan.FromSeconds(100);
            host.PollInput(start);
            host.PollInput(start + OverlayHost.PutBackShowsFor - TimeSpan.FromMilliseconds(1));
            Assert.True(host.Showing.ShowIdleCard);
            Assert.Equal(2, host.FramesDrawn);

            host.PollInput(start + OverlayHost.PutBackShowsFor);
            Assert.False(host.PuttingBack);
            Assert.False(host.Showing.ShowIdleCard);
            Assert.Equal(3, host.FramesDrawn);
        });
    }

    [Fact]
    public void APanelNotYetHandedAScreenStillDrawsTheCard()
    {
        AvaloniaTestHost.Run(() =>
        {
            var runtime = new HeadlessOverlayRuntime();
            using var host = Build(runtime);
            Assert.Equal(0, host.FramesDrawn);

            host.PutBack(OverlayPlacement.StraightAhead);

            Assert.Equal(1, host.FramesDrawn);
            Assert.Equal(1, runtime.Submissions);
            Assert.True(host.Showing.ShowIdleCard);
        });
    }

    [Fact]
    public void APanelWithSomethingToSayKeepsSayingIt()
    {
        AvaloniaTestHost.Run(() =>
        {
            using var host = Build(new HeadlessOverlayRuntime());

            host.Update(Roster());
            host.PutBack(OverlayPlacement.StraightAhead);

            Assert.Equal("Cat Lounge", host.Showing.GroupLabel);
            Assert.False(host.Showing.ShowIdleCard);
        });
    }
}
