using System.Numerics;
using Modbot.Companion.Overlay;
using Modbot.Overlay.Interaction;
using Modbot.Overlay.OpenVr;
using Modbot.Overlay.Rendering;
using Modbot.Overlay.Views;

namespace Modbot.Overlay.Tests.Interaction;

/// <summary>
/// The host with controllers: the cursor lands in the frame, a tap says what it hit, a grab
/// moves the placement through the runtime and out to be saved, and scrolling over the roster
/// comes out in rows.
/// </summary>
public class OverlayHostInputTests
{
    private const int Size = 256;

    private static readonly Vector3 Centre = new(0.35f, -0.28f, -1.0f);

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

    private static OverlayScreen Roster() => new(
        "Cat Lounge",
        new Cached<InstanceContext>(
            new InstanceContext("39911", [.. Enumerable.Range(0, 3).Select(i => new RosterMember($"usr_{i}", $"Person {i}", RosterStanding.Member, 0, []))]),
            Freshness.Fresh,
            TimeSpan.Zero),
        Freshness.Fresh);

    private static (OverlayHost Host, AttachedRuntime Runtime) Build()
    {
        var runtime = new AttachedRuntime();
        var host = new OverlayHost(runtime, new FakeSurface(), new AvaloniaFrameRenderer(Size, Size));
        runtime.Start();
        host.Start();
        host.Update(Roster());
        return (host, runtime);
    }

    private static Pose Aim(Vector3 target) => Hands.AimingAt(Vector3.Zero, target);

    [Fact]
    public void PointingDrawsTheCursorAndLeavingTakesItAway()
    {
        AvaloniaTestHost.Run(() =>
        {
            var (host, runtime) = Build();
            var frames = host.FramesDrawn;

            runtime.Tracking = Hands.RightOnly(Hands.Hand(Aim(Centre)));
            host.PollInput(TimeSpan.Zero);

            Assert.Equal(frames + 1, host.FramesDrawn);
            Assert.NotNull(host.Showing.Cursor);
            Assert.Equal(0.5f, host.Showing.Cursor.Value.Across, 1);

            runtime.Tracking = OverlayTracking.None;
            host.PollInput(TimeSpan.FromMilliseconds(50));

            Assert.Equal(frames + 2, host.FramesDrawn);
            Assert.Null(host.Showing.Cursor);
        });
    }

    [Fact]
    public void ATapSaysWhatItLandedOn()
    {
        AvaloniaTestHost.Run(() =>
        {
            var (host, runtime) = Build();
            var taps = new List<OverlayTarget?>();
            host.Tapped += taps.Add;

            // The first row is near the top of the panel; find it from the tree and aim there.
            runtime.Tracking = Hands.RightOnly(Hands.Hand(Aim(Centre)));
            host.PollInput(TimeSpan.Zero);
            var row = FindRow(host, "usr_0");

            runtime.Tracking = Hands.RightOnly(Hands.Hand(AimAtPanel(row.Across, row.Down), click: true));
            host.PollInput(TimeSpan.FromMilliseconds(50));

            Assert.Equal([new OverlayTarget.Person("usr_0")], taps);
        });
    }

    [Fact]
    public void GrabbingPlacesThePanelThroughTheRuntimeAndAnnouncesIt()
    {
        AvaloniaTestHost.Run(() =>
        {
            var (host, runtime) = Build();
            var announced = new List<OverlayPlacement>();
            host.PlacementChanged += announced.Add;
            var placedBefore = runtime.Placed.Count;

            runtime.Tracking = Hands.RightOnly(Hands.Hand(Aim(Centre), grab: true));
            host.PollInput(TimeSpan.Zero);

            Assert.Equal(Hand.Right, host.Holding);
            Assert.Equal(OverlayAnchor.RightHand, host.Placement.Anchor);
            Assert.Equal(placedBefore + 1, runtime.Placed.Count);
            Assert.Single(announced);

            runtime.Tracking = Hands.RightOnly(Hands.Hand(Aim(Centre)));
            host.PollInput(TimeSpan.FromMilliseconds(100));

            Assert.Null(host.Holding);
            Assert.Equal(OverlayAnchor.World, host.Placement.Anchor);
            Assert.Equal(2, announced.Count);
        });
    }

    [Fact]
    public void ScrollingOverTheRosterComesOutInRows()
    {
        AvaloniaTestHost.Run(() =>
        {
            var (host, runtime) = Build();
            var rows = new List<int>();
            host.RosterScrolled += rows.Add;

            runtime.Tracking = Hands.RightOnly(Hands.Hand(Aim(Centre)));
            host.PollInput(TimeSpan.Zero);
            var row = FindRow(host, "usr_1");

            for (var i = 0; i < (int)OverlayHost.ScrollPerRow * 2; i++)
            {
                runtime.Tracking = Hands.RightOnly(Hands.Hand(AimAtPanel(row.Across, row.Down), scroll: new Vector2(0, -1)));
                host.PollInput(TimeSpan.FromMilliseconds(10 * i));
            }

            Assert.Equal(2, rows.Sum());
            Assert.All(rows, r => Assert.True(r > 0));
        });
    }

    [Fact]
    public void PlacingFromTheSettingsPageReachesTheRuntime()
    {
        AvaloniaTestHost.Run(() =>
        {
            var (host, runtime) = Build();
            var wall = OverlayPlacement.Default with { Anchor = OverlayAnchor.World, Width = 0.8f };

            host.Place(wall);

            Assert.Equal(wall, runtime.Placed[^1]);
            Assert.Equal(wall, host.Placement);
        });
    }

    private static (float Across, float Down) FindRow(OverlayHost host, string subjectId)
    {
        // Sweep the panel for the row, using the host's own hit testing.
        for (var down = 0.02f; down < 1f; down += 0.01f)
        {
            if (host.TargetAt(0.3f, down) is OverlayTarget.Person { SubjectId: var id } && id == subjectId)
                return (0.3f, down);
        }

        throw new InvalidOperationException($"No row for {subjectId} on the panel.");
    }

    private static Pose AimAtPanel(float across, float down)
    {
        var width = OverlayPlacement.Default.Width;
        var target = Centre + new Vector3((across - 0.5f) * width, (0.5f - down) * width, 0);
        return Aim(target);
    }
}
