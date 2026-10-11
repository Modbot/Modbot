using System.Numerics;
using Modbot.Companion.Overlay;
using Modbot.Overlay.Interaction;
using Modbot.Overlay.OpenVr;
using Modbot.Overlay.Rendering;
using Modbot.Overlay.Tests.Interaction;

namespace Modbot.Overlay.Tests;

/// <summary>
/// The headset panel's hidden state: it is a flag the host keeps, so a reattach does not undo it,
/// showing and putting back clear it, and a hidden panel takes no pointing.
/// </summary>
public class OverlayHostHiddenTests
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

    /// <summary>
    /// Attached like SteamVR: starting it shows the overlay, as creating an overlay does, and the
    /// runtime remembers whether it is currently showing.
    /// </summary>
    private sealed class AttachedRuntime : IOverlayRuntime
    {
        public OverlayRuntimeStatus Status { get; private set; } = new(OverlayRuntimeState.NotStarted);

        public OverlayTracking Tracking { get; set; } = OverlayTracking.None;

        public bool IsShowing { get; private set; }

        public int ReadsOfTracking { get; private set; }

        public OverlayRuntimeStatus Start()
        {
            IsShowing = true;
            return Status = new(OverlayRuntimeState.Running, Detail: "Attached to a test.");
        }

        /// <summary>The VR runtime going away: the next Start creates the overlay afresh.</summary>
        public void Detach() => Status = new(OverlayRuntimeState.NotStarted);

        public void Poll()
        {
        }

        public bool Submit(IOverlaySurface surface) => true;

        public void Show() => IsShowing = true;

        public void Hide() => IsShowing = false;

        public OverlayTracking ReadTracking()
        {
            ReadsOfTracking++;
            return Tracking;
        }

        public void Place(OverlayPlacement placement)
        {
        }

        public void Dispose()
        {
        }
    }

    private static (OverlayHost Host, AttachedRuntime Runtime) Build()
    {
        var runtime = new AttachedRuntime();
        var host = new OverlayHost(runtime, new FakeSurface(), new AvaloniaFrameRenderer(Size, Size));
        host.Start();
        return (host, runtime);
    }

    [Fact]
    public void HidingTakesDownThePanelAndShowingBringsItBack()
    {
        AvaloniaTestHost.Run(() =>
        {
            var (host, runtime) = Build();
            using var panel = host;

            Assert.False(host.Hidden);

            host.Hide();
            Assert.True(host.Hidden);
            Assert.False(runtime.IsShowing);

            host.Show();
            Assert.False(host.Hidden);
            Assert.True(runtime.IsShowing);
        });
    }

    [Fact]
    public void AHiddenPanelStaysHiddenWhenTheRuntimeComesBack()
    {
        AvaloniaTestHost.Run(() =>
        {
            var (host, runtime) = Build();
            using var panel = host;

            host.Hide();
            runtime.Detach();
            host.Start();

            // The runtime shows a freshly made overlay; the host puts the curtain back.
            Assert.True(host.Hidden);
            Assert.False(runtime.IsShowing);
        });
    }

    [Fact]
    public void PuttingThePanelBackShowsItAgain()
    {
        AvaloniaTestHost.Run(() =>
        {
            var (host, runtime) = Build();
            using var panel = host;

            host.Hide();
            host.PutBack(OverlayPlacement.StraightAhead);

            Assert.False(host.Hidden);
            Assert.True(runtime.IsShowing);
        });
    }

    [Fact]
    public void AHiddenPanelTakesNoPointing()
    {
        AvaloniaTestHost.Run(() =>
        {
            var (host, runtime) = Build();
            using var panel = host;
            var taps = 0;
            host.Tapped += _ => taps++;

            var centre = new Vector3(0.35f, -0.28f, -1.0f);
            runtime.Tracking = Hands.RightOnly(Hands.Hand(Hands.AimingAt(Vector3.Zero, centre), click: true));

            host.Hide();
            host.PollInput(TimeSpan.Zero);

            Assert.Null(host.Busy);
            Assert.Equal(0, runtime.ReadsOfTracking);
            Assert.Equal(0, taps);
        });
    }
}
