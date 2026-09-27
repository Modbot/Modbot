using System.Numerics;
using Modbot.Companion.Overlay;
using Modbot.Overlay.Interaction;
using Modbot.Overlay.OpenVr;
using Modbot.Overlay.Rendering;
using Modbot.Overlay.Views;

namespace Modbot.Overlay.Tests.Interaction;

/// <summary>
/// The bar through the two hosts, with controllers: pressing its switches changes and saves the
/// placement and never reaches the drive loop, and the notification panel can be picked up and
/// goes back onto the head when it is let go.
/// </summary>
public class PanelBarInputTests
{
    private const int MainSize = 256;

    private const int PopUpSize = NotifyOverlaySettings.PanelPixels;

    private sealed class FakeSurface(int size) : IOverlaySurface
    {
        public int Width => size;

        public int Height => size;

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

        public void Place(OverlayPlacement placement)
        {
        }

        public void Dispose()
        {
        }
    }

    private static OverlayScreen Roster() => new(
        "Cat Lounge",
        new Cached<InstanceContext>(
            new InstanceContext("39911", [new RosterMember("usr_1", "Rin", RosterStanding.Member, 0, [])]),
            Freshness.Fresh,
            TimeSpan.Zero),
        Freshness.Fresh);

    /// <summary>Where on a flat, unturned, head-fixed panel a fraction across and down is, with the head at the origin.</summary>
    private static Vector3 PointOn(OverlayPlacement placement, float across, float down) => new(
        placement.Offset.X + ((across - 0.5f) * placement.Width),
        placement.Offset.Y - ((down - 0.5f) * placement.Width),
        placement.Offset.Z);

    /// <summary>The middle of a target, found by looking across the drawn frame.</summary>
    private static (float Across, float Down) Find(Func<float, float, OverlayTarget?> at, Func<OverlayTarget?, bool> wanted)
    {
        var hits = new List<(float, float)>();
        for (var y = 0; y < 128; y++)
        {
            for (var x = 0; x < 128; x++)
            {
                var (across, down) = ((x + 0.5f) / 128f, (y + 0.5f) / 128f);
                if (wanted(at(across, down)))
                    hits.Add((across, down));
            }
        }

        Assert.NotEmpty(hits);
        return (hits.Average(h => h.Item1), hits.Average(h => h.Item2));
    }

    private static HandState Pointing(Vector3 at, bool click = false, bool grab = false)
        => Hands.Hand(Hands.AimingAt(Vector3.Zero, at), grab: grab, click: click);

    [Fact]
    public void PressingTheLockLocksThePanelAndSavesItAndTellsTheDriveLoopNothing()
    {
        AvaloniaTestHost.Run(() =>
        {
            var runtime = new AttachedRuntime();
            using var host = new OverlayHost(runtime, new FakeSurface(MainSize), new AvaloniaFrameRenderer(MainSize, MainSize));
            runtime.Start();
            host.Start();

            // The bar answers only in edit mode, which is off until the window's switch says so.
            host.EditMode = true;
            host.Update(Roster());

            var saved = new List<OverlayPlacement>();
            var tapped = new List<OverlayTarget?>();
            host.PlacementChanged += saved.Add;
            host.Tapped += tapped.Add;

            var (across, down) = Find(host.TargetAt, t => t is OverlayTarget.Lock);
            var at = PointOn(host.Placement, across, down);

            runtime.Tracking = Hands.RightOnly(Pointing(at));
            host.PollInput(TimeSpan.FromMilliseconds(0));
            runtime.Tracking = Hands.RightOnly(Pointing(at, click: true));
            host.PollInput(TimeSpan.FromMilliseconds(33));

            Assert.True(host.Placement.Locked);
            Assert.True(saved[^1].Locked);
            Assert.Empty(tapped);

            // Locked, a grip on it takes nothing.
            runtime.Tracking = Hands.RightOnly(Pointing(PointOn(host.Placement, 0.5f, 0.2f), grab: true));
            host.PollInput(TimeSpan.FromMilliseconds(66));
            Assert.Null(host.Holding);
        });
    }

