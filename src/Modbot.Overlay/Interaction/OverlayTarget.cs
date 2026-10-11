using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;

namespace Modbot.Overlay.Interaction;

/// <summary>Something on the panel a click can land on.</summary>
/// <remarks>
/// The view marks a control by putting one of these in its <c>Tag</c>; nothing else about the
/// tree is interpreted. There is deliberately no target that acts on a person: the companion
/// observes and reports and never acts (M3 §10).
/// </remarks>
public abstract record OverlayTarget
{
    /// <summary>The alert card. Tapping it waves the alert away.</summary>
    public sealed record DismissAlert : OverlayTarget;

    /// <summary>A roster row. Tapping it opens that person's card.</summary>
    public sealed record Person(string SubjectId) : OverlayTarget;

    /// <summary>The open person card. Tapping it closes it.</summary>
    public sealed record ClosePerson : OverlayTarget;

    /// <summary>The roster list. Scrolling while pointing here moves the list.</summary>
    public sealed record Roster : OverlayTarget;

    /// <summary>A tab across the top of the panel. Tapping it shows that screen.</summary>
    public sealed record GoTo(Views.OverlayPage Page) : OverlayTarget;

    /// <summary>The person screen's Refresh: read that one person's summary again.</summary>
    /// <remarks>
    /// A read, and the only one a tap can ask for. There is deliberately no target that acts on a
    /// person: the companion observes and reports and never acts, and its device token is
    /// ingest-scoped and could not carry a moderation action even if something here tried
    /// (M3 §10; two overlay modes design §3.1).
    /// </remarks>
    public sealed record RefreshPerson : OverlayTarget;

    /// <summary>The events list. Scrolling while pointing here moves the list.</summary>
    public sealed record Events : OverlayTarget;

    /// <summary>Save a clip: keep the last few minutes as a file on this PC.</summary>
    /// <remarks>
    /// The one target that does something rather than showing something, and it is still not an
    /// action on a person: it writes a file on the moderator's own machine and sends nothing
    /// anywhere. The loop hands it straight back to the companion, which owns the recorder; the
    /// overlay has no recorder, no folder and no way to reach either (clips design spec §11).
    /// </remarks>
    public sealed record SaveClip : OverlayTarget;

    /// <summary>The bar under a headset panel, between its buttons. A tap here does nothing.</summary>
    /// <remarks>
    /// A target so that the bar can be found under a ray at all: a panel letting rays through
    /// still answers on its bar, and this is how the host tells the bar from the rest.
    /// </remarks>
    public sealed record Bar : OverlayTarget;

    /// <summary>The notification panel's box, while the panel can be moved. A tap here does nothing.</summary>
    /// <remarks>
    /// A target so that the whole box counts as the panel: its cards leave gaps, and with no
    /// pop-ups up it holds none, yet a ray anywhere on it has to find the panel to pick it up.
    /// </remarks>
    public sealed record PopUpBox : OverlayTarget;

    /// <summary>The bar's lock: the panel cannot be picked up, moved or resized while it is on.</summary>
    /// <remarks>
    /// Handled by the panel's own host, never by the drive loop: it changes where the panel may
    /// go, not what it shows, and it is saved with the panel's placement.
    /// </remarks>
    public sealed record Lock : OverlayTarget;

    /// <summary>The bar's hand: the panel lets rays through to VRChat while it is on.</summary>
    /// <remarks>Handled by the panel's own host, like <see cref="Lock"/>.</remarks>
    public sealed record ClickThrough : OverlayTarget;

    /// <summary>
    /// One of a list's filters in the row above it. Tapping it shows its choices under the row, or
    /// hides them when they are showing.
    /// </summary>
    /// <param name="List">The list it filters: <see cref="Views.OverlayPage.Instance"/> or <see cref="Views.OverlayPage.Events"/>.</param>
    public sealed record Filter(Views.OverlayPage List, Views.FilterPart Part) : OverlayTarget;

    /// <summary>
    /// One choice among an open filter's. A filter that takes one choice takes it and closes; a
    /// filter that takes several ticks or unticks it and stays open.
    /// </summary>
    /// <param name="Choice">
    /// Which choice: the enum's value for Who, the time and the order; the rank's value, or -1 for
    /// "Not known"; the kind's place in <see cref="Views.KindPick.Offered"/>. For Name it is 0, and
    /// clears the name.
    /// </param>
    public sealed record Pick(Views.OverlayPage List, Views.FilterPart Part, int Choice) : OverlayTarget;

    /// <summary>Clear: every filter on a list back to showing everybody.</summary>
    public sealed record ClearFilters(Views.OverlayPage List) : OverlayTarget;

    /// <summary>
    /// The box holding the name searched for. In a headset, tapping it brings up the runtime's
    /// keyboard with <paramref name="Text"/> already in it; on the desktop window, typing goes
    /// there while it is showing.
    /// </summary>
    public sealed record TypeName(Views.OverlayPage List, string Text) : OverlayTarget;

    /// <summary>
    /// The "+" at a roster row's end: start writing a heads-up from that row (heads-ups,
    /// 2026-10-03).
    /// </summary>
    /// <remarks>
    /// A heads-up is words for the other staff in the instance to read. It is still not an action
    /// on a person: nothing on VRChat or Discord changes because one was placed.
    /// </remarks>
    public sealed record AddHeadsUp(string SubjectId) : OverlayTarget;

    /// <summary>One of the heads-up kinds, picked while one is being written.</summary>
    public sealed record HeadsUpKindPick(Modbot.Shared.HeadsUps.HeadsUpKind Kind) : OverlayTarget;

