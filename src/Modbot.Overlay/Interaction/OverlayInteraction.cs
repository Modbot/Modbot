using System.Numerics;
using Modbot.Companion.Overlay;

namespace Modbot.Overlay.Interaction;

/// <summary>A hand pointing at the panel.</summary>
/// <param name="Hand">Which hand.</param>
/// <param name="Across">0 at the panel's left edge, 1 at its right.</param>
/// <param name="Down">0 at the top, 1 at the bottom.</param>
public readonly record struct Pointer(Hand Hand, float Across, float Down);

/// <summary>What one poll of the controllers came to.</summary>
/// <param name="Pointer">The hand pointing at the panel, or null.</param>
/// <param name="Placement">The placement after this poll.</param>
/// <param name="PlacementChanged">Whether it differs from before, so the caller applies and saves it.</param>
/// <param name="Clicks">Trigger presses on the panel, as fractions across and down.</param>
/// <param name="Scroll">Scrolling while pointing and not holding, for the roster; zero otherwise.</param>
/// <param name="Holding">The hand holding the panel, or null.</param>
public sealed record InteractionResult(
    Pointer? Pointer,
    OverlayPlacement Placement,
    bool PlacementChanged,
    IReadOnlyList<Pointer> Clicks,
    Vector2 Scroll,
    Hand? Holding);

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
/// <item><strong>Head lock.</strong> Two grips on the panel within <see cref="DoubleTap"/> put it
/// back in front of the head at the default offset, so a lost panel can always be found.</item>
/// <item><strong>Click.</strong> The trigger while pointing is a click at that spot.</item>
/// <item><strong>The hand wearing the panel is left out of all of it.</strong> See
/// <see cref="Ignoring"/>.</item>
/// </list>
/// </remarks>
public sealed class OverlayInteraction
{
    /// <summary>Let go this close to the hand, and the panel stays on the hand.</summary>
    public const float WristReach = 0.10f;

    /// <summary>Two grips on the panel this close together put it back in front of the head.</summary>
    public static readonly TimeSpan DoubleTap = TimeSpan.FromMilliseconds(500);

    /// <summary>Metres per poll at full scroll, pushing or pulling.</summary>
    public const float DistanceStep = 0.01f;

    /// <summary>Metres of width per poll at full scroll.</summary>
    public const float WidthStep = 0.005f;

    private OverlayPlacement _placement;
    private OverlayTracking _last = OverlayTracking.None;
    private Hand? _holding;
    private Pose _heldOffset;
    private readonly Dictionary<Hand, TimeSpan> _lastGrip = new();

    public OverlayInteraction(OverlayPlacement placement)
    {
        ArgumentNullException.ThrowIfNull(placement);
        _placement = placement.Clamped();
    }

    public OverlayPlacement Placement => _placement;

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
    }

    /// <param name="now">Any steadily rising clock; only differences are used.</param>
    public InteractionResult Update(OverlayTracking tracking, TimeSpan now)
    {
        var before = _placement;
        var clicks = new List<Pointer>();
        var scroll = Vector2.Zero;
        Pointer? pointer = null;

        if (_holding is { } holding)
        {
            var hand = tracking[holding];

            if (!hand.Tracked || !hand.Grab)
            {
                LetGo(holding, hand);
            }
            else
            {
                Hold(hand);
            }
        }

        if (_holding is null)
        {
            pointer = Point(tracking);

            if (pointer is { } pointing)
            {
                var hand = tracking[pointing.Hand];
                var was = _last[pointing.Hand];

                if (hand.Grab && !was.Grab)
                {
                    if (_lastGrip.TryGetValue(pointing.Hand, out var previous) && now - previous <= DoubleTap)
                    {
                        // Off the wrist and back in front of the head, at a width somebody can
                        // read a roster on again rather than the wrist size it was wearing.
                        _placement = OverlayPlacement.Default with
                        {
                            Width = OverlayPlacement.WidthFor(OverlayAnchor.Head, _placement.Width),
                            Opacity = _placement.Opacity,
                            Curve = _placement.Curve,
                        };
                        _lastGrip.Remove(pointing.Hand);
                        pointer = null;
                    }
                    else
                    {
                        _lastGrip[pointing.Hand] = now;
                        Take(pointing.Hand, hand, tracking);
                    }
                }
                else if (hand.Click && !was.Click)
                {
                    clicks.Add(pointing);
                }
                else
                {
                    scroll = hand.Scroll;
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

        return new InteractionResult(pointer, _placement, _placement != before, clicks, scroll, _holding);
    }

    /// <remarks>
    /// Everything a hand can do to the panel — point, tap, scroll, grab, the double grip that
    /// sends it home — is reached through being the pointer, so leaving the worn hand out here
    /// leaves it out of all of them, and there is no second place for the rule to be got wrong.
    /// </remarks>
    private Pointer? Point(OverlayTracking tracking)
    {
        if (PanelGeometry.PanelPose(_placement, tracking) is not { } panel)
            return null;

        var ignoring = Ignoring;
        Pointer? best = null;
        var nearest = float.MaxValue;

        foreach (var side in new[] { Hand.Left, Hand.Right })
        {
            if (side == ignoring)
                continue;

            var hand = tracking[side];
            if (!hand.Tracked)
                continue;

            if (PanelGeometry.Hit(panel, _placement.Width, hand.Aim) is { IsOnPanel: true } hit && hit.Distance < nearest)
            {
                nearest = hit.Distance;
                best = new Pointer(side, hit.Across, hit.Down);
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
        _holding = side;
        _heldOffset = panel.RelativeTo(hand.Device);
        _placement = _placement with
        {
            Anchor = side == Hand.Left ? OverlayAnchor.LeftHand : OverlayAnchor.RightHand,
            Offset = _heldOffset.ToOverlayPose(),
        };
    }

    private void Hold(HandState hand)
    {
        // Pushing and pulling slide the panel along the line from the hand to it, never through
        // the hand: the distance changes and the direction stays.
        if (hand.Scroll.Y != 0f && _heldOffset.Position.Length() > 0f)
        {
            var direction = Vector3.Normalize(_heldOffset.Position);
            var distance = Math.Clamp(
                _heldOffset.Position.Length() + (hand.Scroll.Y * DistanceStep),
                OverlayPlacement.MinDistance,
                OverlayPlacement.MaxDistance);
            _heldOffset = _heldOffset with { Position = direction * distance };
        }

        var width = _placement.Width;
        if (hand.Scroll.X != 0f)
            width = Math.Clamp(width + (hand.Scroll.X * WidthStep), OverlayPlacement.MinWidth, OverlayPlacement.MaxWidth);

        _placement = _placement with { Offset = _heldOffset.ToOverlayPose(), Width = width };
    }

    private void LetGo(Hand side, HandState hand)
    {
        _holding = null;

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
            };
            return;
        }

        _placement = _placement with { Anchor = OverlayAnchor.World, Offset = panel.ToOverlayPose() };
    }
}
