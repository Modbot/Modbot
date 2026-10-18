using System.Numerics;
using Modbot.Companion.Overlay;

namespace Modbot.Overlay.Interaction;

/// <summary>A hand pointing at the panel.</summary>
/// <param name="Hand">Which hand.</param>
/// <param name="Across">0 at the panel's left edge, 1 at its right.</param>
/// <param name="Down">0 at the top, 1 at the bottom.</param>
public readonly record struct Pointer(Hand Hand, float Across, float Down);

/// <summary>
/// A trigger held down on a list and dragged, as a phone's finger is: how far the ray moved
/// down the panel since the last poll. The list follows it.
/// </summary>
/// <param name="From">Where the trigger went down, which is how the host knows which list it is.</param>
/// <param name="Down">How far the ray moved down this poll, as a fraction of the panel's height; negative is up.</param>
public readonly record struct PanelDrag(Pointer From, float Down);

/// <summary>What one poll of the controllers came to.</summary>
/// <param name="Pointer">The hand pointing at the panel, or null.</param>
/// <param name="Placement">The placement after this poll.</param>
/// <param name="PlacementChanged">Whether it differs from before, so the caller applies and saves it.</param>
/// <param name="Clicks">Trigger presses on the panel, as fractions across and down.</param>
/// <param name="Scroll">Scrolling with the right stick while pointing and not holding, for the lists; zero otherwise.</param>
/// <param name="Holding">The hand holding the panel, or null.</param>
/// <param name="Passing">
/// A ray on a panel that lets rays through, away from its bar: nothing happens on the panel, but
/// the bar is shown so it can be switched back. Null otherwise.
/// </param>
/// <param name="Drag">The trigger held down and dragged on a list this poll, or null.</param>
/// <param name="Touched">The trigger went down on a list this poll, before it is known whether it is a tap or a drag.</param>
public sealed record InteractionResult(
    Pointer? Pointer,
    OverlayPlacement Placement,
    bool PlacementChanged,
    IReadOnlyList<Pointer> Clicks,
    Vector2 Scroll,
    Hand? Holding,
    Pointer? Passing = null,
    PanelDrag? Drag = null,
    bool Touched = false)
{
    /// <summary>Whether any ray is on the panel, used or let through: while one is, the bar is up.</summary>
    public bool RayOnPanel => Pointer is not null || Passing is not null;
}

/// <summary>
/// The rules for holding the panel, from controller state in to placement and clicks out.
/// </summary>
/// <remarks>
/// <para>Runtime-free on purpose: OpenVR and OpenXR each fill an <see cref="OverlayTracking"/> and
/// apply an <see cref="OverlayPlacement"/>, and everything between is here, tested with hands
/// made up in code (overlay OpenXR and interaction design §4.3).</para>
/// <list type="bullet">
/// <item><strong>Pointing.</strong> The nearer hand whose ray lands on the panel is the pointer.</item>
/// <item><strong>Grab.</strong> Grip pressed while pointing takes the panel; it follows that hand,
/// keeping the offset it was taken at. Letting go leaves it anchored to the world, or, if it was
/// let go within <see cref="WristReach"/> of the hand, puts it on that wrist — at the wrist
/// position and the wrist size, because a panel on a wrist is a thing you glance at and not a
/// workspace parked on your arm.</item>
/// <item><strong>Resize and distance.</strong> While held, scrolling up and down pushes and pulls the
/// panel along the hand's ray; left and right changes its width. Both stay inside the placement's
/// bounds.</item>
/// <item><strong>Two-handed resize.</strong> While one hand carries it, the other gripping on the
/// panel sizes it by how far apart the two hands are.</item>
/// <item><strong>The resize corner.</strong> A trigger pressed on the corner at the panel's bottom right
/// (<see cref="IsOnCorner"/>, only drawn in edit mode) and held while the ray is dragged makes the panel taller
/// and wider, from 1 to 2 times its ordinary shape. The panel's top left stays where it is in the room and the
/// bottom right follows the ray; the words keep their size and the panel shows more. A locked panel, a
/// click-through one and one being carried cannot be resized.</item>
/// <item><strong>Head lock.</strong> Two grips on the panel within <see cref="DoubleTap"/> put it
/// back in front of the head at the default offset, so a lost panel can always be found.</item>
/// <item><strong>Click.</strong> The trigger while pointing is a click at that spot. On a list
/// (<see cref="IsScrollableAt"/>) it is a click when the trigger comes up without the ray having
/// moved more than <see cref="DragDeadZone"/>, and a drag otherwise: the list follows the ray like
/// a phone's, and letting go does not click.</item>
/// <item><strong>Scrolling.</strong> The right stick scrolls the list under the ray, whichever hand
/// holds the ray. The left stick never does: it is for moving in VRChat.</item>
/// <item><strong>The hand wearing the panel is left out of all of it.</strong> See
/// <see cref="Ignoring"/>.</item>
/// <item><strong>Locked.</strong> A locked panel cannot be taken, so nothing that needs it held —
/// carrying, either stick pushing, pulling or resizing, the second hand's stretch, the double grip
/// home — can happen. Pointing, clicking and scrolling go on as before.</item>
/// <item><strong>Click-through.</strong> A panel letting rays through does nothing at all for a ray
/// that lands on it, except on its bar (<see cref="IsOnBar"/>), where a click still lands so the
/// switch can be turned back off. It is never picked up while it lets rays through.</item>
/// </list>
/// </remarks>
public sealed class OverlayInteraction
{
    /// <summary>Let go this close to the hand, and the panel stays on the hand.</summary>
    public const float WristReach = 0.10f;

