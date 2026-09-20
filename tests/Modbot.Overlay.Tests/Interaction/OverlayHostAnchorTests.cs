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

    /// <summary>
    /// Choosing a wrist puts the panel in the watch position at the watch size, on either hand,
    /// and tells the runtime about it.
    /// </summary>
    [Theory]
    [InlineData(OverlayAnchor.LeftHand)]
    [InlineData(OverlayAnchor.RightHand)]
    public void AWristAnchorPutsThePanelOnTheWristAtTheWristSize(OverlayAnchor wrist)
    {
        AvaloniaTestHost.Run(() =>
        {
            var (host, runtime) = Build();

            host.Anchor(wrist);

            Assert.Equal(wrist, host.Placement.Anchor);
            Assert.Equal(OverlayPlacement.WristOffset, host.Placement.Offset);
            Assert.Equal(OverlayPlacement.WristWidth, host.Placement.Width);
            Assert.True(host.OnWrist);
            Assert.Equal(host.Placement, runtime.Placed);
        });
    }

    /// <summary>
    /// The wrist panel faces out of the back of the hand, not along the controller: that is the
    /// difference between a watch and a card floating past somebody's knuckles.
    /// </summary>
    [Fact]
    public void TheWristPanelFacesOutOfTheBackOfTheHand()
    {
        var face = Vector3.Transform(Vector3.UnitZ, Pose.From(OverlayPlacement.WristOffset).Rotation);

        // Mostly the controller's +Y, which is the face a wand's touchpad is on: the back of the
        // hand. The rest leans back towards the elbow (+Z), so a raised forearm points it at the eyes.
        Assert.True(face.Y > 0.9f, $"the wrist panel faces {face}");
        Assert.True(face.Z is > 0.2f and < 0.5f, $"the wrist panel faces {face}");
        Assert.True(MathF.Abs(face.X) < 1e-5f, $"the wrist panel faces {face}");
    }

    /// <summary>Leaving a wrist gives the panel a readable width back rather than keeping the watch size.</summary>
    [Fact]
    public void ComingOffTheWristGivesThePanelItsWidthBack()
    {
        AvaloniaTestHost.Run(() =>
        {
            var (host, _) = Build();
            host.Anchor(OverlayAnchor.RightHand);

            host.Anchor(OverlayAnchor.Head);

            Assert.Equal(OverlayPlacement.Default.Width, host.Placement.Width);
            Assert.False(host.OnWrist);
        });
    }

    /// <summary>A width the moderator chose themselves is theirs, and coming off a wrist keeps it.</summary>
    [Fact]
    public void AChosenWidthSurvivesTheWrist()
    {
        Assert.Equal(0.9f, OverlayPlacement.WidthFor(OverlayAnchor.Head, 0.9f));
        Assert.Equal(OverlayPlacement.WristWidth, OverlayPlacement.WidthFor(OverlayAnchor.LeftHand, 0.9f));
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
