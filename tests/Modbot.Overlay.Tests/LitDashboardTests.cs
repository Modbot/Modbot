using Avalonia;
using Modbot.Companion.Overlay;
using Modbot.Overlay.OpenVr;
using Modbot.Overlay.Rendering;
using Modbot.Overlay.Views;

namespace Modbot.Overlay.Tests;

/// <summary>
/// The dashboard tab: the control SteamVR's laser is on lights, a laser that stays on it costs no
/// frame, and the laser leaving puts it out.
/// </summary>
public class LitDashboardTests
{
    private sealed class FakeSurface : IOverlaySurface
    {
        public int Width => DashboardHost.PageWidth;

        public int Height => DashboardHost.PageHeight;

        public nint TextureHandle => 1;

        public ReadOnlyMemory<byte> Pixels => ReadOnlyMemory<byte>.Empty;

        public void Flush()
        {
        }

        public void Upload(ReadOnlySpan<byte> bgra)
        {
        }

        public void Dispose()
        {
        }
    }

    private sealed class FakeRuntime : IDashboardRuntime
    {
        public OverlayRuntimeStatus Status { get; set; } = new(OverlayRuntimeState.NotStarted);

        public OverlayRuntimeStatus Start() => Status = new(OverlayRuntimeState.Running);

        public void Poll()
        {
        }

        public IReadOnlyList<DashboardPointer> TakePointer() => [];

        public bool Submit(IOverlaySurface surface) => true;

        public bool SubmitThumbnail(IOverlaySurface surface) => true;

        public void Dispose()
        {
        }
    }

    private static DashboardHost Host()
        => new(new FakeRuntime(), new FakeSurface(), new AvaloniaFrameRenderer(DashboardHost.PageWidth, DashboardHost.PageHeight));

    private static Point Middle(DashboardHost host, DashboardTarget target)
    {
        var placed = host.Targets.Single(p => p.Target == target);
        return placed.Bounds.Center;
    }

    private static void Move(DashboardHost host, Point at)
        => host.Handle(new DashboardPointer(DashboardPointerKind.Move, at.X, at.Y));

    [Fact]
    public void AMoveOntoAControlLightsItAndTheLaserLeavingPutsItOut()
    {
        AvaloniaTestHost.Run(() =>
        {
            using var host = Host();
            host.Update(DashboardScreen.Default);

            Move(host, Middle(host, new DashboardTarget.Toggle(DashboardSwitch.Overlay)));
            Assert.Equal(new DashboardTarget.Toggle(DashboardSwitch.Overlay), host.LitUp);

            host.Handle(new DashboardPointer(DashboardPointerKind.Gone, 0, 0));
            Assert.Null(host.LitUp);
        });
    }

    [Fact]
    public void EachKindOfControlOnTheTabLights()
    {
        AvaloniaTestHost.Run(() =>
        {
            using var host = Host();
            host.Update(DashboardScreen.Default);

            var kinds = new Func<DashboardTarget, bool>[]
            {
                t => t is DashboardTarget.Toggle,
                t => t is DashboardTarget.FixTo,
                t => t is DashboardTarget.PutBack,
                t => t is DashboardTarget.Spot,
                t => t is DashboardTarget.PopUp,
                t => t is DashboardTarget.Step,
                t => t is DashboardTarget.Track,
            };

            foreach (var kind in kinds)
            {
                var target = host.Targets.First(p => kind(p.Target)).Target;
                Move(host, Middle(host, target));
                Assert.Equal(target, host.LitUp);
            }
        });
    }

    [Fact]
    public void ALaserStayingOnOneControlCostsNoFrameAndAnotherControlCostsOne()
    {
        AvaloniaTestHost.Run(() =>
        {
            using var host = Host();
            host.Update(DashboardScreen.Default);

            var bounds = host.Targets.Single(p => p.Target == new DashboardTarget.Toggle(DashboardSwitch.Overlay)).Bounds;
            Move(host, bounds.Center);
            var frames = host.FramesDrawn;

            // Moves inside the same control: nothing to draw.
            Move(host, new Point(bounds.X + 2, bounds.Y + 2));
            Move(host, new Point(bounds.Right - 2, bounds.Bottom - 2));
            Assert.Equal(frames, host.FramesDrawn);

            Move(host, Middle(host, new DashboardTarget.Toggle(DashboardSwitch.Button)));
            Assert.Equal(frames + 1, host.FramesDrawn);

            // Off every control: the lit one goes out, once.
            Move(host, new Point(1, 1));
            Assert.Null(host.LitUp);
            Assert.Equal(frames + 2, host.FramesDrawn);

            Move(host, new Point(2, 2));
            Assert.Equal(frames + 2, host.FramesDrawn);
        });
    }

    [Fact]
    public void LettingGoAwayFromTheTrackPutsItOut()
    {
        AvaloniaTestHost.Run(() =>
        {
            using var host = Host();
            host.Update(DashboardScreen.Default);

            var track = host.Targets.First(p => p.Target is DashboardTarget.Track).Target;
            var at = Middle(host, track);
            Move(host, at);
            host.Handle(new DashboardPointer(DashboardPointerKind.Down, at.X, at.Y));
            Assert.Equal(track, host.LitUp);

            host.Handle(new DashboardPointer(DashboardPointerKind.Up, 1, 1));
            Assert.Null(host.LitUp);
        });
    }
}
