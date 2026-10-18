using System.Numerics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.VisualTree;
using Modbot.Companion.Overlay;
using Modbot.Overlay.Interaction;
using Modbot.Overlay.OpenVr;
using Modbot.Overlay.Rendering;
using Modbot.Overlay.Views;

namespace Modbot.Overlay.Tests.Interaction;

/// <summary>
/// The one rule for what lights under the laser or the mouse: which targets light, how a lit
/// control looks, and that it is put back.
/// </summary>
public class LitControlTests
{
    private static Color ColourOf(IBrush? brush) => Assert.IsAssignableFrom<ISolidColorBrush>(brush).Color;

    [Fact]
    public void EveryControlAPressCanLandOnLights()
    {
        var lights = new OverlayTarget[]
        {
            new OverlayTarget.Lock(),
            new OverlayTarget.ClickThrough(),
            new OverlayTarget.ResizeCorner(),
            new OverlayTarget.BackToTop(OverlayPage.Instance),
            new OverlayTarget.GoTo(OverlayPage.Events),
            new OverlayTarget.Filter(OverlayPage.Instance, FilterPart.Name),
            new OverlayTarget.Pick(OverlayPage.Instance, FilterPart.Name, 0),
            new OverlayTarget.ClearFilters(OverlayPage.Instance),
            new OverlayTarget.Person("usr_1"),
            new OverlayTarget.AnswerPopUp("a1", true),
            new OverlayTarget.AnswerPopUp("a1", false),
            new OverlayTarget.PanelButton(),
            new OverlayTarget.SaveClip(),
            new OverlayTarget.DismissAlert(),
        };

        foreach (var target in lights)
            Assert.True(Lit.Lights(target, editMode: true), target.ToString());
    }

    [Fact]
    public void WhatOnlyHoldsOtherControlsDoesNotLight()
    {
        Assert.False(Lit.Lights(null, editMode: true));
        Assert.False(Lit.Lights(new OverlayTarget.Roster(), editMode: true));
        Assert.False(Lit.Lights(new OverlayTarget.Events(), editMode: true));
        Assert.False(Lit.Lights(new OverlayTarget.PopUpBox(), editMode: true));
        Assert.False(Lit.Lights(new OverlayTarget.Bar(), editMode: true));
    }

    [Fact]
    public void TheBarsControlsLightOnlyInEditMode()
    {
        Assert.True(Lit.Lights(new OverlayTarget.Lock(), editMode: true));
        Assert.False(Lit.Lights(new OverlayTarget.Lock(), editMode: false));
        Assert.False(Lit.Lights(new OverlayTarget.ClickThrough(), editMode: false));
        Assert.False(Lit.Lights(new OverlayTarget.ResizeCorner(), editMode: false));
        Assert.True(Lit.Lights(new OverlayTarget.GoTo(OverlayPage.Events), editMode: false));
    }

    [Fact]
    public void ALitCardWearsTheAccentAndIsPutBack()
    {
        AvaloniaTestHost.Run(() =>
        {
            var tokens = DesignTokens.Vr;
            var card = new Border
            {
                Tag = new OverlayTarget.Person("usr_1"),
                Background = tokens.Surface3Brush,
                BorderBrush = tokens.Border2Brush,
            };
            var other = new Border { Tag = new OverlayTarget.Person("usr_2"), Background = tokens.Surface3Brush };
            var root = new StackPanel { Children = { card, other } };
            var lit = new Lit();

            lit.Show(root, new OverlayTarget.Person("usr_1"), tokens);

            Assert.Equal(ColourOf(tokens.AccentDimBrush), ColourOf(card.Background));
            Assert.Equal(ColourOf(tokens.AccentBrush), ColourOf(card.BorderBrush));
            Assert.Equal(ColourOf(tokens.Surface3Brush), ColourOf(other.Background));

            // Moving to the other card puts the first back.
            lit.Show(root, new OverlayTarget.Person("usr_2"), tokens);

            Assert.Equal(ColourOf(tokens.Surface3Brush), ColourOf(card.Background));
            Assert.Equal(ColourOf(tokens.Border2Brush), ColourOf(card.BorderBrush));
            Assert.Equal(ColourOf(tokens.AccentDimBrush), ColourOf(other.Background));

            lit.Show(root, null, tokens);

            Assert.Equal(ColourOf(tokens.Surface3Brush), ColourOf(other.Background));
        });
    }

