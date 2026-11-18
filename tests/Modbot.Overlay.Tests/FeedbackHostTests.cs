using System.Numerics;
using Modbot.Companion.Overlay;
using Modbot.Overlay.Interaction;
using Modbot.Overlay.OpenVr;
using Modbot.Overlay.Rendering;
using Modbot.Overlay.Tests.Interaction;
using Modbot.Overlay.Views;

namespace Modbot.Overlay.Tests;

/// <summary>
/// The feedback panel's host: put away until a draft shows it, the bar with the lock and the hand in
/// edit mode, the spot that is saved, the two quick grips that send it home, and Cancel and Send.
/// </summary>
public class FeedbackHostTests
{
    private const int Size = FeedbackHost.DesignPixels;

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

    private static readonly FeedbackScreen Draft = new(null, "It froze", "The list froze when I scrolled.");

    private static (FeedbackHost Host, AttachedRuntime Runtime) Build(FeedbackScreen? screen = null, OverlayPlacement? placement = null)
    {
        var runtime = new AttachedRuntime();
        var host = new FeedbackHost(runtime, new FakeSurface(), new AvaloniaFrameRenderer(Size, Size), placement);
        runtime.Start();
        host.Start();
        host.Show(screen ?? Draft);
        return (host, runtime);
    }

    private static TimeSpan At(double seconds) => TimeSpan.FromSeconds(seconds);

    /// <summary>The middle of what is drawn at a target, as fractions across and down, or null when nothing is.</summary>
    private static (float Across, float Down)? Find(FeedbackHost host, Func<OverlayTarget?, bool> wanted)
    {
        var hits = new List<(float Across, float Down)>();
        for (var y = 0; y < 128; y++)
        {
            for (var x = 0; x < 128; x++)
            {
                var (across, down) = ((x + 0.5f) / 128f, (y + 0.5f) / 128f);
                if (wanted(host.TargetAt(across, down)))
                    hits.Add((across, down));
            }
        }

        return hits.Count == 0 ? null : (hits.Average(h => h.Across), hits.Average(h => h.Down));
    }

    /// <summary>Points at a spot on the panel, then clicks it.</summary>
    private static void Press(FeedbackHost host, AttachedRuntime runtime, (float Across, float Down) spot, double at)
    {
        var centre = host.Placement.Offset;
        var side = host.Placement.Side;
        var target = new Vector3(
            centre.X + ((spot.Across - 0.5f) * side),
            centre.Y - ((spot.Down - 0.5f) * side),
            centre.Z);
        var aim = Hands.AimingAt(Vector3.Zero, target);

        runtime.Tracking = Hands.RightOnly(Hands.Hand(aim));
        host.PollInput(At(at));
        runtime.Tracking = Hands.RightOnly(Hands.Hand(aim, click: true));
        host.PollInput(At(at + 0.5));
    }

    [Fact]
    public void ThePanelStartsPutAwayAndComesUpWhenShown()
    {
        AvaloniaTestHost.Run(() =>
        {
            var runtime = new AttachedRuntime();
            using var host = new FeedbackHost(runtime, new FakeSurface(), new AvaloniaFrameRenderer(Size, Size));

            Assert.True(host.Hidden);

            host.Show(Draft);

            Assert.False(host.Hidden);
        });
    }

    [Fact]
    public void TheBarHasTheLockThenTheHandUnderTheBoxInEditMode()
    {
        AvaloniaTestHost.Run(() =>
        {
            var (host, _) = Build();
            using var panel = host;
            host.EditMode = true;

            var padlock = Find(host, t => t is OverlayTarget.Lock);
            var hand = Find(host, t => t is OverlayTarget.ClickThrough);
            var close = Find(host, t => t is OverlayTarget.CloseFeedback);

            Assert.NotNull(padlock);
            Assert.NotNull(hand);
            Assert.NotNull(close);
            Assert.True(padlock.Value.Across < hand.Value.Across, "Lock, then the hand.");
            Assert.True(padlock.Value.Down > close.Value.Down, "The bar is under the box.");
        });
    }

    [Fact]
    public void ThePadlockLocksThePanelAndSavesItsSpot()
    {
        AvaloniaTestHost.Run(() =>
        {
            var (host, runtime) = Build();
            using var panel = host;
            host.EditMode = true;
            var saved = new List<OverlayPlacement>();
            host.PlacementChanged += saved.Add;

            Press(host, runtime, Find(host, t => t is OverlayTarget.Lock)!.Value, at: 0);

            Assert.True(host.Placement.Locked);
            Assert.True(saved[^1].Locked);
        });
    }

    [Fact]
    public void TheHandLetsRaysThroughAndSavesItsSpot()
    {
        AvaloniaTestHost.Run(() =>
        {
            var (host, runtime) = Build();
            using var panel = host;
            host.EditMode = true;
            var saved = new List<OverlayPlacement>();
            host.PlacementChanged += saved.Add;

            Press(host, runtime, Find(host, t => t is OverlayTarget.ClickThrough)!.Value, at: 0);

            Assert.True(host.Placement.ClickThrough);
            Assert.True(saved[^1].ClickThrough);
        });
    }

