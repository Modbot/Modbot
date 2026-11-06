using System.Numerics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Modbot.Companion.Chat;
using Modbot.Companion.Overlay;
using Modbot.Overlay.Interaction;
using Modbot.Overlay.OpenVr;
using Modbot.Overlay.Rendering;
using Modbot.Overlay.Tests.Interaction;
using Modbot.Overlay.Views;

namespace Modbot.Overlay.Tests;

/// <summary>
/// The chat panel's host: the bar with the lock and the hand in edit mode, the X that puts it away,
/// the resize corner, the typing dots, the error line and the long answer that scrolls.
/// </summary>
public class ChatHostTests
{
    private const int Size = ChatHost.DesignPixels;

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

    private static ChatScreen Said(params ChatLine[] lines) => new(lines);

    private static (ChatHost Host, AttachedRuntime Runtime) Build(ChatScreen? screen = null)
    {
        var runtime = new AttachedRuntime();
        var host = new ChatHost(runtime, new FakeSurface(), new AvaloniaFrameRenderer(Size, Size));
        runtime.Start();
        host.Start();
        host.Show(screen ?? Said(new ChatLine(true, "Hello", ChatLineState.Done)));
        return (host, runtime);
    }

    private static TimeSpan At(double seconds) => TimeSpan.FromSeconds(seconds);

    /// <summary>The middle of what is drawn at a target, as fractions across and down, or null when nothing is.</summary>
    private static (float Across, float Down)? Find(ChatHost host, Func<OverlayTarget?, bool> wanted)
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
    private static void Press(ChatHost host, AttachedRuntime runtime, (float Across, float Down) spot, double at)
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
            using var host = new ChatHost(runtime, new FakeSurface(), new AvaloniaFrameRenderer(Size, Size));

            Assert.True(host.Hidden);

            host.Show(ChatScreen.Empty);

            Assert.False(host.Hidden);
        });
    }

    [Fact]
    public void TheBarHasTheLockThenTheHandUnderTheBoxInEditMode()
    {
        AvaloniaTestHost.Run(() =>
        {
            var (host, _) = Build();
            using var chat = host;
            host.EditMode = true;

            var padlock = Find(host, t => t is OverlayTarget.Lock);
            var hand = Find(host, t => t is OverlayTarget.ClickThrough);
            var close = Find(host, t => t is OverlayTarget.CloseChat);

            Assert.NotNull(padlock);
            Assert.NotNull(hand);
            Assert.NotNull(close);
            Assert.True(padlock.Value.Across < hand.Value.Across, "Lock, then the hand.");
            Assert.True(padlock.Value.Down > close.Value.Down, "The bar is under the box.");
        });
    }

    [Fact]
    public void ThePadlockLocksThePanelAndSavesIt()
    {
        AvaloniaTestHost.Run(() =>
        {
            var (host, runtime) = Build();
            using var chat = host;
            host.EditMode = true;
            var saved = new List<OverlayPlacement>();
            host.PlacementChanged += saved.Add;

            Press(host, runtime, Find(host, t => t is OverlayTarget.Lock)!.Value, at: 0);

            Assert.True(host.Placement.Locked);
            Assert.True(saved[^1].Locked);
        });
    }

    [Fact]
    public void TheHandLetsRaysThroughAndSavesIt()
    {
        AvaloniaTestHost.Run(() =>
        {
            var (host, runtime) = Build();
            using var chat = host;
            host.EditMode = true;
            var saved = new List<OverlayPlacement>();
            host.PlacementChanged += saved.Add;

            Press(host, runtime, Find(host, t => t is OverlayTarget.ClickThrough)!.Value, at: 0);

            Assert.True(host.Placement.ClickThrough);
            Assert.True(saved[^1].ClickThrough);
        });
    }

    [Fact]
    public void TheXRaisesClosedAndPutsThePanelAway()
    {
        AvaloniaTestHost.Run(() =>
        {
            var (host, runtime) = Build();
            using var chat = host;
            var closed = 0;
            host.Closed += () => closed++;

            // Edit mode is off: the X works all the same.
            Press(host, runtime, Find(host, t => t is OverlayTarget.CloseChat)!.Value, at: 0);

            Assert.Equal(1, closed);
            Assert.True(host.Hidden);
        });
    }

    [Fact]
    public void TheResizeCornerIsThereInEditModeOnly()
    {
        AvaloniaTestHost.Run(() =>
        {
            var (host, _) = Build();
            using var chat = host;

            Assert.Null(Find(host, t => t is OverlayTarget.ResizeCorner));

            host.EditMode = true;

            Assert.NotNull(Find(host, t => t is OverlayTarget.ResizeCorner));
        });
    }

    private static List<Color> DotColours(int frame)
        => [.. ChatView.Build(Said(new ChatLine(false, string.Empty, ChatLineState.Typing)), 0, frame, OverlayLook.Headset)
            .GetLogicalDescendants()
            .OfType<Ellipse>()
            .Select(dot => ((ISolidColorBrush)dot.Fill!).Color)];

    [Fact]
    public void TheTypingDotsMoveFromFrameToFrame()
    {
        AvaloniaTestHost.Run(() =>
        {
            var first = DotColours(0);
            var later = DotColours(2);

            Assert.Equal(3, first.Count);
            Assert.NotEqual(first, later);

            // A round later the animation is where it began.
            Assert.Equal(first, DotColours(ChatView.Frames));
        });
    }

    [Fact]
    public void AnErrorShowsAsItsOwnLine()
    {
        AvaloniaTestHost.Run(() =>
        {
            const string Problem = "The AI could not be reached.";
            var root = ChatView.Build(Said(new ChatLine(false, Problem, ChatLineState.Error)), 0, 0, OverlayLook.Headset);

            var line = root.GetLogicalDescendants().OfType<TextBlock>().Single(t => t.Text == Problem);

            Assert.Equal(((ISolidColorBrush)OverlayLook.Headset.Tokens.DangerBrush).Color, ((ISolidColorBrush)line.Foreground!).Color);
        });
    }

    [Fact]
    public void ALongAnswerCanBeScrolled()
    {
        AvaloniaTestHost.Run(() =>
        {
            var answer = string.Join(' ', Enumerable.Repeat("word", 400));
            var root = ChatView.Build(Said(new ChatLine(false, answer, ChatLineState.Done)), 0, 0, OverlayLook.Headset);
            root.Measure(new Size(Size, 200));
            root.Arrange(new Rect(0, 0, Size, 200));

            // The list is internal to the overlay, so it is found by name and asked how far it can go.
            var list = root.GetLogicalDescendants().Single(c => c.GetType().Name == "ScrollingList");
            var most = (double)list.GetType().GetProperty("MaxOffset")!.GetValue(list)!;

            Assert.True(most > 0, "An answer taller than the box scrolls.");
        });
    }
}