    /// <summary>Two grips on the panel this close together put it back in front of the head.</summary>
    public static readonly TimeSpan DoubleTap = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// Metres per poll at full push, pushing or pulling a carried panel, when nobody has said
    /// otherwise: about 1 m a second at the thirty polls a second the host makes. It was a third
    /// of that, which the owner found too slow to be worth reaching for. It is the window's Push
    /// speed default (3) turned into metres, <c>CompanionSettings.PushStep(3)</c>.
    /// </summary>
    public const float DefaultPushStep = 0.0345f;

    /// <summary>The slowest and fastest the window's Push speed slider can set, in metres per poll.</summary>
    public const float MinPushStep = 0.01f;

    public const float MaxPushStep = 0.115f;

    /// <summary>Metres per poll at full push, from the window's Push speed slider.</summary>
    public float PushStep { get; set; } = DefaultPushStep;

    /// <summary>
    /// How far a ray may wander, as a fraction of the panel, between the trigger going down and
    /// coming up on a list and still be a tap. A hand is never quite still.
    /// </summary>
    public const float DragDeadZone = 0.015f;

    /// <summary>Metres of width per poll at full scroll.</summary>
    public const float WidthStep = 0.005f;

    /// <summary>
    /// How far a thumbstick must be pushed before it counts. Legacy controller state is the raw
    /// axis, and a stick at rest reads a little off centre — enough, polled thirty times a second,
    /// to walk a held panel away and shrink it with nobody touching anything.
    /// </summary>
    public const float StickDeadZone = 0.2f;

    /// <summary>
    /// A stick with the dead zone taken out: zero inside it, and from there the rest of the travel
    /// stretched back to the whole range, so the edge of the dead zone is a gentle start rather
    /// than a jump.
    /// </summary>
    public static Vector2 Stick(Vector2 raw)
    {
        static float One(float value)
        {
            var size = MathF.Abs(value);
            return size <= StickDeadZone
                ? 0f
                : MathF.Sign(value) * Math.Min(1f, (size - StickDeadZone) / (1f - StickDeadZone));
        }

        return new Vector2(One(raw.X), One(raw.Y));
    }