    [Fact]
    public void ThePanelOpensWhereItWasLeft()
    {
        AvaloniaTestHost.Run(() =>
        {
            var left = FeedbackHost.DefaultPlacement with { Offset = new OverlayPose(0.3f, 0.1f, -1.2f), Locked = true };
            var (host, _) = Build(placement: left);
            using var panel = host;

            Assert.Equal(left, host.Placement);
        });
    }

    [Fact]
    public void TwoQuickGripsBringAMovedPanelBackToItsOwnSpot()
    {
        AvaloniaTestHost.Run(() =>
        {
            var far = FeedbackHost.DefaultPlacement with { Offset = new OverlayPose(0, 0, -3), Width = 0.8f };
            var (host, runtime) = Build(placement: far);
            using var panel = host;
            var panelAt = new Vector3(0, 0, -3);
            var right = Hands.AimingAt(new Vector3(0.1f, 0, -1f), panelAt);
            var left = Hands.AimingAt(new Vector3(-0.2f, 0, 0f), panelAt);

            runtime.Tracking = Hands.Both(Hands.Hand(left, grab: true), Hands.Hand(right));
            host.PollInput(TimeSpan.FromMilliseconds(0));
            runtime.Tracking = Hands.Both(Hands.Hand(left), Hands.Hand(right));
            host.PollInput(TimeSpan.FromMilliseconds(100));
            runtime.Tracking = Hands.Both(Hands.Hand(left, grab: true), Hands.Hand(right));
            host.PollInput(TimeSpan.FromMilliseconds(300));

            Assert.Equal(FeedbackHost.DefaultPlacement.Offset, host.Placement.Offset);
            Assert.Null(host.Holding);
        });
    }

    [Fact]
    public void TheXAndCancelPutThePanelAwayAndRaiseCancelled()
    {
        AvaloniaTestHost.Run(() =>
        {
            foreach (var target in new Func<OverlayTarget?, bool>[] { t => t is OverlayTarget.CloseFeedback, t => t is OverlayTarget.FeedbackCancel })
            {
                var (host, runtime) = Build();
                using var panel = host;
                var cancelled = 0;
                var sent = 0;
                host.Cancelled += () => cancelled++;
                host.Sent += (_, _, _) => sent++;

                Press(host, runtime, Find(host, target)!.Value, at: 0);

                Assert.Equal(1, cancelled);
                Assert.Equal(0, sent);
                Assert.True(host.Hidden);
            }
        });
    }

    [Fact]
    public void SendRaisesSentWithTheKindThatWasPicked()
    {
        AvaloniaTestHost.Run(() =>
        {
            var (host, runtime) = Build();
            using var panel = host;
            var sent = new List<(string Kind, string Title, string Details)>();
            host.Sent += (kind, title, details) => sent.Add((kind, title, details));

            Press(host, runtime, Find(host, t => t is OverlayTarget.FeedbackKind { Kind: "bug" })!.Value, at: 0);
            Press(host, runtime, Find(host, t => t is OverlayTarget.FeedbackSend)!.Value, at: 2);

            Assert.Equal([("bug", "It froze", "The list froze when I scrolled.")], sent);
        });
    }

    [Fact]
    public void SendDoesNothingWithoutAKind()
    {
        AvaloniaTestHost.Run(() =>
        {
            var (host, runtime) = Build();
            using var panel = host;
            var sent = 0;
            host.Sent += (_, _, _) => sent++;

            Press(host, runtime, Find(host, t => t is OverlayTarget.FeedbackSend)!.Value, at: 0);

            Assert.Equal(0, sent);
            Assert.False(host.Hidden);
        });
    }

    [Fact]
    public void SendDoesNothingWithoutWords()
    {
        AvaloniaTestHost.Run(() =>
        {
            var (host, runtime) = Build(new FeedbackScreen("idea", "Title", "  "));
            using var panel = host;
            var sent = 0;
            host.Sent += (_, _, _) => sent++;

            Press(host, runtime, Find(host, t => t is OverlayTarget.FeedbackSend)!.Value, at: 0);

            Assert.Equal(0, sent);
        });
    }

    [Fact]
    public void WhileSendingCancelAndSendAreHeld()
    {
        AvaloniaTestHost.Run(() =>
        {
            var (host, runtime) = Build(new FeedbackScreen("bug", "Title", "Words"));
            using var panel = host;
            host.SetSending(true);
            var sent = 0;
            var cancelled = 0;
            host.Sent += (_, _, _) => sent++;
            host.Cancelled += () => cancelled++;

            Press(host, runtime, Find(host, t => t is OverlayTarget.FeedbackSend)!.Value, at: 0);
            Press(host, runtime, Find(host, t => t is OverlayTarget.FeedbackCancel)!.Value, at: 2);

            Assert.Equal(0, sent);
            Assert.Equal(0, cancelled);
            Assert.False(host.Hidden);
        });
    }
}
