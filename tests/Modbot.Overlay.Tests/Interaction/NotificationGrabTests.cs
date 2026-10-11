using System.Numerics;
using Modbot.Companion.Overlay;
using Modbot.Overlay.Interaction;
using Modbot.Overlay.OpenVr;
using Modbot.Overlay.OpenXr;
using Modbot.Overlay.Rendering;
using Modbot.Overlay.Views;

namespace Modbot.Overlay.Tests.Interaction;

/// <summary>
/// The notification panel is taken hold of anywhere on its box, not only on a card or the bar, and
/// a hand on it shows the grip hints.
/// </summary>
public class NotificationGrabTests
{
    private const int Size = NotifyOverlaySettings.PanelPixels;

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

    private static NotificationScreen OneCard()
        => new([new PopUp("a1", "Cat Lounge", "Somebody", null, PopUpTone.Flagged)]);

    /// <summary>Where on a flat, unturned, head-fixed panel a fraction across and down is, with the head at the origin.</summary>
    private static Vector3 PointOn(OverlayPlacement placement, float across, float down) => new(
        placement.Offset.X + ((across - 0.5f) * placement.Width),
        placement.Offset.Y - ((down - 0.5f) * placement.Width),
        placement.Offset.Z);

    private static HandState Pointing(OverlayPlacement placement, float across, float down, bool grab = false)
        => Hands.Hand(Hands.AimingAt(Vector3.Zero, PointOn(placement, across, down)), grab: grab);

    private static (NotificationHost Host, AttachedRuntime Runtime) Build(NotificationScreen screen, OverlayPlacement? placement = null)
    {
        var runtime = new AttachedRuntime();
        var host = new NotificationHost(
            runtime, new FakeSurface(), new AvaloniaFrameRenderer(Size, Size), placement ?? NotifyOverlaySettings.Default.ToPlacement());
        runtime.Start();
        host.Start();
        host.Update(screen);
        return (host, runtime);
    }

    // Fractions of the texture: the box runs from the top to 256 of 300 pixels down, and from 22 to
    // 278 across; the cards are drawn from its top.
    private const float InsideBoxAcross = 0.5f;
    private const float EmptyPartOfBox = 0.75f;
    private const float BesideBox = 0.03f;

    [Fact]
    public void AnEmptyPanelCanBeTakenAnywhereOnItsBox()
    {
        AvaloniaTestHost.Run(() =>
        {
            var (host, runtime) = Build(NotificationScreen.Empty);
            using var _ = host;

            runtime.Tracking = Hands.RightOnly(Pointing(host.Placement, InsideBoxAcross, 0.4f, grab: true));
            host.PollInput(TimeSpan.Zero);

            Assert.Equal(Hand.Right, host.Holding);
        });
    }

    [Fact]
    public void ThePanelCanBeTakenBelowItsCardsWhereNothingIsDrawn()
    {
        AvaloniaTestHost.Run(() =>
        {
            var (host, runtime) = Build(OneCard());
            using var _ = host;

            runtime.Tracking = Hands.RightOnly(Pointing(host.Placement, InsideBoxAcross, EmptyPartOfBox, grab: true));
            host.PollInput(TimeSpan.Zero);

            Assert.Equal(Hand.Right, host.Holding);
        });
    }

    [Fact]
    public void TheClearGroundBesideTheBoxIsLookedPast()
    {
        AvaloniaTestHost.Run(() =>
        {
            var (host, runtime) = Build(NotificationScreen.Empty);
            using var _ = host;

            runtime.Tracking = Hands.RightOnly(Pointing(host.Placement, BesideBox, 0.4f, grab: true));
            host.PollInput(TimeSpan.Zero);

            Assert.Null(host.Holding);
            Assert.Null(host.Busy);
        });
    }

    [Fact]
    public void ALockedPanelsBoxIsLookedPastAndShowsNoHints()
    {
        AvaloniaTestHost.Run(() =>
        {
            var (host, runtime) = Build(NotificationScreen.Empty, NotifyOverlaySettings.Default.ToPlacement() with { Locked = true });
            using var _ = host;

            runtime.Tracking = Hands.RightOnly(Pointing(host.Placement, InsideBoxAcross, 0.4f));
            host.PollInput(TimeSpan.Zero);

            Assert.Null(host.Busy);
            Assert.Null(host.Hints);
        });
    }

    [Fact]
    public void PointingAtTheBoxShowsHowToTakeItAndLeavingTakesTheHintsAway()
    {
        AvaloniaTestHost.Run(() =>
        {
            var (host, runtime) = Build(OneCard());
            using var _ = host;
            Assert.Null(host.Hints);

            runtime.Tracking = Hands.RightOnly(Pointing(host.Placement, InsideBoxAcross, EmptyPartOfBox));
            host.PollInput(TimeSpan.Zero);

            Assert.Equal(new ControlHint("Grip", "move"), host.Hints?.Move);
            Assert.Null(host.Hints?.Resize);
            Assert.Null(host.Hints?.Distance);

            runtime.Tracking = Hands.RightOnly(Pointing(host.Placement, BesideBox, EmptyPartOfBox));
            host.PollInput(TimeSpan.FromMilliseconds(33));

            Assert.Null(host.Hints);
        });
    }

    [Fact]
    public void HoldingThePanelShowsHowToSizeAndPushItAndLightsThePillsWhileTheyAreDown()
    {
        AvaloniaTestHost.Run(() =>
        {
            var (host, runtime) = Build(OneCard());
            using var _ = host;

            runtime.Tracking = Hands.RightOnly(Pointing(host.Placement, InsideBoxAcross, EmptyPartOfBox, grab: true));
            host.PollInput(TimeSpan.Zero);
            Assert.Equal(Hand.Right, host.Holding);

            runtime.Tracking = Hands.RightOnly(Pointing(host.Placement, InsideBoxAcross, EmptyPartOfBox, grab: true));
            host.PollInput(TimeSpan.FromMilliseconds(33));

            Assert.Null(host.Hints?.Move);
            Assert.Equal(new ControlHint("Both grips", "larger / smaller"), host.Hints?.Resize);
            Assert.Equal(new ControlHint("Stick", "closer / farther"), host.Hints?.Distance);

            var held = Pointing(host.Placement, InsideBoxAcross, EmptyPartOfBox, grab: true);
            var other = Hands.Hand(Hands.AimingAt(new Vector3(-0.3f, 0, 0), new Vector3(-0.3f, 0, -1)), grab: true);
            runtime.Tracking = Hands.Both(other, held);
            host.PollInput(TimeSpan.FromMilliseconds(66));

            Assert.True(host.Hints?.Resize?.Lit);
        });
    }

    [Fact]
    public void TheHintsNameTheControllerTheRuntimeSays()
    {
        AvaloniaTestHost.Run(() =>
        {
            var (host, runtime) = Build(OneCard());
            using var _ = host;
            runtime.Controller = ControllerBindings.Simple;

            runtime.Tracking = Hands.RightOnly(Pointing(host.Placement, InsideBoxAcross, EmptyPartOfBox));
            host.PollInput(TimeSpan.Zero);

            Assert.Equal(new ControlHint("Menu button", "move"), host.Hints?.Move);
        });
    }
}