    /// <summary>For a Message being written: about the row's person, or about the instance.</summary>
    public sealed record HeadsUpAboutPick(bool Instance) : OverlayTarget;

    /// <summary>One of Ask for help's places, picked while one is being written.</summary>
    public sealed record HeadsUpPlacePick(string Place) : OverlayTarget;

    /// <summary>
    /// The box holding a heads-up's words. In a headset, tapping it brings up the runtime's
    /// keyboard with <paramref name="Text"/> already in it; on the desktop window, typing goes
    /// there while a heads-up is being written.
    /// </summary>
    public sealed record TypeHeadsUp(string Text) : OverlayTarget;

    /// <summary>Place: send the heads-up being written.</summary>
    public sealed record PlaceHeadsUp : OverlayTarget;

    /// <summary>Cancel: drop the heads-up being written. Nothing was sent.</summary>
    public sealed record CancelHeadsUp : OverlayTarget;

    /// <summary>Clear a standing heads-up, for everybody in the instance.</summary>
    public sealed record ClearHeadsUp(string Id) : OverlayTarget;

    /// <summary>The show and hide button's face. Tapping it shows or hides the headset panel.</summary>
    /// <remarks>
    /// Handled by the button's own host, never by the drive loop: it changes whether the panel is
    /// there, not what it shows, and it is not an action on a person.
    /// </remarks>
    public sealed record PanelButton : OverlayTarget;
}

/// <summary>A target and where it was drawn, in panel pixels.</summary>
public readonly record struct PlacedTarget(OverlayTarget Target, Rect Bounds);

/// <summary>
/// Finds targets in a laid-out tree by walking it, rather than through Avalonia's own hit
/// testing, which wants a window the offscreen tree does not have.
/// </summary>
/// <remarks>
/// Each control's <c>Bounds</c> is relative to its parent, so the walk keeps a running offset.
/// The panel uses no render transforms except one: the headset's VRChat look is drawn bigger than it
/// is laid out (<see cref="Views.Zoom"/>), and the walk multiplies by its scale on the way in. The
/// deepest target under the point wins, which is how a row inside the roster beats the roster itself.
/// </remarks>
public static class OverlayTargets
{
    /// <summary>Every target in the tree, with its bounds in the root's coordinates, outermost first.</summary>
    public static IReadOnlyList<PlacedTarget> Find(Visual root)
    {
        ArgumentNullException.ThrowIfNull(root);

        var found = new List<PlacedTarget>();
        Walk(root, new Point(0, 0), 1.0, found);
        return found;
    }

    /// <summary>The deepest target under a point in the root's coordinates, or null.</summary>
    public static OverlayTarget? At(Visual root, Point point)
    {
        OverlayTarget? deepest = null;
        foreach (var placed in Find(root))
        {
            if (placed.Bounds.Contains(point))
                deepest = placed.Target;
        }

        return deepest;
    }

    /// <summary>
    /// Whether anything a moderator can see is drawn under a point: a card, a row, a tab, a chip,
    /// or text. The panel is one square picture and most of it is usually clear ground; a ray on
    /// the clear part is looking past the panel, not at it.
    /// </summary>
    /// <remarks>
    /// Read off the laid-out tree rather than the pixels, because the pixels hold the cursor ring,
    /// and a ray that counted the ring as the panel would drag the cursor with it into the empty
    /// space and never let go — the bar stuck open, the cursor stuck on nothing. A surface counts
    /// when it has a background that is not fully see-through and nothing above it has faded it to
    /// nothing, which is also what leaves out a bar laid out at no opacity.
    /// </remarks>
    public static bool Drawn(Visual root, Point point)
    {
        ArgumentNullException.ThrowIfNull(root);
        return DrawnAt(root, new Point(0, 0), 1.0, 1.0, point);
    }

    private static bool DrawnAt(Visual visual, Point offset, double scale, double opacity, Point point)
    {
        if (!visual.IsVisible)
            return false;

        opacity *= visual.Opacity;
        if (opacity <= 0)
            return false;

        var bounds = visual.Bounds;
        var origin = new Point(offset.X + (bounds.X * scale), offset.Y + (bounds.Y * scale));
        var box = new Rect(origin, new Size(bounds.Width * scale, bounds.Height * scale));

        var seen = visual switch
        {
            Border { Background: { } ground } => ground.Opacity > 0 && ground is not Avalonia.Media.ISolidColorBrush { Color.A: 0 },
            Panel { Background: { } ground } => ground.Opacity > 0 && ground is not Avalonia.Media.ISolidColorBrush { Color.A: 0 },
            TextBlock { Text.Length: > 0 } => true,
            _ => false,
        };

        if (seen && box.Contains(point))
            return true;

        var inside = visual is Views.Zoom zoom ? scale * zoom.Scale : scale;
        foreach (var child in visual.GetVisualChildren())
        {
            if (DrawnAt(child, origin, inside, opacity, point))
                return true;
        }

        return false;
    }

    private static void Walk(Visual visual, Point offset, double scale, List<PlacedTarget> found)
    {
        var bounds = visual.Bounds;
        var origin = new Point(offset.X + (bounds.X * scale), offset.Y + (bounds.Y * scale));

        if (visual is Control { Tag: OverlayTarget target })
            found.Add(new PlacedTarget(target, new Rect(origin, new Size(bounds.Width * scale, bounds.Height * scale))));

        var inside = visual is Views.Zoom zoom ? scale * zoom.Scale : scale;
        foreach (var child in visual.GetVisualChildren())
            Walk(child, origin, inside, found);
    }
}