    [Fact]
    public void PressingTheHandLetsRaysThroughAndPressingItAgainTakesThemBack()
    {
        AvaloniaTestHost.Run(() =>
        {
            var runtime = new AttachedRuntime();
            using var host = new OverlayHost(runtime, new FakeSurface(MainSize), new AvaloniaFrameRenderer(MainSize, MainSize));
            runtime.Start();
            host.Start();

            // The bar answers only in edit mode, which is off until the window's switch says so.
            host.EditMode = true;
            host.Update(Roster());

            var (across, down) = Find(host.TargetAt, t => t is OverlayTarget.ClickThrough);
            var hand = PointOn(host.Placement, across, down);

            runtime.Tracking = Hands.RightOnly(Pointing(hand));
            host.PollInput(TimeSpan.FromMilliseconds(0));
            runtime.Tracking = Hands.RightOnly(Pointing(hand, click: true));
            host.PollInput(TimeSpan.FromMilliseconds(33));
            Assert.True(host.Placement.ClickThrough);

            // Off the bar: no cursor, and the ray is not this panel's.
            runtime.Tracking = Hands.RightOnly(Pointing(PointOn(host.Placement, 0.5f, 0.1f)));
            host.PollInput(TimeSpan.FromMilliseconds(66));
            Assert.Null(host.Showing.Cursor);
            Assert.Null(host.Busy);

            // Back on the bar's hand, which still answers.
            runtime.Tracking = Hands.RightOnly(Pointing(hand));
            host.PollInput(TimeSpan.FromMilliseconds(99));
            runtime.Tracking = Hands.RightOnly(Pointing(hand, click: true));
            host.PollInput(TimeSpan.FromMilliseconds(132));
            Assert.False(host.Placement.ClickThrough);
        });
    }

    [Fact]
    public void ThePopUpPanelIsPickedUpAndGoesBackOnTheHead()
    {
        AvaloniaTestHost.Run(() =>
        {
            var runtime = new AttachedRuntime();
            using var host = new NotificationHost(
                runtime, new FakeSurface(PopUpSize), new AvaloniaFrameRenderer(PopUpSize, PopUpSize), NotifyOverlaySettings.Default.ToPlacement());
            runtime.Start();
            host.Start();

            // The bar answers only in edit mode, which is off until the window's switch says so.
            host.EditMode = true;
            host.Update(NotificationScreen.Empty);

            var saved = new List<OverlayPlacement>();
            host.PlacementChanged += saved.Add;

            var start = host.Placement;

            // An empty pop-up panel draws nothing but its bar, and a ray on clear ground is not on
            // the panel; the bar is where it is picked up.
            var (barAcross, barDown) = Find(host.TargetAt, t => t is OverlayTarget.Bar);
            var centre = PointOn(start, barAcross, barDown);

            runtime.Tracking = Hands.RightOnly(Pointing(centre, grab: true));
            host.PollInput(TimeSpan.FromMilliseconds(0));
            Assert.Equal(Hand.Right, host.Holding);

            var moved = Hands.Hand(Hands.AimingAt(new Vector3(0, -0.1f, 0), centre + new Vector3(0, -0.1f, 0)), grab: true);
            runtime.Tracking = Hands.RightOnly(moved);
            host.PollInput(TimeSpan.FromMilliseconds(33));
            runtime.Tracking = Hands.RightOnly(moved with { Grab = false });
            host.PollInput(TimeSpan.FromMilliseconds(66));

            Assert.Null(host.Holding);
            Assert.Equal(OverlayAnchor.Head, host.Placement.Anchor);
            Assert.Equal(start.Offset.Y - 0.1f, host.Placement.Offset.Y, 2);
            Assert.Equal(OverlayAnchor.Head, saved[^1].Anchor);
        });
    }

    [Fact]
    public void ThePopUpPanelsLockIsOnItsBar()
    {
        AvaloniaTestHost.Run(() =>
        {
            var runtime = new AttachedRuntime();
            using var host = new NotificationHost(
                runtime, new FakeSurface(PopUpSize), new AvaloniaFrameRenderer(PopUpSize, PopUpSize), NotifyOverlaySettings.Default.ToPlacement());
            runtime.Start();
            host.Start();

            // The bar answers only in edit mode, which is off until the window's switch says so.
            host.EditMode = true;
            host.Update(NotificationScreen.Empty);

            var saved = new List<OverlayPlacement>();
            host.PlacementChanged += saved.Add;

            var (across, down) = Find(host.TargetAt, t => t is OverlayTarget.Lock);
            var at = PointOn(host.Placement, across, down);

            runtime.Tracking = Hands.RightOnly(Pointing(at));
            host.PollInput(TimeSpan.FromMilliseconds(0));
            runtime.Tracking = Hands.RightOnly(Pointing(at, click: true));
            host.PollInput(TimeSpan.FromMilliseconds(33));

            Assert.True(host.Placement.Locked);
            Assert.True(saved[^1].Locked);

            runtime.Tracking = Hands.RightOnly(Pointing(PointOn(host.Placement, 0.5f, 0.4f), grab: true));
            host.PollInput(TimeSpan.FromMilliseconds(66));
            Assert.Null(host.Holding);
        });
    }
}
