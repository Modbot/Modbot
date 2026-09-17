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
}

/// <summary>A target and where it was drawn, in panel pixels.</summary>
public readonly record struct PlacedTarget(OverlayTarget Target, Rect Bounds);

/// <summary>
/// Finds targets in a laid-out tree by walking it, rather than through Avalonia's own hit
/// testing, which wants a window the offscreen tree does not have.
/// </summary>
/// <remarks>
/// Each control's <c>Bounds</c> is relative to its parent, so the walk keeps a running offset.
/// The panel uses no render transforms, so that is the whole of the geometry. The deepest target
/// under the point wins, which is how a row inside the roster beats the roster itself.
/// </remarks>
public static class OverlayTargets
{
    /// <summary>Every target in the tree, with its bounds in the root's coordinates, outermost first.</summary>
    public static IReadOnlyList<PlacedTarget> Find(Visual root)
    {
        ArgumentNullException.ThrowIfNull(root);

        var found = new List<PlacedTarget>();
        Walk(root, new Point(0, 0), found);
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

    private static void Walk(Visual visual, Point offset, List<PlacedTarget> found)
    {
        var bounds = visual.Bounds;
        var origin = new Point(offset.X + bounds.X, offset.Y + bounds.Y);

        if (visual is Control { Tag: OverlayTarget target })
            found.Add(new PlacedTarget(target, new Rect(origin, bounds.Size)));

        foreach (var child in visual.GetVisualChildren())
            Walk(child, origin, found);
    }
}
