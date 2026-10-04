using Avalonia;
using Modbot.Companion.Overlay;
using Modbot.Companion.Presentation;
using Modbot.Companion.Sounds;
using Modbot.Overlay.OpenVr;
using Modbot.Overlay.Rendering;
using Modbot.Overlay.Views;

namespace Modbot.Overlay.Tests;

/// <summary>
/// The dashboard tab's host: a press from SteamVR's laser becomes the setting it asks for, raised
/// for the companion to save, and nothing is changed behind the companion's back.
/// </summary>
public class DashboardHostTests
{
    private sealed class FakeSurface(List<string> calls) : IOverlaySurface
    {
        public int Width => DashboardHost.PageWidth;

        public void Flush() => calls.Add("flush");

        public int Height => DashboardHost.PageHeight;

        public nint TextureHandle => 1;

        public ReadOnlyMemory<byte> Pixels => ReadOnlyMemory<byte>.Empty;

        public void Upload(ReadOnlySpan<byte> bgra)
        {
        }

        public void Dispose()
        {
        }
    }

    private sealed class FakeRuntime : IDashboardRuntime
    {
        public List<DashboardPointer> Waiting { get; } = [];

        /// <summary>What was asked of SteamVR and of the surface, in order.</summary>
        public List<string> Calls { get; } = [];

        public int Submissions { get; private set; }

        public int Thumbnails { get; private set; }

        public OverlayRuntimeStatus Status { get; set; } = new(OverlayRuntimeState.NotStarted);

        public OverlayRuntimeStatus Start() => Status = new(OverlayRuntimeState.Running);

        public void Poll()
        {
        }

        public IReadOnlyList<DashboardPointer> TakePointer()
        {
            var taken = Waiting.ToArray();
            Waiting.Clear();
            return taken;
        }

        public bool Submit(IOverlaySurface surface)
        {
            Calls.Add("submit");
            Submissions++;
            return true;
        }

        public bool SubmitThumbnail(IOverlaySurface surface)
        {
            Thumbnails++;
            return true;
        }

        public void Dispose()
        {
        }
    }

    private static DashboardHost Host(FakeRuntime runtime)
        => new(runtime, new FakeSurface(runtime.Calls), new AvaloniaFrameRenderer(DashboardHost.PageWidth, DashboardHost.PageHeight));

    private static Point Middle(DashboardHost host, DashboardTarget target) => FindOrFail(host, target).Center;

    private static Rect FindOrFail(DashboardHost host, DashboardTarget target)
    {
        var placed = host.Targets.Where(p => p.Target == target).ToList();
        Assert.Single(placed);

        // What a press in its middle finds is the control itself, not something drawn over it.
        Assert.Equal(target, host.TargetAt(placed[0].Bounds.Center.X, placed[0].Bounds.Center.Y)?.Target);
        return placed[0].Bounds;
    }

    private static void Press(DashboardHost host, Point at)
    {
        host.Handle(new DashboardPointer(DashboardPointerKind.Down, at.X, at.Y));
        host.Handle(new DashboardPointer(DashboardPointerKind.Up, at.X, at.Y));
    }

    [Fact]
    public void TheOverlaySwitchAsksForTheOtherWay()
    {
        AvaloniaTestHost.Run(() =>
        {
            using var host = Host(new FakeRuntime());
            host.Update(DashboardScreen.Default);

            bool? asked = null;
            host.OverlayOnChanged += on => asked = on;

            Press(host, Middle(host, new DashboardTarget.Toggle(DashboardSwitch.Overlay)));

            Assert.False(asked);
        });
    }

    [Fact]
    public void ChoosingAnAnchorAsksForIt()
    {
        AvaloniaTestHost.Run(() =>
        {
            using var host = Host(new FakeRuntime());
            host.Update(DashboardScreen.Default);

            OverlayAnchor? asked = null;
            host.AnchorChosen += anchor => asked = anchor;

            Press(host, Middle(host, new DashboardTarget.FixTo(OverlayAnchor.LeftHand)));

            Assert.Equal(OverlayAnchor.LeftHand, asked);
        });
    }

    [Fact]
    public void ChoosingASpotPutsThePopUpsBackOnIt()
    {
        AvaloniaTestHost.Run(() =>
        {
            using var host = Host(new FakeRuntime());
            host.Update(DashboardScreen.Default with
            {
                Notify = NotifyOverlaySettings.Default with { Placed = new OverlayPose(0.1f, 0.1f, -1f) },
            });

            NotifyOverlaySettings? asked = null;
            host.NotifyOverlayChanged += settings => asked = settings;

            Press(host, Middle(host, new DashboardTarget.Spot(ScreenSpot.BottomMiddle)));

            Assert.NotNull(asked);
            Assert.Equal(ScreenSpot.BottomMiddle, asked.Spot);
            Assert.Null(asked.Placed);
        });
    }

