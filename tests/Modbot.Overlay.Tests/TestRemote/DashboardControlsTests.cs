using Modbot.Companion.Overlay;
using Modbot.Overlay.OpenVr;
using Modbot.Overlay.Rendering;
using Modbot.Overlay.Views;

namespace Modbot.Overlay.Tests.TestRemote;

/// <summary>
/// The test remote finds the dashboard tab's controls by label and presses them through the tab's
/// own pointer handling, the way SteamVR's laser does.
/// </summary>
public class DashboardControlsTests
{
    private sealed class Surface : IOverlaySurface
    {
        public int Width => DashboardHost.PageWidth;

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

    /// <summary>Records every pointer the host is handed by SteamVR; the remote must hand it none this way.</summary>
    private sealed class Runtime : IDashboardRuntime
    {
        public OverlayRuntimeStatus Status { get; private set; } = new(OverlayRuntimeState.NotStarted);

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
    {
        var host = new DashboardHost(new Runtime(), new Surface(), new AvaloniaFrameRenderer(DashboardHost.PageWidth, DashboardHost.PageHeight));
        host.Update(DashboardScreen.Default);
        return host;
    }

    [Fact]
    public void EveryControlHasALabelOfItsOwn()
    {
        AvaloniaTestHost.Run(() =>
        {
            using var host = Host();
            var controls = DashboardControls.Of(host);

            Assert.NotEmpty(controls);
            Assert.Equal(controls.Count, controls.Select(c => c.Label.ToLowerInvariant()).Distinct().Count());
            Assert.Contains(controls, c => c is { Label: "Overlay on", Kind: "switch", On: true });
            Assert.Contains(controls, c => c is { Label: "Head", Kind: "choice", On: true });
            Assert.Contains(controls, c => c is { Label: "Put it back in front of me", Kind: "button" });
            Assert.Contains(controls, c => c is { Label: "Width", Kind: "slider", Value: not null });
            Assert.Contains(controls, c => c is { Label: "Width +", Kind: "step" });
            Assert.All(controls, c => Assert.True(c.Bounds.Width > 0 && c.Bounds.Height > 0));
        });
    }

    [Fact]
    public void APressGoesThroughThePointerPathAtTheControlsMiddle()
    {
        AvaloniaTestHost.Run(() =>
        {
            using var host = Host();
            var heard = new List<DashboardPointer>();
            host.PressHeard += heard.Add;

            bool? asked = null;
            host.OverlayOnChanged += on => asked = on;

            var pressed = DashboardControls.Press(host, "overlay ON");

            Assert.NotNull(pressed);
            Assert.False(asked);

            // The trigger came down where the control is drawn, as SteamVR's laser would send it.
            var down = Assert.Single(heard);
            Assert.Equal(DashboardPointerKind.Down, down.Kind);
            Assert.True(pressed.Bounds.Contains(new Avalonia.Point(down.X, down.Y)));
        });
    }

    [Fact]
    public void AnAnchorAndPutBackArePressedByTheirWords()
    {
        AvaloniaTestHost.Run(() =>
        {
            using var host = Host();
            OverlayAnchor? anchor = null;
            var putBack = 0;
            host.AnchorChosen += a => anchor = a;
            host.PutBackPressed += () => putBack++;

            DashboardControls.Press(host, "Left wrist");
            DashboardControls.Press(host, "Put it back in front of me");

            Assert.Equal(OverlayAnchor.LeftHand, anchor);
            Assert.Equal(1, putBack);
        });
    }

    [Fact]
    public void ATypedMinusPressesTheMinusSign()
    {
        AvaloniaTestHost.Run(() =>
        {
            using var host = Host();
            NotifyOverlaySettings? asked = null;
            host.NotifyOverlayChanged += s => asked = s;

            Assert.NotNull(DashboardControls.Press(host, "Width -"));

            Assert.NotNull(asked);
            Assert.True(asked.Width < NotifyOverlaySettings.Default.Width);
        });
    }

    [Fact]
    public void SlidingPressesTheTrackWhereTheValueSits()
    {
        AvaloniaTestHost.Run(() =>
        {
            using var host = Host();
            NotifyOverlaySettings? asked = null;
            host.NotifyOverlayChanged += s => asked = s;

            Assert.NotNull(DashboardControls.Slide(host, "Opacity", 0.0));

            Assert.NotNull(asked);
            Assert.Equal(NotifyOverlaySettings.MinOpacity, asked.Opacity, 3);
        });
    }

    [Fact]
    public void AnUnknownLabelPressesNothing()
    {
        AvaloniaTestHost.Run(() =>
        {
            using var host = Host();
            var heard = 0;
            host.PressHeard += _ => heard++;

            Assert.Null(DashboardControls.Press(host, "Self destruct"));
            Assert.Null(DashboardControls.Slide(host, "Head", 0.5));
            Assert.Equal(0, heard);
        });
    }
}