    /// <summary>
    /// The stick that pushes, pulls and resizes a carried panel: the first hand's, and the second
    /// hand's when the first is at rest. A moderator carrying the panel in the left hand reaches for
    /// the right stick, and a panel that only listened to the carrying hand did nothing for them.
    /// </summary>
    /// <param name="first">The hand whose stick is read first.</param>
    /// <param name="second">The other hand, with <c>Tracked</c> false when it is not to be read.</param>
    public static Vector2 EitherStick(HandState first, HandState second)
    {
        var stick = Stick(first.Scroll);
        return stick == Vector2.Zero && second.Tracked ? Stick(second.Scroll) : stick;
    }

    /// <summary>A trigger that went down on a list and has not come up: a tap or a drag, not decided yet.</summary>
    private sealed record Press(Hand Hand, Pointer From, float LastDown, bool Dragging);

    /// <summary>A trigger held on the resize corner: which hand, where it took hold, and the shape then.</summary>
    /// <param name="Start">Where the ray was, in metres right of and below the panel's top left.</param>
    private sealed record Resizing(Hand Hand, Vector2 Start, float Wide, float Tall);

    private Press? _press;
    private Resizing? _resizing;
    private OverlayPlacement _placement;
    private OverlayTracking _last = OverlayTracking.None;
    private Hand? _holding;
    private Pose _heldOffset;

    /// <summary>
    /// The second hand's hold while the panel is being carried: how far apart the two hands were,
    /// and how wide the panel was, when it took hold. Null when only one hand has it.
    /// </summary>
    private Stretching? _stretching;

    private sealed record Stretching(float Distance, float Width);
    private readonly Dictionary<Hand, TimeSpan> _lastGrip = new();

    /// <summary>Where the panel was before it was taken, for a head-fixed panel whose hand vanishes.</summary>
    private OverlayPlacement _beforeTake;

    private readonly bool _homeResetsWidth;

    /// <summary>
    /// The anchor a kept panel goes back on when it is let go; see the constructor. Settable because
    /// a button's place can change while it is running.
    /// </summary>
    public OverlayAnchor? KeepAnchor { get; set; }

    /// <param name="placement">Where the panel starts.</param>
    /// <param name="home">
    /// Where two quick grips send it. Null is the main panel's own home in front of the head.
    /// </param>
    /// <param name="keepAnchor">
    /// A panel that stays on one anchor, as the notification panel stays on the head: let go, it
    /// stays where it was left <em>relative to that anchor</em> (the head or one hand) rather than
    /// in the room, and it never goes on a wrist or into the room. Null is the main panel's free
    /// rule.
    /// </param>
    /// <param name="homeResetsWidth">
    /// Whether two quick grips also put a kept panel back at its home's width. Off keeps the width
    /// it has, as the pop-up does.
    /// </param>
    public OverlayInteraction(
        OverlayPlacement placement,
        OverlayPlacement? home = null,
        OverlayAnchor? keepAnchor = null,
        bool homeResetsWidth = false)
    {
        ArgumentNullException.ThrowIfNull(placement);
        _placement = placement.Clamped();
        _beforeTake = _placement;
        Home = home;
        KeepAnchor = keepAnchor;
        _homeResetsWidth = homeResetsWidth;
    }

    public OverlayPlacement Placement => _placement;

    /// <summary>Where two quick grips send the panel; null is in front of the head at the default offset.</summary>
    public OverlayPlacement? Home { get; set; }

    /// <summary>
    /// Whether a point on the panel, as fractions across and down, is on its bar. Set by the host,
    /// which has the drawn frame to look in; without it nothing counts as the bar.
    /// </summary>
    public Func<float, float, bool>? IsOnBar { get; set; }

    /// <summary>
    /// Whether a point on the panel, as fractions across and down, is on its resize corner. Set by the
    /// host, which has the drawn frame to look in; without it there is no corner to take hold of.
    /// </summary>
    public Func<float, float, bool>? IsOnCorner { get; set; }

    /// <summary>
    /// How much of the panel's width the shape it is resized by is measured against: 1 for a panel
    /// whose whole width is what grows, and less for the pop-up panel, whose box is a share of it.
    /// </summary>
    public float ShapeShare { get; set; } = 1f;

