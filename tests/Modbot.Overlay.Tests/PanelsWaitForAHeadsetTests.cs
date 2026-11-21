using Avalonia.Controls;
using Modbot.Companion.Overlay;
using Modbot.Overlay.Interaction;
using Modbot.Overlay.OpenVr;
using Modbot.Overlay.Rendering;
using Modbot.Overlay.Views;

namespace Modbot.Overlay.Tests;

/// <summary>
/// Neither panel builds its renderer or its texture until a headset runtime has actually
/// attached, and both give them back when one goes away.
/// </summary>
/// <remarks>
/// <para><strong>Why this is worth a test class of its own.</strong> Most machines running the
/// companion never plug a headset in, and each panel used to make a Direct3D device and a texture
/// the moment its switch was on — measured at about forty megabytes and forty threads for a
/// picture nobody could see. The switches did not change; what changed is when the work happens.
/// </para>
/// <para><strong>The case that can break quietly</strong> is a headset started in the middle of a
/// session. Nothing crashes if it is broken: the panel simply never appears, which looks to a
/// moderator like the overlay not working rather than like a bug. So it is checked twice here —
/// once for a first attach, and once for an attach after the headset went away and came back.
/// </para>
/// <para>Nothing here touches a real graphics card or a real runtime, so it runs the same on a
/// machine with SteamVR and on one without.</para>
/// </remarks>
public class PanelsWaitForAHeadsetTests
{
    private const int Size = 64;

    /// <summary>A runtime whose answer the test sets, and which can close itself on a poll.</summary>
    private sealed class ScriptedRuntime : IOverlayRuntime
    {
        public OverlayRuntimeStatus Answer { get; set; } = new(OverlayRuntimeState.NotStarted, Detail: "Not running.");

        public OverlayRuntimeStatus Status { get; private set; } = new(OverlayRuntimeState.NotStarted, Detail: "Not looked yet.");

        public int Submissions { get; private set; }

        public bool CloseOnPoll { get; set; }

        public OverlayRuntimeStatus Start() => Status = Answer;

        public void Poll()
        {
            if (CloseOnPoll)
                Status = Answer = new(OverlayRuntimeState.NotStarted, Detail: "Closed.");
        }

        public bool Submit(IOverlaySurface surface)
        {
            Submissions++;
            return true;
        }

        public void Show()
        {
        }

        public void Hide()
        {
        }

        public OverlayTracking ReadTracking() => OverlayTracking.None;

        public void Place(OverlayPlacement placement)
        {
        }

        public void Dispose()
        {
        }
    }

    /// <summary>A surface that counts how many were ever made and how many are still alive.</summary>
    private sealed class CountedSurface : IOverlaySurface
    {
        public static int Made { get; private set; }

        public static int Alive { get; private set; }

        public static void Forget() => Made = Alive = 0;

        public CountedSurface()
        {
            Made++;
            Alive++;
        }

        public int Width => Size;

        public int Height => Size;

        public nint TextureHandle => 1;

        public ReadOnlyMemory<byte> Pixels => ReadOnlyMemory<byte>.Empty;

        public int Uploads { get; private set; }

        public void Upload(ReadOnlySpan<byte> bgra) => Uploads++;

        public void Dispose() => Alive--;
    }

    /// <summary>A renderer that counts the same way, over a real Avalonia one.</summary>
    private sealed class CountedRenderer : IFrameRenderer
    {
        public static int Made { get; private set; }

        public static int Alive { get; private set; }

        public static void Forget() => Made = Alive = 0;

        private readonly AvaloniaFrameRenderer _inner = new(Size, Size);

        public CountedRenderer()
        {
            Made++;
            Alive++;
        }

        public int Width => _inner.Width;

        public int Height => _inner.Height;

        public ReadOnlySpan<byte> Render(Control root) => _inner.Render(root);

        public void Dispose()
        {
            Alive--;
            _inner.Dispose();
        }
    }

    private static void Forget()
    {
        CountedSurface.Forget();
        CountedRenderer.Forget();
    }

    private static OverlayHost Panel(ScriptedRuntime runtime)
        => new(runtime, Size, () => new CountedRenderer(), () => new CountedSurface());

