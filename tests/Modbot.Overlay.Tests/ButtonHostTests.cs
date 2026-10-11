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

    /// <summary>A ray that points away from every panel.</summary>
    private static Pose AimedAway() => Hands.AimingAt(Vector3.Zero, new Vector3(0f, 5f, -1f));

    private static OverlayTracking Sticks(Vector2 left, Vector2 right)
        => Hands.Both(Hands.Hand(AimedAway(), scroll: left), Hands.Hand(AimedAway(), scroll: right));

    private static readonly Vector2 PulledBack = new(0f, -1f);

    private static TimeSpan At(double seconds) => TimeSpan.FromSeconds(seconds);

    [Fact]
    public void TheRightStickHeldBackForFiveSecondsRaisesTheShortcutOnceAndCountsDownOnTheFace()
    {
        AvaloniaTestHost.Run(() =>
        {
            var (host, runtime) = Build();
            using var button = host;
            var held = 0;
            host.ShortcutHeld += () => held++;
            runtime.Tracking = Sticks(Vector2.Zero, PulledBack);

            host.PollInput(At(0));
            Assert.Equal(5, host.Counting?.SecondsLeft);

            host.PollInput(At(2.5));
            Assert.Equal(3, host.Counting?.SecondsLeft);
            Assert.Equal(0, held);

            host.PollInput(At(5));
            Assert.Equal(1, held);
            Assert.Null(host.Counting);

            host.PollInput(At(9));
            Assert.Equal(1, held);

            runtime.Tracking = Sticks(Vector2.Zero, Vector2.Zero);
            host.PollInput(At(10));
            runtime.Tracking = Sticks(Vector2.Zero, PulledBack);
            host.PollInput(At(11));
            host.PollInput(At(16));
            Assert.Equal(2, held);
        });
    }

    [Fact]
    public void TheFaceCountsWhileTheStickIsHeldAndGoesBackToTheShortcutWhenItIsLetGo()
    {
        AvaloniaTestHost.Run(() =>
        {
            var (host, runtime) = Build();
            using var button = host;
            var drawn = host.FramesDrawn;
            runtime.Tracking = Sticks(Vector2.Zero, PulledBack);

            host.PollInput(At(0));
            Assert.True(host.FramesDrawn > drawn, "The count is drawn.");

            runtime.Tracking = Sticks(Vector2.Zero, Vector2.Zero);
            host.PollInput(At(2));

            Assert.Null(host.Counting);
            Assert.Equal(OverlayButton.DefaultShortcut, host.Showing.Line);
        });
    }

    [Fact]
    public void TheOtherStickAndTheOtherWayDoNothing()
    {
        AvaloniaTestHost.Run(() =>
        {
            var (host, runtime) = Build();
            using var button = host;
            var held = 0;
            host.ShortcutHeld += () => held++;

            runtime.Tracking = Sticks(PulledBack, Vector2.Zero);
            host.PollInput(At(0));
            host.PollInput(At(6));

            runtime.Tracking = Sticks(Vector2.Zero, new Vector2(0f, 1f));
            host.PollInput(At(7));
            host.PollInput(At(14));

            Assert.Equal(0, held);
            Assert.Null(host.Counting);
        });
    }

    [Fact]
    public void TheSettingsChooseTheStickTheWayAndTheTime()
    {
        AvaloniaTestHost.Run(() =>
        {
            var (host, runtime) = Build();
            using var button = host;
            var held = 0;
            host.ShortcutHeld += () => held++;
            host.Shortcut = new ButtonShortcut(ShortcutStick.Left, StickDirection.Forward, 2);

            runtime.Tracking = Sticks(new Vector2(0f, 1f), PulledBack);
            host.PollInput(At(0));
            Assert.Equal(2, host.Counting?.SecondsLeft);

            host.PollInput(At(2));
            Assert.Equal(1, held);
        });
    }

    [Fact]
    public void WithTheShortcutOffNothingCountsAndNothingIsRaised()
    {
        AvaloniaTestHost.Run(() =>
        {
            var (host, runtime) = Build();
            using var button = host;
            var held = 0;
            host.ShortcutHeld += () => held++;
            host.Shortcut = ButtonShortcut.Default with { Stick = ShortcutStick.Off };

            runtime.Tracking = Sticks(PulledBack, PulledBack);
            host.PollInput(At(0));
            host.PollInput(At(10));

            Assert.Equal(0, held);
            Assert.Null(host.Counting);
        });
    }

    [Fact]
    public void AStickThatIsCarryingOrScrollingAnotherPanelIsLeftOut()
    {
        AvaloniaTestHost.Run(() =>
        {
            var (host, runtime) = Build();
            using var button = host;
            var held = 0;
            host.ShortcutHeld += () => held++;
            runtime.Tracking = Sticks(Vector2.Zero, PulledBack);

            host.PollInput(At(0), stickInUse: _ => true);
            host.PollInput(At(6), stickInUse: _ => true);
            Assert.Equal(0, held);
            Assert.Null(host.Counting);

            // Free again with the stick still back: it waits for the middle first.
            host.PollInput(At(7));
            host.PollInput(At(14));
            Assert.Equal(0, held);

            runtime.Tracking = Sticks(Vector2.Zero, Vector2.Zero);
            host.PollInput(At(15));
            runtime.Tracking = Sticks(Vector2.Zero, PulledBack);
            host.PollInput(At(16));
            host.PollInput(At(21));
            Assert.Equal(1, held);
        });
    }

    [Fact]
    public void AStickOnTheHandPointingAtTheButtonIsLeftOut()
    {
        AvaloniaTestHost.Run(() =>
        {
            var (host, runtime) = Build();
            using var button = host;
            var held = 0;
            host.ShortcutHeld += () => held++;

            runtime.Tracking = Hands.RightOnly(Hands.Hand(Aim(host), scroll: PulledBack));
            host.PollInput(At(0));
            host.PollInput(At(6));

            Assert.Equal(Hand.Right, host.Busy);
            Assert.Equal(0, held);
            Assert.Null(host.Counting);
        });
    }

    [Fact]
    public void ChangingTheShortcutDropsTheCountUntilTheStickHasBeenLetGo()
    {
        AvaloniaTestHost.Run(() =>
        {
            var (host, runtime) = Build();
            using var button = host;
            runtime.Tracking = Sticks(Vector2.Zero, PulledBack);

            host.PollInput(At(0));
            Assert.NotNull(host.Counting);

            host.Shortcut = host.Shortcut with { Seconds = 8 };
            Assert.Null(host.Counting);

            host.PollInput(At(1));
            Assert.Null(host.Counting);
        });
    }
}