    [Fact]
    public void APopUpTickChangesOnlyThePopUpColumn()
    {
        AvaloniaTestHost.Run(() =>
        {
            using var host = Host(new FakeRuntime());
            host.Update(DashboardScreen.Default);

            NotificationFilters? asked = null;
            host.FiltersChanged += filters => asked = filters;

            Press(host, Middle(host, new DashboardTarget.PopUp(NotificationKind.Joined)));

            Assert.NotNull(asked);
            Assert.True(asked.PopUpShows(NotificationKind.Joined));
            Assert.Equal(NotificationFilters.Default.For(NotificationWay.Sound), asked.For(NotificationWay.Sound));
            Assert.Equal(NotificationFilters.Default.For(NotificationWay.Voice), asked.For(NotificationWay.Voice));
        });
    }

    [Fact]
    public void DraggingATrackFollowsTheLaserUntilTheTriggerComesUp()
    {
        AvaloniaTestHost.Run(() =>
        {
            using var host = Host(new FakeRuntime());
            host.Update(DashboardScreen.Default);

            var asked = new List<NotifyOverlaySettings>();
            host.NotifyOverlayChanged += settings =>
            {
                asked.Add(settings);
                host.Update(host.Showing with { Notify = settings });
            };

            var track = FindOrFail(host, new DashboardTarget.Track(DashboardSlider.Seconds));
            var y = track.Center.Y;

            host.Handle(new DashboardPointer(DashboardPointerKind.Down, track.Left, y));
            host.Handle(new DashboardPointer(DashboardPointerKind.Move, track.Right, y));
            host.Handle(new DashboardPointer(DashboardPointerKind.Up, track.Right, y));
            host.Handle(new DashboardPointer(DashboardPointerKind.Move, track.Left, y));

            Assert.Equal(NotifyOverlaySettings.MinSeconds, asked[0].Seconds, 3);
            Assert.Equal(NotifyOverlaySettings.MaxSeconds, asked[^1].Seconds, 3);
            Assert.Equal(2, asked.Count);
        });
    }

    [Fact]
    public void TheLaserMovingWithNothingHeldChangesNothing()
    {
        AvaloniaTestHost.Run(() =>
        {
            using var host = Host(new FakeRuntime());
            host.Update(DashboardScreen.Default);

            var raised = 0;
            host.NotifyOverlayChanged += _ => raised++;
            host.OverlayOnChanged += _ => raised++;

            var track = FindOrFail(host, new DashboardTarget.Track(DashboardSlider.Width));
            host.Handle(new DashboardPointer(DashboardPointerKind.Move, track.Right, track.Center.Y));

            Assert.Equal(0, raised);
        });
    }

    [Fact]
    public void TheSameSettingsAgainDrawNothing()
    {
        AvaloniaTestHost.Run(() =>
        {
            var runtime = new FakeRuntime();
            using var host = Host(runtime);

            Assert.True(host.Update(DashboardScreen.Default));
            Assert.False(host.Update(DashboardScreen.Default with { }));
            Assert.Equal(1, runtime.Submissions);

            Assert.True(host.Update(DashboardScreen.Default with { OverlayOn = false }));
            Assert.Equal(2, runtime.Submissions);
        });
    }

    [Fact]
    public void EachRedrawSaysHowLongItTook()
    {
        AvaloniaTestHost.Run(() =>
        {
            using var host = Host(new FakeRuntime());
            Assert.Null(host.LastDraw);

            Assert.True(host.Update(DashboardScreen.Default));
            Assert.NotNull(host.LastDraw);
            Assert.True(host.LastDraw.Value.Drawing > TimeSpan.Zero);
            Assert.True(host.LastDraw.Value.Handing >= TimeSpan.Zero);
            Assert.True(host.LastDraw.Value.Flushing >= TimeSpan.Zero);
        });
    }

    [Fact]
    public void EachHandOverIsFlushedStraightAfter()
    {
        AvaloniaTestHost.Run(() =>
        {
            var runtime = new FakeRuntime();
            using var host = Host(runtime);

            host.Update(DashboardScreen.Default);
            host.Update(DashboardScreen.Default);
            host.Update(DashboardScreen.Default with { OverlayOn = false });

            // One hand-over per changed screen, each followed by its flush; the same screen again
            // hands over nothing and flushes nothing.
            Assert.Equal(new[] { "submit", "flush", "submit", "flush" }, runtime.Calls);
        });
    }