    private static NotificationHost PopUps(ScriptedRuntime runtime)
        => new(runtime, () => new CountedRenderer(), () => new CountedSurface());

    private static OverlayScreen Roster(string name) => new(
        "Cat Lounge",
        new Cached<InstanceContext>(
            new InstanceContext("39911", [new RosterMember("usr_1", name, RosterStanding.Member, 0, [])]),
            Freshness.Fresh,
            TimeSpan.Zero),
        Freshness.Fresh);

    private static NotificationScreen PopUp(string id) => new(
        [new PopUp(id, "Cat Lounge", "Somebody", null, PopUpTone.Flagged)]);

    [Fact]
    public void ThePanelMakesNoTextureWhileNoHeadsetIsRunning()
    {
        AvaloniaTestHost.Run(() =>
        {
            Forget();
            var runtime = new ScriptedRuntime();
            using var host = Panel(runtime);

            // The switch is on and the drive loop is pushing screens at it. On a PC with no
            // headset that is the whole of the session, and it must cost nothing.
            host.Start();
            Assert.False(host.IsDrawing);

            Assert.False(host.Update(Roster("Rin")));
            Assert.False(host.Update(Roster("Kai")));

            Assert.Equal(0, CountedSurface.Made);
            Assert.Equal(0, CountedRenderer.Made);
            Assert.Equal(0, host.FramesDrawn);
            Assert.Equal(0, runtime.Submissions);
        });
    }

    [Fact]
    public void AHeadsetStartedMidSessionGetsThePanelItWouldHaveHad()
    {
        AvaloniaTestHost.Run(() =>
        {
            Forget();
            var runtime = new ScriptedRuntime();
            using var host = Panel(runtime);

            host.Start();
            host.Update(Roster("Rin"));
            Assert.Equal(0, host.FramesDrawn);

            // The moderator starts SteamVR. The companion's ten-second look calls Start again,
            // which is where the texture is made -- and the screen that was already pushed is
            // drawn, rather than the panel hanging blank until the roster next changes.
            runtime.Answer = new(OverlayRuntimeState.Running, Detail: "Attached.");
            host.Start();

            Assert.True(host.IsDrawing);
            Assert.Equal(1, CountedSurface.Made);
            Assert.Equal(1, CountedRenderer.Made);
            Assert.Equal(1, host.FramesDrawn);
            Assert.Equal(1, runtime.Submissions);
            Assert.Equal("Rin", host.Showing.Roster.Value?.Members[0].DisplayName);
        });
    }

    [Fact]
    public void AHeadsetThatGoesAwayGivesTheTextureBackAndComingBackMakesAnother()
    {
        AvaloniaTestHost.Run(() =>
        {
            Forget();
            var runtime = new ScriptedRuntime { Answer = new(OverlayRuntimeState.Running, Detail: "Attached.") };
            using var host = Panel(runtime);

            host.Start();
            host.Update(Roster("Rin"));
            Assert.True(host.IsDrawing);
            Assert.Equal(1, CountedSurface.Alive);

            // SteamVR closes. Leaving VR gives the memory back now rather than at the end of the
            // session.
            runtime.CloseOnPoll = true;
            host.Poll();

            Assert.False(host.IsDrawing);
            Assert.Equal(0, CountedSurface.Alive);
            Assert.Equal(0, CountedRenderer.Alive);
            Assert.Equal(0, host.FramesDrawn);

            // And it comes back. A new texture holds nothing, so the panel has to be drawn again
            // instead of being handed the old picture.
            runtime.CloseOnPoll = false;
            runtime.Answer = new(OverlayRuntimeState.Running, Detail: "Attached again.");
            host.Start();

            Assert.True(host.IsDrawing);
            Assert.Equal(2, CountedSurface.Made);
            Assert.Equal(1, CountedSurface.Alive);
            Assert.Equal(1, host.FramesDrawn);
            Assert.Equal("Rin", host.Showing.Roster.Value?.Members[0].DisplayName);
        });
    }