    /// <summary>
    /// Whether anything is drawn at a point across and down the panel, asked by the host that drew
    /// it. A ray on the clear ground of the square counts as missing the panel, so the cursor and
    /// the bar go the moment a hand moves off what can be seen. Null counts the whole square.
    /// </summary>
    public Func<float, float, bool>? IsDrawnAt { get; set; }

    /// <summary>
    /// Whether a point across and down the panel is on a list that scrolls, asked by the host that
    /// drew it. A trigger pressed there waits to see whether it is a tap or a drag; anywhere else
    /// it clicks the moment it goes down, as it always did. Null means nothing scrolls.
    /// </summary>
    public Func<float, float, bool>? IsScrollableAt { get; set; }

    /// <summary>
    /// The hand the panel is worn on, which is left out of pointing, tapping, scrolling and
    /// grabbing. Null unless the panel is sitting on a wrist.
    /// </summary>
    /// <remarks>
    /// <para><strong>Why a hand is ignored at all.</strong> A panel worn on a wrist sits where that
    /// hand is, so that hand's ray lands on it constantly — the cursor parks itself on the panel
    /// and will not leave, and any squeeze of that hand takes hold of a panel that is already
    /// travelling with it. Both are useless, and the second is worse than useless: a moderator
    /// squeezes their grip all day for reasons that have nothing to do with an overlay, and every
    /// one of those was tearing the panel off its own wrist.</para>
    /// <para><strong>It follows the anchor, not a gesture.</strong> <c>LeftHand</c> ignores the
    /// left, <c>RightHand</c> ignores the right, and the head and the room ignore neither. The
    /// anchor is a setting, so the rule is steady rather than something that comes and goes with
    /// how the hands happen to be held.</para>
    /// <para><strong>Never the hand carrying it.</strong> While the panel is being carried it is
    /// anchored to the hand doing the carrying, and that hand must go on being read or the carry
    /// could not be ended. Carrying is the one thing the anchored hand is allowed to do, so this
    /// is null for as long as it lasts.</para>
    /// <para><strong>How a panel leaves a wrist.</strong> The other hand points at it and grips,
    /// exactly as it would anywhere else; two quick grips from that other hand send it back in
    /// front of the head. Failing either, the settings page moves it with no controller at all.
    /// The worn hand is never the way off, which is the point.</para>
    /// </remarks>
    public Hand? Ignoring => _holding is not null
        ? null
        : _placement.Anchor switch
        {
            OverlayAnchor.LeftHand => Hand.Left,
            OverlayAnchor.RightHand => Hand.Right,
            _ => null,
        };

    /// <summary>Replaces the placement from outside, as the settings page does.</summary>
    public void Place(OverlayPlacement placement)
    {
        ArgumentNullException.ThrowIfNull(placement);
        _placement = placement.Clamped();
        _holding = null;
        _stretching = null;
        _press = null;
        _resizing = null;
    }

