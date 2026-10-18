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
/// around it is not, it is picked up, locked and let through like the other panels, and the place
/// setting moves it.
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

        public int Shows { get; private set; }

        public int Hides { get; private set; }

        public void Show() => Shows++;

        public void Hide() => Hides++;

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

            // A hair outside the card's left edge, in the clear ground around it.
            var edge = (OverlayButton.CornerWidth / 2f) + 0.01f;

            runtime.Tracking = Hands.RightOnly(Hands.Hand(Aim(host, across: -edge), click: true));
            host.PollInput(TimeSpan.Zero);

            Assert.Equal(0, presses);
            Assert.Null(host.Busy);
        });
    }

    [Fact]
    public void AGripOnTheButtonPicksItUpAndItGoesBackOnTheHeadWhereItWasLeft()
    {
        AvaloniaTestHost.Run(() =>
        {
            var (host, runtime) = Build();
            using var button = host;
            var saved = new List<OverlayPlacement>();
            host.PlacementChanged += saved.Add;
            var start = host.Placement;

            Assert.False(start.Locked);

            runtime.Tracking = Hands.RightOnly(Hands.Hand(Aim(host), grab: true));
            host.PollInput(At(0));
            Assert.Equal(Hand.Right, host.Holding);
            Assert.Equal(Hand.Right, host.Busy);

            // Carried a little lower, then let go: still on the head, a little lower.
            var lower = Hands.AimingAt(new Vector3(0f, -0.1f, 0f), new Vector3(start.Offset.X, start.Offset.Y - 0.1f, start.Offset.Z));
            runtime.Tracking = Hands.RightOnly(Hands.Hand(lower, grab: true));
            host.PollInput(At(0.033));
            runtime.Tracking = Hands.RightOnly(Hands.Hand(lower));
            host.PollInput(At(0.066));

            Assert.Null(host.Holding);
            Assert.Equal(OverlayAnchor.Head, host.Placement.Anchor);
            Assert.NotEqual(start.Offset, host.Placement.Offset);
            Assert.Equal(host.Placement, saved[^1]);
        });
    }

    [Fact]
    public void TheBarIsThereInEditModeOnlyAndTheFaceKeepsItsPlaceAboveIt()
    {
        AvaloniaTestHost.Run(() =>
        {
            var (host, _) = Build();
            using var button = host;

            Assert.Null(Find(host, t => t is OverlayTarget.Lock));
            Assert.Null(Find(host, t => t is OverlayTarget.ClickThrough));

            host.EditMode = true;

            var padlock = Find(host, t => t is OverlayTarget.Lock);
            var hand = Find(host, t => t is OverlayTarget.ClickThrough);
            var face = Find(host, t => t is OverlayTarget.PanelButton);

            Assert.NotNull(padlock);
            Assert.NotNull(hand);
            Assert.NotNull(face);
            Assert.True(padlock.Value.Down > face.Value.Down, "The bar is under the face.");
            Assert.True(padlock.Value.Across < hand.Value.Across, "Lock, then the hand.");
        });
    }

    /// <summary>The card's left, right, top and bottom edges in the picture's pixels, read along the middle of the picture.</summary>
    private static (int Left, int Right, int Top, int Bottom) CardEdges(ButtonHost host)
    {
        bool OnCard(int x, int y) => host.TargetAt((x + 0.5f) / Size, (y + 0.5f) / Size) is OverlayTarget.PanelButton;

        var middle = Size / 2;
        var across = Enumerable.Range(0, Size).Where(x => OnCard(x, middle)).ToList();
        var down = Enumerable.Range(0, Size).Where(y => OnCard(middle, y)).ToList();

        return (across.Min(), across.Max() + 1, down.Min(), down.Max() + 1);
    }

    [Theory]
    [InlineData(ButtonPlace.Corner)]
    [InlineData(ButtonPlace.Wrist)]
    public void TheCardIsTheSameSizeAndInTheSameSpotWithEditModeOnAndOff(ButtonPlace place)
    {
        AvaloniaTestHost.Run(() =>
        {
            var (host, runtime) = Build(place);
            using var button = host;
            var cardWidth = OverlayButton.ToPlacement(place).Width;

            var off = CardEdges(host);
            var placed = runtime.Placed.Count;
            var told = runtime.Placed[^1];
            var spot = host.Placement;

            host.EditMode = true;
            var on = CardEdges(host);

            // In the picture: the same edges, in the middle of it.
            Assert.Equal(off, on);
            Assert.InRange(off.Left + off.Right, Size - 1, Size + 1);
            Assert.InRange(off.Top + off.Bottom, Size - 1, Size + 1);
            Assert.InRange(off.Right - off.Left, OverlayButton.CardPixels * 0.8, OverlayButton.CardPixels);
            Assert.Equal(off.Right - off.Left, off.Bottom - off.Top);

            // In the room: nothing is told again, and what was told puts the card's 256 pixels at its own width.
            Assert.Equal(placed, runtime.Placed.Count);
            Assert.Equal(told, runtime.Placed[^1]);
            Assert.Equal(spot, host.Placement);
            Assert.Equal(cardWidth, told.Width);
            Assert.Equal(cardWidth, OverlayButton.CardPixels * told.Side / Size, 4);
            Assert.Equal(OverlayButton.ToPlacement(place).Offset, told.Offset);

            host.EditMode = false;
            Assert.Equal(off, CardEdges(host));
        });
    }

    [Fact]
    public void TheBarIsInTheGroundUnderTheCardAndNothingIsDrawnThereWithEditModeOff()
    {
        AvaloniaTestHost.Run(() =>
        {
            var (host, _) = Build();
            using var button = host;
            var card = CardEdges(host);
            var underTheCard = (card.Bottom + Size) / 2;

            Assert.Null(host.TargetAt(0.5f, (underTheCard + 0.5f) / Size));

            host.EditMode = true;

            var padlock = Find(host, t => t is OverlayTarget.Lock)!.Value;
            Assert.True(padlock.Down * Size >= card.Bottom, "The lock is under the card.");
            Assert.True(padlock.Down * Size <= Size, "The lock is inside the picture.");
            Assert.IsType<OverlayTarget.Bar>(host.TargetAt(0.5f, (underTheCard + 0.5f) / Size));
        });
    }

    [Fact]
    public void ThePadlockOnTheBarLocksTheButtonAndSavesItAndALockedButtonIsNotPickedUp()
    {
        AvaloniaTestHost.Run(() =>
        {
            var (host, runtime) = Build();
            using var button = host;
            host.EditMode = true;
            var saved = new List<OverlayPlacement>();
            host.PlacementChanged += saved.Add;

            Press(host, runtime, Find(host, t => t is OverlayTarget.Lock)!.Value, at: 0);

            Assert.True(host.Placement.Locked);
            Assert.True(saved[^1].Locked);

            runtime.Tracking = Hands.RightOnly(Hands.Hand(Aim(host), grab: true));
            host.PollInput(At(1));
            Assert.Null(host.Holding);
            Assert.Equal(OverlayAnchor.Head, host.Placement.Anchor);

            // Locked, a click on the face still presses.
            var presses = 0;
            host.Pressed += () => presses++;
            runtime.Tracking = Hands.RightOnly(Hands.Hand(Aim(host)));
            host.PollInput(At(2));
            runtime.Tracking = Hands.RightOnly(Hands.Hand(Aim(host), click: true));
            host.PollInput(At(3));
            Assert.Equal(1, presses);
        });
    }

    [Fact]
    public void WithTheHandOnTheFaceIsLookedThroughAndOnlyTheBarAnswersUntilItIsTurnedOff()
    {
        AvaloniaTestHost.Run(() =>
        {
            var (host, runtime) = Build();
            using var button = host;
            host.EditMode = true;
            var presses = 0;
            host.Pressed += () => presses++;

            Press(host, runtime, Find(host, t => t is OverlayTarget.ClickThrough)!.Value, at: 0);
            Assert.True(host.Placement.ClickThrough);

            // A click on the face, and a grip on it, do nothing.
            runtime.Tracking = Hands.RightOnly(Hands.Hand(Aim(host)));
            host.PollInput(At(1));
            runtime.Tracking = Hands.RightOnly(Hands.Hand(Aim(host), click: true));
            host.PollInput(At(2));
            runtime.Tracking = Hands.RightOnly(Hands.Hand(Aim(host)));
            host.PollInput(At(3));
            runtime.Tracking = Hands.RightOnly(Hands.Hand(Aim(host), grab: true));
            host.PollInput(At(4));
            Assert.Equal(0, presses);
            Assert.Null(host.Holding);

            // The bar still answers, so the hand can be turned off again, and the face presses again.
            Press(host, runtime, Find(host, t => t is OverlayTarget.ClickThrough)!.Value, at: 5);
            Assert.False(host.Placement.ClickThrough);

            runtime.Tracking = Hands.RightOnly(Hands.Hand(Aim(host)));
            host.PollInput(At(7));
            runtime.Tracking = Hands.RightOnly(Hands.Hand(Aim(host), click: true));
            host.PollInput(At(8));
            Assert.Equal(1, presses);
        });
    }

    [Fact]
    public void ThePadlockDoesNothingWhileEditModeIsOff()
    {
        AvaloniaTestHost.Run(() =>
        {
            var (host, runtime) = Build();
            using var button = host;
            host.EditMode = true;
            var padlock = Find(host, t => t is OverlayTarget.Lock)!.Value;
            host.EditMode = false;

            Press(host, runtime, padlock, at: 0);

            Assert.False(host.Placement.Locked);
        });
    }

    [Fact]
    public void TwoQuickGripsSendTheButtonBackToItsPlacesOwnSpot()
    {
        AvaloniaTestHost.Run(() =>
        {
            var moved = OverlayButton.ToPlacement(ButtonPlace.Corner) with { Offset = new OverlayPose(-0.1f, 0.1f, -0.6f), Width = 0.3f };
            var runtime = new AttachedRuntime();
            using var host = new ButtonHost(runtime, new FakeSurface(), new AvaloniaFrameRenderer(Size, Size), ButtonPlace.Corner, moved);
            runtime.Start();
            host.Start();
            host.Update(ButtonScreen.Shown);

            Assert.Equal(moved, host.Placement);

            // Grip, let go, grip again inside half a second.
            runtime.Tracking = Hands.RightOnly(Hands.Hand(Aim(host), grab: true));
            host.PollInput(At(0));
            runtime.Tracking = Hands.RightOnly(Hands.Hand(Aim(host)));
            host.PollInput(At(0.1));
            runtime.Tracking = Hands.RightOnly(Hands.Hand(Aim(host), grab: true));
            host.PollInput(At(0.2));

            var own = OverlayButton.ToPlacement(ButtonPlace.Corner);
            Assert.Equal(own.Anchor, host.Placement.Anchor);
            Assert.Equal(own.Offset, host.Placement.Offset);
            Assert.Equal(own.Width, host.Placement.Width, 3);
        });
    }

    /// <summary>
    /// The right hand takes the button where it hangs from the left hand, carries it 10 cm to the
    /// side in the room, and lets go while the left hand is <paramref name="leftAtRelease"/>.
    /// Returns where it hung before, in the room.
    /// </summary>
    private static Pose CarryAside(ButtonHost host, AttachedRuntime runtime, Pose leftDevice, HandState? leftAtRelease = null)
    {
        var left = Hands.Hand(AimedAway(), device: leftDevice);
        var panel = leftDevice.Then(Pose.From(host.Placement.Offset));
        var from = panel.Position + (Vector3.Transform(Vector3.UnitZ, panel.Rotation) * 0.4f);

        runtime.Tracking = Hands.Both(left, Hands.Hand(Hands.AimingAt(from, panel.Position), grab: true));
        host.PollInput(At(0));
        Assert.Equal(Hand.Right, host.Holding);

        var aside = new Vector3(0.1f, 0f, 0f);
        var moved = Hands.AimingAt(from + aside, panel.Position + aside);
        runtime.Tracking = Hands.Both(left, Hands.Hand(moved, grab: true));
        host.PollInput(At(0.033));
        runtime.Tracking = Hands.Both(leftAtRelease ?? left, Hands.Hand(moved));
        host.PollInput(At(0.066));

        Assert.Null(host.Holding);
        return panel;
    }

    private static readonly Pose LeftSomewhere = new(new Vector3(-0.3f, 0.2f, -0.5f), Quaternion.CreateFromAxisAngle(Vector3.UnitY, 0.6f));

    [Fact]
    public void AWristButtonGrabbedByTheRightHandAndLetGoStaysOnTheLeftHandWhereItWasLeft()
    {
        AvaloniaTestHost.Run(() =>
        {
            var (host, runtime) = Build(ButtonPlace.Wrist);
            using var button = host;

            var panel = CarryAside(host, runtime, LeftSomewhere);

            // Measured from the left hand, which is elsewhere and turned: only right if its pose was used.
            var expected = new Pose(panel.Position + new Vector3(0.1f, 0f, 0f), panel.Rotation).RelativeTo(LeftSomewhere);
            Assert.Equal(OverlayAnchor.LeftHand, host.Placement.Anchor);
            Assert.Equal(expected.Position.X, host.Placement.Offset.X, 3);
            Assert.Equal(expected.Position.Y, host.Placement.Offset.Y, 3);
            Assert.Equal(expected.Position.Z, host.Placement.Offset.Z, 3);
            Assert.Equal(OverlayButton.WristWidth, host.Placement.Width, 3);
        });
    }

    [Fact]
    public void AButtonSwitchedToWristWhileRunningStaysOnTheLeftHandWhenLetGo()
    {
        AvaloniaTestHost.Run(() =>
        {
            var (host, runtime) = Build();
            using var button = host;
            host.Place(ButtonPlace.Wrist);

            CarryAside(host, runtime, LeftSomewhere);

            Assert.Equal(OverlayAnchor.LeftHand, host.Placement.Anchor);
        });
    }

    [Fact]
    public void AButtonSwitchedToCornerWhileRunningGoesBackOnTheHeadWhenLetGo()
    {
        AvaloniaTestHost.Run(() =>
        {
            var (host, runtime) = Build(ButtonPlace.Wrist);
            using var button = host;
            host.Place(ButtonPlace.Corner);

            // The corner hangs from the head, which these tests keep at the origin.
            var left = Hands.Hand(AimedAway(), device: Pose.Identity);
            runtime.Tracking = Hands.Both(left, Hands.Hand(Aim(host), grab: true));
            host.PollInput(At(0));
            runtime.Tracking = Hands.Both(left, Hands.Hand(Aim(host)));
            host.PollInput(At(0.033));

            Assert.Equal(OverlayAnchor.Head, host.Placement.Anchor);
        });
    }

    [Fact]
    public void AWristButtonWhoseLeftHandVanishesMidCarryGoesBackToWhereItWasPickedUp()
    {
        AvaloniaTestHost.Run(() =>
        {
            var (host, runtime) = Build(ButtonPlace.Wrist);
            using var button = host;
            var start = host.Placement.Offset;

            CarryAside(host, runtime, LeftSomewhere, leftAtRelease: HandState.Missing);

            Assert.Equal(OverlayAnchor.LeftHand, host.Placement.Anchor);
            Assert.Equal(start, host.Placement.Offset);
            Assert.Equal(OverlayButton.WristWidth, host.Placement.Width, 3);
        });
    }

    [Fact]
    public void ThePlaceSettingStartsFromWhereTheButtonWasLeftInThatPlace()
    {
        AvaloniaTestHost.Run(() =>
        {
            var (host, runtime) = Build();
            using var button = host;
            var left = OverlayButton.ToPlacement(ButtonPlace.Wrist) with { Locked = true, ClickThrough = true };

            host.Place(ButtonPlace.Wrist, left);

            Assert.Equal(left, host.Placement);
            Assert.Equal(OverlayButton.Drawn(left), runtime.Placed[^1]);

            host.Place(ButtonPlace.Corner);

            Assert.Equal(OverlayButton.ToPlacement(ButtonPlace.Corner), host.Placement);
        });
    }

    [Fact]
    public void AStickOnTheHandCarryingTheButtonIsLeftOutOfTheShortcut()
    {
        AvaloniaTestHost.Run(() =>
        {
            var (host, runtime) = Build();
            using var button = host;
            var held = 0;
            host.ShortcutHeld += () => held++;

            runtime.Tracking = Hands.RightOnly(Hands.Hand(Aim(host), grab: true));
            host.PollInput(At(0));
            Assert.Equal(Hand.Right, host.Holding);

            runtime.Tracking = Hands.RightOnly(Hands.Hand(Aim(host), grab: true, scroll: PulledBack));
            host.PollInput(At(1));
            host.PollInput(At(7));

            Assert.Equal(0, held);
            Assert.Null(host.Counting);
        });
    }

    /// <summary>The middle of what is drawn at a target, as fractions across and down, or null when nothing is.</summary>
    private static (float Across, float Down)? Find(ButtonHost host, Func<OverlayTarget?, bool> wanted)
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

    /// <summary>Points at a spot on the button, then clicks it.</summary>
    private static void Press(ButtonHost host, AttachedRuntime runtime, (float Across, float Down) spot, double at)
    {
        // The fractions are of the picture's square, which is bigger than the card.
        var centre = host.Placement.Offset;
        var side = OverlayButton.Drawn(host.Placement).Side;
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
    public void ThePlaceSettingMovesTheButtonToThePlaceItNamesThroughTheRuntime()
    {
        AvaloniaTestHost.Run(() =>
        {
            var (host, runtime) = Build();
            using var button = host;

            Assert.Equal(OverlayAnchor.Head, host.Placement.Anchor);

            host.Place(ButtonPlace.Wrist);

            Assert.Equal(OverlayAnchor.LeftHand, host.Placement.Anchor);
            Assert.Equal(OverlayButton.Drawn(host.Placement), runtime.Placed[^1]);
            Assert.False(host.Placement.Locked);
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
    public void AButtonSwitchedOffIsNotThereForARayOrAGripButTheShortcutStillWorks()
    {
        AvaloniaTestHost.Run(() =>
        {
            var (host, runtime) = Build();
            using var button = host;
            var held = 0;
            var pressed = 0;
            host.ShortcutHeld += () => held++;
            host.Pressed += () => pressed++;
            var spot = host.Placement;

            host.Shown = false;

            Assert.Equal(1, runtime.Hides);

            // A grip and a click on the spot where it was: nothing takes hold, nothing is pressed.
            runtime.Tracking = Hands.RightOnly(Hands.Hand(Aim(host), grab: true, click: true));
            host.PollInput(At(0));
            Assert.Null(host.Holding);
            Assert.Null(host.Busy);
            Assert.Equal(spot, host.Placement);
            Assert.Equal(0, pressed);

            // The shortcut, held for its time, still fires once.
            runtime.Tracking = Sticks(Vector2.Zero, PulledBack);
            host.PollInput(At(1));
            host.PollInput(At(3));
            Assert.Equal(0, held);
            host.PollInput(At(6.5));
            Assert.Equal(1, held);

            // Switched on again: back, drawn, at the same spot.
            host.Shown = true;
            Assert.Equal(1, runtime.Shows);
            Assert.Equal(spot, host.Placement);
        });
    }

    [Fact]
    public void AButtonStartedWhileSwitchedOffStaysDownAndMakesNoTexture()
    {
        AvaloniaTestHost.Run(() =>
        {
            var runtime = new AttachedRuntime();
            using var host = new ButtonHost(runtime, () => new AvaloniaFrameRenderer(Size, Size), () => new FakeSurface());
            host.Shown = false;

            host.Start();

            Assert.Equal(1, runtime.Hides);
            Assert.False(host.IsDrawing);

            host.Shown = true;

            Assert.Equal(1, runtime.Shows);
            Assert.True(host.IsDrawing);
        });
    }

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