    [Fact]
    public void KeepingTheLastFrameDrawsWithNoHeadsetAtAll()
    {
        AvaloniaTestHost.Run(() =>
        {
            Forget();
            var runtime = new ScriptedRuntime();
            using var host = Panel(runtime);

            host.Update(Roster("Rin"));
            Assert.True(host.LastFrame.IsEmpty);

            // The Debug page's overlay window shows the very bytes the panel would hand a
            // compositor, so asking for them is asking for the drawing.
            host.KeepLastFrame = true;

            Assert.True(host.IsDrawing);
            Assert.Equal(Size * Size * 4, host.LastFrame.Length);

            // And it is not taken away again by a poll that finds no headset, which is every poll
            // on the machine this is used on.
            host.Poll();
            Assert.True(host.IsDrawing);
        });
    }

    [Fact]
    public void TheHostHandedASurfaceKeepsIt()
    {
        AvaloniaTestHost.Run(() =>
        {
            Forget();
            var runtime = new ScriptedRuntime();
            using var host = new OverlayHost(runtime, new CountedSurface(), new CountedRenderer());

            // Nothing was deferred, so nothing may be let go: the surface belongs to whoever
            // passed it in, and a poll with no headset must not dispose it.
            Assert.True(host.IsDrawing);
            Assert.True(host.Update(Roster("Rin")));

            host.Poll();

            Assert.True(host.IsDrawing);
            Assert.Equal(1, CountedSurface.Alive);
        });
    }

    [Fact]
    public void ThePopUpPanelMakesNoTextureWhileNoHeadsetIsRunning()
    {
        AvaloniaTestHost.Run(() =>
        {
            Forget();
            var runtime = new ScriptedRuntime();
            using var host = PopUps(runtime);

            host.Start();
            Assert.False(host.IsDrawing);

            Assert.False(host.Update(PopUp("a1")));

            Assert.Equal(0, CountedSurface.Made);
            Assert.Equal(0, CountedRenderer.Made);
            Assert.Equal(0, host.FramesDrawn);
            Assert.Equal(0, runtime.Submissions);
        });
    }

    [Fact]
    public void ThePopUpPanelDrawsWhatItWasHoldingWhenAHeadsetArrives()
    {
        AvaloniaTestHost.Run(() =>
        {
            Forget();
            var runtime = new ScriptedRuntime();
            using var host = PopUps(runtime);

            host.Start();
            host.Update(PopUp("a1"));
            Assert.Equal(0, host.FramesDrawn);

            runtime.Answer = new(OverlayRuntimeState.Running, Detail: "Attached.");
            host.Start();

            Assert.True(host.IsDrawing);
            Assert.Equal(1, host.FramesDrawn);
            Assert.Equal(1, runtime.Submissions);
            Assert.Equal("a1", host.Showing.PopUps[0].Id);
        });
    }

    [Fact]
    public void ThePopUpPanelGivesItsTextureBackWhenTheHeadsetGoes()
    {
        AvaloniaTestHost.Run(() =>
        {
            Forget();
            var runtime = new ScriptedRuntime { Answer = new(OverlayRuntimeState.Running, Detail: "Attached.") };
            using var host = PopUps(runtime);

            host.Start();
            host.Update(PopUp("a1"));
            Assert.Equal(1, CountedSurface.Alive);

            runtime.CloseOnPoll = true;
            host.Poll();

            Assert.False(host.IsDrawing);
            Assert.Equal(0, CountedSurface.Alive);
            Assert.Equal(0, CountedRenderer.Alive);

            runtime.CloseOnPoll = false;
            runtime.Answer = new(OverlayRuntimeState.Running, Detail: "Attached again.");
            host.Start();

            Assert.True(host.IsDrawing);
            Assert.Equal(2, CountedSurface.Made);
            Assert.Equal(1, host.FramesDrawn);
            Assert.Equal("a1", host.Showing.PopUps[0].Id);
        });
    }

    [Fact]
    public void BuildingTheOrdinaryPanelsMakesNoTextureYet()
    {
        AvaloniaTestHost.Run(() =>
        {
            // The construction the companion actually uses. Before, this made two Direct3D devices
            // and two textures on the spot; now it makes the runtimes and nothing else.
            using var panel = OverlayHost.Create(Size);
            using var popUps = NotificationHost.Create(Size);

            Assert.False(panel.IsDrawing);
            Assert.False(popUps.IsDrawing);
            Assert.Equal(0, panel.FramesDrawn);
            Assert.Equal(0, popUps.FramesDrawn);
        });
    }
}
