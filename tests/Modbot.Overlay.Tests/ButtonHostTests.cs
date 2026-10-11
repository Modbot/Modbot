using System.Numerics;
using Modbot.Companion.Overlay;
using Modbot.Overlay.Interaction;
using Modbot.Overlay.OpenVr;
using Modbot.Overlay.Rendering;
using Modbot.Overlay.Tests.Interaction;
using Modbot.Overlay.Views;

namespace Modbot.Overlay.Tests;

/// <summary>
/// The show and hide button's host: a click on its face is a press, a click on the clear ground
/// around it is not, it cannot be picked up, and the place setting moves it.
/// </summary>
public class ButtonHostTests
{
    private const int Size = OverlayButton.PanelPixels;

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

    /// <summary>Attached, with whatever tracking a test sets.</summary>
    private sealed class AttachedRuntime : IOverlayRuntime
    {
        public OverlayRuntimeStatus Status { get; private set; } = new(OverlayRuntimeState.NotStarted);

        public OverlayTracking Tracking { get; set; } = OverlayTracking.None;

        public List<OverlayPlacement> Placed { get; } = [];

        public OverlayRuntimeStatus Start() => Status = new(OverlayRuntimeState.Running, Detail: "Attached to a test.");

        public void Poll()
        {
        }

        public bool Submit(IOverlaySurface surface) => true;

        public void Show()
        {
        }

        public void Hide()
        {
        }

        public OverlayTracking ReadTracking() => Tracking;

        public void Place(OverlayPlacement placement) => Placed.Add(placement);

        public void Dispose()
        {
        }
    }

    private static (ButtonHost Host, AttachedRuntime Runtime) Build(ButtonPlace place = ButtonPlace.Corner)
    {
        var runtime = new AttachedRuntime();
        var host = new ButtonHost(runtime, new FakeSurface(), new AvaloniaFrameRenderer(Size, Size), place);
        runtime.Start();
        host.Start();
        host.Update(ButtonScreen.Shown);
        return (host, runtime);
    }

    /// <summary>A ray from the origin at a spot on the corner button, in metres from its centre across and up.</summary>
    private static Pose Aim(ButtonHost host, float across = 0f, float up = 0f)
    {
        var centre = host.Placement.Offset;
        return Hands.AimingAt(Vector3.Zero, new Vector3(centre.X + across, centre.Y + up, centre.Z));
    }

    [Fact]
    public void AClickOnTheFaceIsAPress()
    {
        AvaloniaTestHost.Run(() =>
        {
            var (host, runtime) = Build();
            using var button = host;
            var presses = 0;
            host.Pressed += () => presses++;

            runtime.Tracking = Hands.RightOnly(Hands.Hand(Aim(host)));
            host.PollInput(TimeSpan.Zero);
            Assert.Equal(0, presses);

            runtime.Tracking = Hands.RightOnly(Hands.Hand(Aim(host), click: true));
            host.PollInput(TimeSpan.FromMilliseconds(50));

            Assert.Equal(1, presses);
            Assert.Equal(Hand.Right, host.Busy);
        });
    }

    [Fact]
    public void AClickOnTheClearGroundAroundTheFaceIsNotAPress()
    {
        AvaloniaTestHost.Run(() =>
        {
            var (host, runtime) = Build();
            using var button = host;
            var presses = 0;
            host.Pressed += () => presses++;

            // A hair inside the panel's left edge, in the margin the face leaves clear.
            var edge = (OverlayButton.CornerWidth / 2f) - 0.002f;

            runtime.Tracking = Hands.RightOnly(Hands.Hand(Aim(host, across: -edge), click: true));
            host.PollInput(TimeSpan.Zero);

            Assert.Equal(0, presses);
            Assert.Null(host.Busy);
        });
    }

    [Fact]
    public void AGripOnTheButtonDoesNotPickItUp()
    {
        AvaloniaTestHost.Run(() =>
        {
            var (host, runtime) = Build();
            using var button = host;
            var before = host.Placement;

            runtime.Tracking = Hands.RightOnly(Hands.Hand(Aim(host), grab: true));
            host.PollInput(TimeSpan.Zero);

            Assert.True(host.Placement.Locked);
            Assert.Equal(before, host.Placement);
        });
    }

    [Fact]
    public void ThePlaceSettingMovesTheButtonToThePlaceItNamesThroughTheRuntime()
    {
        AvaloniaTestHost.Run(() =>
        {
            var (host, runtime) = Build();
            using var button = host;

            Assert.Equal(OverlayAnchor.Head, host.Placement.Anchor);

            host.Place(ButtonPlace.Wrist);

            Assert.Equal(OverlayAnchor.LeftHand, host.Placement.Anchor);
            Assert.Equal(host.Placement, runtime.Placed[^1]);
            Assert.True(host.Placement.Locked);
        });
    }

    [Fact]
    public void TheSameScreenAgainDrawsNothingAndTheOtherStateDrawsOnce()
    {
        AvaloniaTestHost.Run(() =>
        {
            var (host, _) = Build();
            using var button = host;
            var drawn = host.FramesDrawn;

            Assert.False(host.Update(ButtonScreen.Shown));
            Assert.Equal(drawn, host.FramesDrawn);

            Assert.True(host.Update(ButtonScreen.Hidden));
            Assert.Equal(drawn + 1, host.FramesDrawn);
            Assert.False(host.Showing.PanelShown);
        });
    }
}
