using System.Numerics;
using Modbot.Companion.Overlay;
using Modbot.Overlay.Interaction;
using Modbot.Overlay.OpenVr;
using Modbot.Overlay.OpenXr;
using Modbot.Overlay.Rendering;
using Modbot.Overlay.Views;

namespace Modbot.Overlay.Tests.Interaction;

/// <summary>
/// The controller hints beside the bar: up while a hand points at the panel or holds it, named for
/// the controller when the runtime says which it is, and not there when the panel is locked or lets rays through.
/// </summary>
public class ControlHintsHostTests
{
    private const int Size = 512;

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

    /// <summary>Attached, with whatever tracking and controller a test sets.</summary>
    private sealed class AttachedRuntime : IOverlayRuntime, IControllerKind
    {
        public OverlayRuntimeStatus Status { get; private set; } = new(OverlayRuntimeState.NotStarted);

        public OverlayTracking Tracking { get; set; } = OverlayTracking.None;

        public ControllerProfile? Controller { get; set; }

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

        public ControllerProfile? ControllerOf(Hand hand) => Controller;

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
            new InstanceContext("39911", [.. Enumerable.Range(0, 3).Select(i => new RosterMember($"usr_{i}", $"Person {i}", RosterStanding.Member, 0, []))]),
            Freshness.Fresh,
            TimeSpan.Zero),
        Freshness.Fresh);

    private static (OverlayHost Host, AttachedRuntime Runtime) Build(OverlayPlacement? placement = null)
    {
        var runtime = new AttachedRuntime();
        var host = new OverlayHost(runtime, new FakeSurface(), new AvaloniaFrameRenderer(Size, Size), placement);
        runtime.Start();
        host.Start();
        host.Update(Roster());
        return (host, runtime);
    }

    private static HandState Pointing(bool grab = false)
        => Hands.Hand(Hands.AimingAt(Vector3.Zero, Centre), grab: grab);

    [Fact]
    public void PointingShowsHowToTakeAndSizeIt()
    {
        AvaloniaTestHost.Run(() =>
        {
            var (host, runtime) = Build();
            Assert.Null(host.Hints);

            runtime.Tracking = Hands.RightOnly(Pointing());
            host.PollInput(TimeSpan.Zero);

            Assert.Equal(new ControlHint("Grip", "move"), host.Hints?.Move);
            Assert.Equal(new ControlHint("Both grips", "larger / smaller"), host.Hints?.Resize);
            Assert.Null(host.Hints?.Distance);
        });
    }

    [Fact]
    public void LeavingThePanelTakesTheHintsAway()
    {
        AvaloniaTestHost.Run(() =>
        {
            var (host, runtime) = Build();

            runtime.Tracking = Hands.RightOnly(Pointing());
            host.PollInput(TimeSpan.Zero);
            Assert.NotNull(host.Hints);

            runtime.Tracking = OverlayTracking.None;
            host.PollInput(TimeSpan.FromMilliseconds(50));
            Assert.Null(host.Hints);
        });
    }

    [Fact]
    public void HoldingItSwapsMoveForTheStick()
    {
        AvaloniaTestHost.Run(() =>
        {
            var (host, runtime) = Build();

            runtime.Tracking = Hands.RightOnly(Pointing(grab: true));
            host.PollInput(TimeSpan.Zero);

            Assert.Equal(Hand.Right, host.Holding);
            Assert.Null(host.Hints?.Move);
            Assert.Equal(new ControlHint("Both grips", "larger / smaller"), host.Hints?.Resize);
            Assert.Equal(new ControlHint("Stick", "closer / farther"), host.Hints?.Distance);
        });
    }

    [Fact]
    public void TheHintsNameTheControllerTheRuntimeSays()
    {
        AvaloniaTestHost.Run(() =>
        {
            var (host, runtime) = Build();
            runtime.Controller = ControllerBindings.Simple;

            runtime.Tracking = Hands.RightOnly(Pointing());
            host.PollInput(TimeSpan.Zero);

            Assert.Equal(new ControlHint("Menu button", "move"), host.Hints?.Move);
        });
    }

    [Fact]
    public void ALockedPanelShowsNoHints()
    {
        AvaloniaTestHost.Run(() =>
        {
            var (host, runtime) = Build(OverlayPlacement.Default with { Locked = true });

            runtime.Tracking = Hands.RightOnly(Pointing());
            host.PollInput(TimeSpan.Zero);

            Assert.NotNull(host.Showing.Cursor);
            Assert.Null(host.Hints);
        });
    }

    [Fact]
    public void APanelLettingRaysThroughShowsNoHints()
    {
        AvaloniaTestHost.Run(() =>
        {
            var (host, runtime) = Build(OverlayPlacement.Default with { ClickThrough = true });

            runtime.Tracking = Hands.RightOnly(Pointing());
            host.PollInput(TimeSpan.Zero);

            Assert.Null(host.Hints);
        });
    }
}