    [Fact]
    public void APressIsHeardWithHowLongSteamVrHeldIt()
    {
        AvaloniaTestHost.Run(() =>
        {
            var runtime = new FakeRuntime();
            using var host = Host(runtime);
            host.Start();
            host.Update(DashboardScreen.Default);

            var heard = new List<DashboardPointer>();
            host.PressHeard += heard.Add;

            var at = Middle(host, new DashboardTarget.Toggle(DashboardSwitch.Overlay));
            runtime.Waiting.Add(new DashboardPointer(DashboardPointerKind.Move, at.X, at.Y));
            runtime.Waiting.Add(new DashboardPointer(DashboardPointerKind.Down, at.X, at.Y, TimeSpan.FromMilliseconds(40)));
            runtime.Waiting.Add(new DashboardPointer(DashboardPointerKind.Up, at.X, at.Y));
            host.Poll();

            var press = Assert.Single(heard);
            Assert.Equal(TimeSpan.FromMilliseconds(40), press.Age);
        });
    }

    [Fact]
    public void OfManyMovesReadInOnePollOnlyTheLastMovesTheSlider()
    {
        AvaloniaTestHost.Run(() =>
        {
            var runtime = new FakeRuntime();
            using var host = Host(runtime);
            host.Start();
            host.Update(DashboardScreen.Default);

            var asked = new List<NotifyOverlaySettings>();
            host.NotifyOverlayChanged += settings =>
            {
                asked.Add(settings);
                host.Update(host.Showing with { Notify = settings });
            };

            var track = FindOrFail(host, new DashboardTarget.Track(DashboardSlider.Seconds));
            var y = track.Center.Y;

            runtime.Waiting.Add(new DashboardPointer(DashboardPointerKind.Down, track.Left, y));
            host.Poll();
            Assert.Single(asked);

            // Four moves between two polls: the slider goes straight to where the last one was.
            runtime.Waiting.Add(new DashboardPointer(DashboardPointerKind.Move, track.Left + (track.Width * 0.25), y));
            runtime.Waiting.Add(new DashboardPointer(DashboardPointerKind.Move, track.Left + (track.Width * 0.5), y));
            runtime.Waiting.Add(new DashboardPointer(DashboardPointerKind.Move, track.Left + (track.Width * 0.75), y));
            runtime.Waiting.Add(new DashboardPointer(DashboardPointerKind.Move, track.Right, y));
            host.Poll();

            Assert.Equal(2, asked.Count);
            Assert.Equal(NotifyOverlaySettings.MaxSeconds, asked[^1].Seconds, 3);

            // A move followed by the release is still acted on before the trigger comes up.
            var drawn = host.FramesDrawn;
            runtime.Waiting.Add(new DashboardPointer(DashboardPointerKind.Move, track.Left, y));
            runtime.Waiting.Add(new DashboardPointer(DashboardPointerKind.Up, track.Left, y));
            host.Poll();

            Assert.Equal(3, asked.Count);
            Assert.Equal(NotifyOverlaySettings.MinSeconds, asked[^1].Seconds, 3);
            Assert.Equal(drawn + 1, host.FramesDrawn);
        });
    }

    [Fact]
    public void StartingHandsSteamVrThePageAndTheButtonsPicture()
    {
        AvaloniaTestHost.Run(() =>
        {
            var runtime = new FakeRuntime();
            using var host = Host(runtime);
            host.Update(DashboardScreen.Default);

            Assert.Equal(OverlayRuntimeState.Running, host.Start().State);
            Assert.Equal(1, runtime.Thumbnails);
            Assert.Equal(2, runtime.Submissions);
        });
    }

    [Fact]
    public void ThePointerSteamVrSendsIsActedOnAtTheNextPoll()
    {
        AvaloniaTestHost.Run(() =>
        {
            var runtime = new FakeRuntime();
            using var host = Host(runtime);
            host.Start();
            host.Update(DashboardScreen.Default);

            bool? asked = null;
            host.OverlayOnChanged += on => asked = on;

            var at = Middle(host, new DashboardTarget.Toggle(DashboardSwitch.Overlay));
            runtime.Waiting.Add(new DashboardPointer(DashboardPointerKind.Down, at.X, at.Y));
            Assert.Null(asked);

            host.Poll();
            Assert.False(asked);
        });
    }
}