    /// <param name="now">Any steadily rising clock; only differences are used.</param>
    /// <param name="elsewhere">
    /// A hand busy with another panel drawn over this one, left out of pointing here so one press
    /// does not land on both. Null leaves both hands in.
    /// </param>
    public InteractionResult Update(OverlayTracking tracking, TimeSpan now, Hand? elsewhere = null)
    {
        var before = _placement;
        var clicks = new List<Pointer>();
        var scroll = Vector2.Zero;
        Pointer? pointer = null;
        Pointer? passing = null;
        PanelDrag? drag = null;
        var touched = false;

        if (_holding is { } holding)
        {
            var hand = tracking[holding];

            if (!hand.Tracked || !hand.Grab || _placement.Locked)
            {
                LetGo(holding, hand, tracking);
            }
            else
            {
                Hold(hand, tracking[holding == Hand.Left ? Hand.Right : Hand.Left]);
                Stretch(holding, tracking);
            }
        }

        if (_resizing is not null)
            FollowResize(tracking, elsewhere);

        if (_press is not null)
            drag = FollowPress(tracking, elsewhere, clicks);

        if (_holding is null && Point(tracking, elsewhere) is { } pointing)
        {
            var onBar = IsOnBar?.Invoke(pointing.Across, pointing.Down) ?? false;

            if (_placement.ClickThrough && !onBar)
            {
                // Through to VRChat: no cursor, no click, no grip. Only the bar comes up.
                passing = pointing;
            }
            else
            {
                pointer = pointing;

                var hand = tracking[pointing.Hand];
                var was = _last[pointing.Hand];
                var canMove = !_placement.Locked && !_placement.ClickThrough;

                if (_resizing is not null)
                {
                    // The corner is being dragged: the hand on it is only pointing, and the other
                    // hand has nothing to do to the panel until it is let go.
                    if (pointing.Hand != _resizing.Hand)
                        pointer = null;
                }
                else if (hand.Grab && !was.Grab && canMove)
                {
                    if (_lastGrip.TryGetValue(pointing.Hand, out var previous) && now - previous <= DoubleTap)
                    {
                        GoHome();
                        _lastGrip.Remove(pointing.Hand);
                        pointer = null;
                    }
                    else
                    {
                        _lastGrip[pointing.Hand] = now;
                        Take(pointing.Hand, hand, tracking);
                        _press = null;
                    }
                }
                else if (hand.Click && !was.Click)
                {
                    if (canMove && (IsOnCorner?.Invoke(pointing.Across, pointing.Down) ?? false))
                    {
                        var side = _placement.Side;
                        _resizing = new Resizing(pointing.Hand, new Vector2(pointing.Across * side, pointing.Down * side), _placement.Wide, _placement.Tall);
                    }
                    else if (IsScrollableAt?.Invoke(pointing.Across, pointing.Down) ?? false)
                    {
                        // On a list the press is held back until it is known whether it is a tap.
                        _press ??= new Press(pointing.Hand, pointing, pointing.Down, false);
                        touched = true;
                    }
                    else
                    {
                        clicks.Add(pointing);
                    }
                }
                else if (!_placement.ClickThrough)
                {
                    scroll = RightStick(tracking, elsewhere);
                }
            }
        }

        // Every hand, including one being ignored. A grab is the moment a grip closes, not the
        // fact that it is closed, and a hand wearing the panel is usually mid-squeeze when the
        // panel moves off it — remembering that squeeze is what stops the panel leaping straight
        // back into a hand that never asked for it. That hand takes the panel on its next fresh
        // squeeze, like any other.
        _last = tracking;
        _placement = _placement.Clamped();

        return new InteractionResult(pointer, _placement, _placement != before, clicks, scroll, _holding, passing, drag, touched);
    }

    /// <summary>
    /// The right stick, which is the only one that scrolls a list: the left stick is for moving in
    /// VRChat. Zero when the right hand is worn on the panel's wrist (<see cref="Ignoring"/>) or
    /// busy with another panel, which are left out of scrolling as they are of everything else.
    /// </summary>
    private Vector2 RightStick(OverlayTracking tracking, Hand? elsewhere)
    {
        var right = tracking[Hand.Right];
        return !right.Tracked || Ignoring == Hand.Right || elsewhere == Hand.Right
            ? Vector2.Zero
            : Stick(right.Scroll);
    }

