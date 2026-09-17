using System.Numerics;
using Modbot.Companion.Overlay;
using Modbot.Overlay.Interaction;
using Modbot.Overlay.OpenVr;
using Modbot.Overlay.Rendering;
using Modbot.Overlay.Views;

namespace Modbot.Overlay.Tests.Interaction;

/// <summary>
/// Fixing the panel to something else from the settings page puts it where that anchor makes
/// sense: choosing a hand brings the panel to the hand rather than leaving it a metre ahead, and
/// choosing the room pins it where it is rather than sending it to the room's origin.
/// </summary>
public class OverlayHostAnchorTests
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

    private sealed class AttachedRuntime : IOverlayRuntime
    {
        public OverlayRuntimeStatus Status { get; private set; } = new(OverlayRuntimeState.NotStarted);

        public OverlayTracking Tracking { get; set; } = OverlayTracking.None;

        public OverlayPlacement? Placed { get; private set; }

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

        public void Place(OverlayPlacement placement) => Placed = placement;

        public void Dispose()
        {
        }
    }

    private static (OverlayHost Host, AttachedRuntime Runtime) Build()
    {
        var runtime = new AttachedRuntime();
        var host = new OverlayHost(runtime, new FakeSurface(), new AvaloniaFrameRenderer(Size, Size));
        runtime.Start();
        host.Start();
        host.Update(OverlayScreen.Idle);
        return (host, runtime);
    }

    [Fact]
    public void AHandAnchorPutsThePanelJustAboveTheHand()
    {
        AvaloniaTestHost.Run(() =>
        {
            var (host, runtime) = Build();

            host.Anchor(OverlayAnchor.LeftHand);

            Assert.Equal(OverlayAnchor.LeftHand, host.Placement.Anchor);
            Assert.Equal(OverlayPlacement.HandOffset, host.Placement.Offset);
            Assert.Equal(host.Placement, runtime.Placed);
        });
    }

    [Fact]
    public void TheInstanceAnchorPinsThePanelWhereItIs()
    {
        AvaloniaTestHost.Run(() =>
        {
            var (host, runtime) = Build();

            // The head is standing at (1, 1.6, 2), turned to face +X; the panel is in front of it.
            var head = new Pose(new Vector3(1f, 1.6f, 2f), Quaternion.CreateFromAxisAngle(Vector3.UnitY, -MathF.PI / 2));
            runtime.Tracking = new OverlayTracking(head, HandState.Missing, HandState.Missing);
            host.PollInput(TimeSpan.Zero);
            var before = PanelGeometry.PanelPose(host.Placement, runtime.Tracking)!.Value;

            host.Anchor(OverlayAnchor.World);

            Assert.Equal(OverlayAnchor.World, host.Placement.Anchor);
            var after = Pose.From(host.Placement.Offset);
            Assert.True(Vector3.Distance(before.Position, after.Position) < 1e-4f, $"{before.Position} became {after.Position}");
            Assert.True(MathF.Abs(Quaternion.Dot(before.Rotation, after.Rotation)) > 0.9999f);
        });
    }

    [Fact]
    public void TheHeadAnchorPutsThePanelBackInFront()
    {
        AvaloniaTestHost.Run(() =>
        {
            var (host, _) = Build();
            host.Place(OverlayPlacement.Default with { Anchor = OverlayAnchor.World, Offset = new OverlayPose(3, 1, -2), Width = 0.7f });

            host.Anchor(OverlayAnchor.Head);

            Assert.Equal(OverlayAnchor.Head, host.Placement.Anchor);
            Assert.Equal(OverlayPlacement.Default.Offset, host.Placement.Offset);
            Assert.Equal(0.7f, host.Placement.Width);
        });
    }
}