    [Fact]
    public void ALitPanelControlWearsTheAccent()
    {
        AvaloniaTestHost.Run(() =>
        {
            var tokens = DesignTokens.Vr;
            var track = new Grid { Tag = new DashboardTarget.Track(DashboardSlider.Width), Background = Brushes.Transparent };
            var lit = new Lit();

            lit.Show(new StackPanel { Children = { track } }, new DashboardTarget.Track(DashboardSlider.Width), tokens);
            Assert.Equal(ColourOf(tokens.AccentDimBrush), ColourOf(track.Background));

            lit.Clear();
            Assert.Equal(Colors.Transparent, ColourOf(track.Background));
        });
    }

    [Fact]
    public void TheResizeCornersStrokesGoOpaqueRatherThanItsGround()
    {
        AvaloniaTestHost.Run(() =>
        {
            var tokens = DesignTokens.Vr;
            var seeThrough = new SolidColorBrush(tokens.Palette.Text, 0.55);
            var stroke = new Avalonia.Controls.Shapes.Path { Stroke = seeThrough };
            var corner = new Border
            {
                Tag = new OverlayTarget.ResizeCorner(),
                Background = Brushes.Transparent,
                Child = stroke,
            };
            var lit = new Lit();

            lit.Show(corner, new OverlayTarget.ResizeCorner(), tokens);

            Assert.Equal(255, ColourOf(stroke.Stroke).A);
            Assert.Equal(Colors.Transparent, ColourOf(corner.Background));

            lit.Clear();
            Assert.Same(seeThrough, stroke.Stroke);
        });
    }

    [Fact]
    public void ARowCardOnTheBuiltPanelIsTheOneThatLights()
    {
        AvaloniaTestHost.Run(() =>
        {
            var screen = new OverlayScreen(
                "Cat Lounge",
                new Cached<InstanceContext>(
                    new InstanceContext("39911", [new RosterMember("usr_1", "Rin", RosterStanding.Member, 0, []), new RosterMember("usr_2", "Sam", RosterStanding.Member, 0, [])]),
                    Freshness.Fresh,
                    TimeSpan.Zero),
                Freshness.Fresh);
            var root = OverlayView.Build(screen);
            var tokens = DesignTokens.Vr;

            new Lit().Show(root, new OverlayTarget.Person("usr_1"), tokens);

            var rows = root.GetVisualDescendants().OfType<Border>().Where(b => b.Tag is OverlayTarget.Person).ToList();
            var lit = rows.Where(r => ColourOf(r.Background) == ColourOf(tokens.AccentDimBrush)).ToList();

            Assert.Equal(2, rows.Count);
            Assert.Single(lit);
            Assert.Equal(new OverlayTarget.Person("usr_1"), lit[0].Tag);
        });
    }
}