    /// <summary>
    /// Follows a trigger that went down on a list: once the ray has moved more than the dead zone
    /// it is a drag and every poll after reports how far the ray moved down; coming up before that
    /// it is a tap, clicked where it went down. Anything that takes the hand away from the list
    /// drops it without a click.
    /// </summary>
    private PanelDrag? FollowPress(OverlayTracking tracking, Hand? elsewhere, List<Pointer> clicks)
    {
        var press = _press!;
        var hand = tracking[press.Hand];

        if (_holding is not null || !hand.Tracked || press.Hand == elsewhere || _placement.ClickThrough)
        {
            _press = null;
            return null;
        }

        if (!hand.Click)
        {
            if (!press.Dragging)
                clicks.Add(press.From);

            _press = null;
            return null;
        }

        // The ray may wander off the panel mid-drag and the list still follows it, so the plane is
        // measured and not the square.
        if (PanelGeometry.PanelPose(_placement, tracking) is not { } panel
            || PanelGeometry.Hit(panel, _placement.Side, hand.Aim) is not { } hit)
        {
            // The press is still alive: a drag in progress goes on, standing still, so the host
            // does not take the ray missing the plane for the trigger coming up.
            return press.Dragging ? new PanelDrag(press.From, 0f) : null;
        }

        var dragging = press.Dragging
            || Vector2.Distance(new Vector2(hit.Across, hit.Down), new Vector2(press.From.Across, press.From.Down)) > DragDeadZone;

        _press = press with { LastDown = dragging ? hit.Down : press.LastDown, Dragging = dragging };

        return dragging ? new PanelDrag(press.From, hit.Down - press.LastDown) : null;
    }

    /// <summary>
    /// Back to where the panel lives: for the main panel, off the wrist and in front of the head at
    /// a width somebody can read a roster on again rather than the wrist size it was wearing.
    /// </summary>
    private void GoHome()
    {
        var home = Home ?? OverlayPlacement.Default;
        _placement = home with
        {
            Width = KeepAnchor is null
                ? OverlayPlacement.WidthFor(OverlayAnchor.Head, _placement.Width)
                : _homeResetsWidth ? home.Width : _placement.Width,
            Opacity = _placement.Opacity,
            Curve = _placement.Curve,
            Locked = _placement.Locked,
            ClickThrough = _placement.ClickThrough,

            // The shape is kept like the other things the grips do not undo, except on a pop-up
            // whose home is its size too.
            Wide = KeepAnchor is not null && _homeResetsWidth ? home.Wide : _placement.Wide,
            Tall = KeepAnchor is not null && _homeResetsWidth ? home.Tall : _placement.Tall,
        };
    }

    /// <summary>
    /// A trigger held on the resize corner: while it is down the panel is as wide and as tall as the
    /// ray has dragged the corner from where it took hold, and its top left stays where it is in the room.
    /// It ends with the trigger, and with anything that takes the hand away from the corner or the corner
    /// away from the panel.
    /// </summary>
    private void FollowResize(OverlayTracking tracking, Hand? elsewhere)
    {
        var resizing = _resizing!;
        var hand = tracking[resizing.Hand];

        if (_holding is not null || !hand.Tracked || !hand.Click || resizing.Hand == elsewhere
            || _placement.Locked || _placement.ClickThrough)
        {
            _resizing = null;
            return;
        }

        // The ray may wander off the panel mid-drag and the corner still follows it, so the plane is
        // measured and not the square. Off the plane altogether the panel stays as it is.
        var side = _placement.Side;
        if (PanelGeometry.PanelPose(_placement, tracking) is not { } panel
            || PanelGeometry.Hit(panel, side, hand.Aim) is not { } hit)
            return;

        var share = Math.Max(_placement.Width * ShapeShare, 1e-4f);
        var wide = Math.Clamp(resizing.Wide + (((hit.Across * side) - resizing.Start.X) / share), OverlayPlacement.MinShape, OverlayPlacement.MaxShape);
        var tall = Math.Clamp(resizing.Tall + (((hit.Down * side) - resizing.Start.Y) / share), OverlayPlacement.MinShape, OverlayPlacement.MaxShape);

        var grown = _placement with { Wide = wide, Tall = tall };

        // The panel's middle is half a side from its top left, so when the side grows the middle moves
        // by half of it, right and down along the panel's own axes, and the top left stays put.
        var change = grown.Side - side;
        if (change == 0f)
        {
            _placement = grown;
            return;
        }

        var turn = new Quaternion(_placement.Offset.QX, _placement.Offset.QY, _placement.Offset.QZ, _placement.Offset.QW);
        var shift = Vector3.Transform(new Vector3(change / 2f, -change / 2f, 0f), Quaternion.Normalize(turn));
        _placement = grown with
        {
            Offset = _placement.Offset with
            {
                X = _placement.Offset.X + shift.X,
                Y = _placement.Offset.Y + shift.Y,
                Z = _placement.Offset.Z + shift.Z,
            },
        };
    }

    /// <remarks>
    /// Everything a hand can do to the panel — point, tap, scroll, grab, the double grip that
    /// sends it home — is reached through being the pointer, so leaving the worn hand out here
    /// leaves it out of all of them, and there is no second place for the rule to be got wrong.
    /// </remarks>
    private Pointer? Point(OverlayTracking tracking, Hand? elsewhere)
    {
        if (PanelGeometry.PanelPose(_placement, tracking) is not { } panel)
            return null;

        var ignoring = Ignoring;
        Pointer? best = null;
        var nearest = float.MaxValue;

        foreach (var side in new[] { Hand.Left, Hand.Right })
        {
            if (side == ignoring || side == elsewhere)
                continue;

            var hand = tracking[side];
            if (!hand.Tracked)
                continue;

            // The hand dragging a list or the corner keeps the pointer, even with the other hand nearer.
            if (PanelGeometry.Hit(panel, _placement.Side, hand.Aim) is { IsOnPanel: true } hit
                && (IsDrawnAt?.Invoke(hit.Across, hit.Down) ?? true))
            {
                var distance = side == _press?.Hand || side == _resizing?.Hand ? -1f : hit.Distance;
                if (distance < nearest)
                {
                    nearest = distance;
                    best = new Pointer(side, hit.Across, hit.Down);
                }
            }
        }

        return best;
    }

    private void Take(Hand side, HandState hand, OverlayTracking tracking)
    {
        if (PanelGeometry.PanelPose(_placement, tracking) is not { } panel)
            return;

        // Held against the controller itself rather than against where it points, because that is
        // what a hand anchor means to both runtimes: the panel has to end up where it was carried.
        _beforeTake = _placement;
        _resizing = null;
        _holding = side;
        _heldOffset = panel.RelativeTo(hand.Device);
        _placement = _placement with
        {
            Anchor = side == Hand.Left ? OverlayAnchor.LeftHand : OverlayAnchor.RightHand,
            Offset = _heldOffset.ToOverlayPose(),
        };
    }

    /// <remarks>
    /// Either stick drives it: the carrying hand's first, and the other hand's when that one is
    /// at rest. A moderator carrying the panel in the left hand reaches for the right stick to
    /// push it away, and a panel that only listened to the carrying hand did nothing for them.
    /// </remarks>
    private void Hold(HandState hand, HandState other)
    {
        // Never reached while locked, since a locked panel is let go of before this; said here too
        // because this is where the sticks move it.
        if (_placement.Locked)
            return;

        var stick = EitherStick(hand, other);

        // Pushing and pulling slide the panel along the line from the hand to it, never through
        // the hand: the distance changes and the direction stays.
        if (stick.Y != 0f && _heldOffset.Position.Length() > 0f)
        {
            var direction = Vector3.Normalize(_heldOffset.Position);
            var distance = Math.Clamp(
                _heldOffset.Position.Length() + (stick.Y * Math.Clamp(PushStep, MinPushStep, MaxPushStep)),
                OverlayPlacement.MinDistance,
                OverlayPlacement.MaxDistance);
            _heldOffset = _heldOffset with { Position = direction * distance };
        }

        var width = _placement.Width;
        if (stick.X != 0f)
            width = Math.Clamp(width + (stick.X * WidthStep), OverlayPlacement.MinWidth, OverlayPlacement.MaxWidth);

        _placement = _placement with { Offset = _heldOffset.ToOverlayPose(), Width = width };
    }