/// <summary>
/// The main panel with controllers: the control under the ray lights, a ray that stays on it
/// costs no frame of its own, and leaving the panel puts it out.
/// </summary>
public class LitMainPanelTests
{
    private const int Size = OverlayHost.DefaultResolution;

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
            new InstanceContext("39911", [new RosterMember("usr_0", "Rin", RosterStanding.Member, 0, [])]),
            Freshness.Fresh,
            TimeSpan.Zero),
        Freshness.Fresh);

    private static (OverlayHost Host, AttachedRuntime Runtime) Build(bool editMode = true)
    {
        var runtime = new AttachedRuntime();
        var host = new OverlayHost(runtime, new FakeSurface(), new AvaloniaFrameRenderer(Size, Size));
        runtime.Start();
        host.Start();
        host.EditMode = editMode;
        host.Update(Roster());
        return (host, runtime);
    }

    private static Vector3 PointOn(OverlayPlacement placement, float across, float down) => new(
        placement.Offset.X + ((across - 0.5f) * placement.Width),
        placement.Offset.Y - ((down - 0.5f) * placement.Width),
        placement.Offset.Z);

    /// <summary>The hits of a target across the drawn frame: the first and the last, which are on the same control.</summary>
    private static List<(float Across, float Down)> Hits(OverlayHost host, Func<OverlayTarget?, bool> wanted)
    {
        var hits = new List<(float, float)>();
        for (var y = 0; y < 128; y++)
        {
            for (var x = 0; x < 128; x++)
            {
                var (across, down) = ((x + 0.5f) / 128f, (y + 0.5f) / 128f);
                if (wanted(host.TargetAt(across, down)))
                    hits.Add((across, down));
            }
        }

        Assert.NotEmpty(hits);
        return hits;
    }

    /// <summary>A spot on the first control found of a kind: its top left corner, which is inside it, not between two.</summary>
    private static (float Across, float Down) Middle(OverlayHost host, Func<OverlayTarget?, bool> wanted)
        => Hits(host, wanted)[0];

    private static void Point(OverlayHost host, AttachedRuntime runtime, (float Across, float Down) spot, double ms)
    {
        runtime.Tracking = Hands.RightOnly(Hands.Hand(Hands.AimingAt(Vector3.Zero, PointOn(host.Placement, spot.Across, spot.Down))));
        host.PollInput(TimeSpan.FromMilliseconds(ms));
    }

    [Fact]
    public void ARayOnATabLightsItAndLeavingThePanelPutsItOut()
    {
        AvaloniaTestHost.Run(() =>
        {
            var (host, runtime) = Build();
            using var panel = host;

            Point(host, runtime, Middle(host, t => t is OverlayTarget.GoTo { Page: OverlayPage.Events }), 0);
            Assert.Equal(new OverlayTarget.GoTo(OverlayPage.Events), host.LitUp);

            runtime.Tracking = OverlayTracking.None;
            host.PollInput(TimeSpan.FromMilliseconds(50));
            Assert.Null(host.LitUp);
        });
    }

    [Fact]
    public void EachKindOfControlOnThePanelLightsUnderTheRay()
    {
        AvaloniaTestHost.Run(() =>
        {
            var (host, runtime) = Build();
            using var panel = host;

            var kinds = new Func<OverlayTarget?, bool>[]
            {
                t => t is OverlayTarget.GoTo,
                t => t is OverlayTarget.Filter,
                t => t is OverlayTarget.Person,
                t => t is OverlayTarget.Lock,
                t => t is OverlayTarget.ClickThrough,
                t => t is OverlayTarget.ResizeCorner,
            };

            var time = 0;
            foreach (var kind in kinds)
            {
                var spot = Middle(host, kind);
                var expected = host.TargetAt(spot.Across, spot.Down);

                Point(host, runtime, spot, time += 40);

                Assert.NotNull(expected);
                Assert.Equal(expected, host.LitUp);
            }
        });
    }

    [Fact]
    public void ARayMovingWithinOneControlCostsOnlyTheCursorsFrameAndAnotherControlCostsOne()
    {
        AvaloniaTestHost.Run(() =>
        {
            var (host, runtime) = Build();
            using var panel = host;

            var tab = Hits(host, t => t is OverlayTarget.GoTo { Page: OverlayPage.Events });
            var other = Middle(host, t => t is OverlayTarget.GoTo { Page: not OverlayPage.Events });

            Point(host, runtime, tab[0], 0);
            var first = host.LitUp;
            var frames = host.FramesDrawn;

            // The same spot again: nothing moved, nothing drawn.
            Point(host, runtime, tab[0], 40);
            Assert.Equal(frames, host.FramesDrawn);

            // Another spot on the same control: the cursor's frame, and the lit control is the same.
            Point(host, runtime, tab[^1], 80);
            Assert.Equal(frames + 1, host.FramesDrawn);
            Assert.Equal(first, host.LitUp);

            // Another control: one frame with the cursor and the lit control both changed.
            Point(host, runtime, other, 120);
            Assert.Equal(frames + 2, host.FramesDrawn);
            Assert.NotEqual(first, host.LitUp);
        });
    }

    [Fact]
    public void AClickThroughPanelLightsOnlyItsBar()
    {
        AvaloniaTestHost.Run(() =>
        {
            var (host, runtime) = Build();
            using var panel = host;

            var tab = Middle(host, t => t is OverlayTarget.GoTo);
            var hand = Middle(host, t => t is OverlayTarget.ClickThrough);

            host.Place(host.Placement with { ClickThrough = true });

            Point(host, runtime, tab, 0);
            Assert.Null(host.LitUp);

            Point(host, runtime, hand, 40);
            Assert.IsType<OverlayTarget.ClickThrough>(host.LitUp);
        });
    }

    [Fact]
    public void ALockedPanelStillLightsWhatCanBePressed()
    {
        AvaloniaTestHost.Run(() =>
        {
            var (host, runtime) = Build();
            using var panel = host;

            var tab = Middle(host, t => t is OverlayTarget.GoTo);

            host.Place(host.Placement with { Locked = true });

            Point(host, runtime, tab, 0);
            Assert.IsType<OverlayTarget.GoTo>(host.LitUp);
        });
    }

    [Fact]
    public void TheBarLightsNothingOutOfEditMode()
    {
        AvaloniaTestHost.Run(() =>
        {
            var (host, runtime) = Build(editMode: true);
            using var panel = host;

            var hand = Middle(host, t => t is OverlayTarget.ClickThrough);
            host.EditMode = false;

            Point(host, runtime, hand, 0);
            Assert.Null(host.LitUp);
        });
    }
}