    /// <summary>
    /// Two hands on the panel: the carrying hand holds it where it is, and the other hand, gripping
    /// on the panel, sizes it by how far apart the two are — pull apart to make it bigger, bring
    /// them together to make it smaller, the way XSOverlay's windows are sized.
    /// </summary>
    /// <remarks>
    /// <para>The second hand takes hold with a fresh squeeze while the first is carrying, wherever
    /// it is pointing. From then the width follows the ratio of the distance between the controllers to
    /// what it was at that squeeze, so the panel keeps the size it had until the hands move, and a
    /// small hand movement is a small change. Letting go with the second hand keeps the new size
    /// and leaves the first still carrying; letting go with the first ends both.</para>
    /// <para>The thumbstick still resizes as well. Two hands is the quick way to a very different
    /// size; the stick is the fine one.</para>
    /// </remarks>
    private void Stretch(Hand holding, OverlayTracking tracking)
    {
        if (_placement.Locked)
            return;

        var otherSide = holding == Hand.Left ? Hand.Right : Hand.Left;
        var other = tracking[otherSide];
        var carrying = tracking[holding];

        if (_stretching is { } stretching)
        {
            if (!other.Tracked || !other.Grab)
            {
                _stretching = null;
                return;
            }

            var apart = Vector3.Distance(carrying.Device.Position, other.Device.Position);
            _placement = _placement with
            {
                Width = Math.Clamp(
                    stretching.Width * apart / stretching.Distance,
                    OverlayPlacement.MinWidth,
                    OverlayPlacement.MaxWidth),
            };
            return;
        }

        // A fresh squeeze, not one held since before: a hand that was already gripping when the
        // panel was picked up is not asking to stretch it.
        if (!other.Tracked || !other.Grab || _last[otherSide].Grab)
            return;

        // Anywhere: the ray does not have to be on the panel. Aiming the second hand at a panel
        // the first is already carrying proved fiddly, and a fresh squeeze of the free hand while
        // carrying is not something anybody does by accident.
        var start = Vector3.Distance(carrying.Device.Position, other.Device.Position);

        // Hands on top of each other give no distance to measure a stretch against.
        if (start >= MinStretchDistance)
            _stretching = new Stretching(start, _placement.Width);
    }

    /// <summary>How far apart the two controllers must be when the second one takes hold.</summary>
    public const float MinStretchDistance = 0.05f;

    private void LetGo(Hand side, HandState hand, OverlayTracking tracking)
    {
        _holding = null;
        _stretching = null;

        if (KeepAnchor is { } keep)
        {
            // A kept panel goes back on its anchor, where the hand left it as seen from the head or
            // from the worn hand. A hand that vanished mid-carry, or an anchor with nothing to
            // measure from, leaves nothing to work with, so it goes back to where it was picked up
            // from, at the size it has been given.
            Pose? from = keep switch
            {
                OverlayAnchor.LeftHand => tracking[Hand.Left].Tracked ? tracking[Hand.Left].Device : null,
                OverlayAnchor.RightHand => tracking[Hand.Right].Tracked ? tracking[Hand.Right].Device : null,
                _ => tracking.Head,
            };

            _placement = hand.Tracked && from is { } origin
                ? _placement with { Anchor = keep, Offset = hand.Device.Then(_heldOffset).RelativeTo(origin).ToOverlayPose() }
                : _beforeTake with { Width = _placement.Width, Wide = _placement.Wide, Tall = _placement.Tall };
            return;
        }

        if (!hand.Tracked)
        {
            // The hand vanished mid-hold: the panel stays on it, where it will be when it is back.
            return;
        }

        var panel = hand.Device.Then(_heldOffset);

        if (Vector3.Distance(panel.Position, hand.Device.Position) <= WristReach)
        {
            // Brought to the wrist, so it becomes the wrist panel: the watch position and the
            // watch size, not wherever the hand happened to stop.
            _placement = _placement with
            {
                Anchor = side == Hand.Left ? OverlayAnchor.LeftHand : OverlayAnchor.RightHand,
                Offset = OverlayPlacement.WristOffset,
                Width = OverlayPlacement.WristWidth,

                // A wrist panel is a glance, not a workspace, and the corner is not drawn there.
                Wide = 1f,
                Tall = 1f,
            };
            return;
        }

        _placement = _placement with { Anchor = OverlayAnchor.World, Offset = panel.ToOverlayPose() };
    }
}