/// <summary>The pop-up panel: a pop-up's buttons and the bar light under the ray.</summary>
public class LitPopUpPanelTests
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

    private static (float Across, float Down) Middle(NotificationHost host, Func<OverlayTarget?, bool> wanted)
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

        Assert.NotEmpty(hits);
        return (hits.Average(h => h.Across), hits.Average(h => h.Down));
    }

    private static void Point(NotificationHost host, AttachedRuntime runtime, (float Across, float Down) spot, double ms)
    {
        var placement = host.Placement;
        var at = new Vector3(
            placement.Offset.X + ((spot.Across - 0.5f) * placement.Width),
            placement.Offset.Y - ((spot.Down - 0.5f) * placement.Width),
            placement.Offset.Z);

        runtime.Tracking = Hands.RightOnly(Hands.Hand(Hands.AimingAt(Vector3.Zero, at)));
        host.PollInput(TimeSpan.FromMilliseconds(ms));
    }

    [Fact]
    public void APopUpsButtonAndTheBarLightUnderTheRayAndLeavingPutsThemOut()
    {
        AvaloniaTestHost.Run(() =>
        {
            var runtime = new AttachedRuntime();
            using var host = new NotificationHost(
                runtime, new FakeSurface(), new AvaloniaFrameRenderer(Size, Size), NotifyOverlaySettings.Default.ToPlacement());
            runtime.Start();
            host.Start();
            host.EditMode = true;
            host.Update(new NotificationScreen([new PopUp("a1", "Cat Lounge", "Somebody", null, PopUpTone.Flagged, Ask: new PopUpAsk("Yes", "No"))]));

            var yes = Middle(host, t => t is OverlayTarget.AnswerPopUp { Yes: true });
            Point(host, runtime, yes, 0);
            Assert.Equal(new OverlayTarget.AnswerPopUp("a1", true), host.LitUp);

            var frames = host.FramesDrawn;
            Point(host, runtime, yes, 40);
            Assert.Equal(frames, host.FramesDrawn);

            var no = Middle(host, t => t is OverlayTarget.AnswerPopUp { Yes: false });
            Point(host, runtime, no, 80);
            Assert.Equal(new OverlayTarget.AnswerPopUp("a1", false), host.LitUp);

            Point(host, runtime, Middle(host, t => t is OverlayTarget.Lock), 120);
            Assert.IsType<OverlayTarget.Lock>(host.LitUp);

            runtime.Tracking = OverlayTracking.None;
            host.PollInput(TimeSpan.FromMilliseconds(160));
            Assert.Null(host.LitUp);
        });
    }
}

/// <summary>The show and hide button: its face lights under the ray.</summary>
public class LitButtonTests
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

    private static Pose Aim(ButtonHost host)
    {
        var centre = host.Placement.Offset;
        return Hands.AimingAt(Vector3.Zero, new Vector3(centre.X, centre.Y, centre.Z));
    }

    [Fact]
    public void TheFaceLightsUnderTheRayAndLeavingPutsItOut()
    {
        AvaloniaTestHost.Run(() =>
        {
            var runtime = new AttachedRuntime();
            using var host = new ButtonHost(runtime, new FakeSurface(), new AvaloniaFrameRenderer(Size, Size), ButtonPlace.Corner);
            runtime.Start();
            host.Start();
            host.Update(ButtonScreen.Shown);

            runtime.Tracking = Hands.RightOnly(Hands.Hand(Aim(host)));
            host.PollInput(TimeSpan.Zero);
            Assert.IsType<OverlayTarget.PanelButton>(host.LitUp);

            // Still on the face: no frame of its own.
            var frames = host.FramesDrawn;
            host.PollInput(TimeSpan.FromMilliseconds(40));
            Assert.Equal(frames, host.FramesDrawn);

            runtime.Tracking = OverlayTracking.None;
            host.PollInput(TimeSpan.FromMilliseconds(80));
            Assert.Null(host.LitUp);
        });
    }
}
