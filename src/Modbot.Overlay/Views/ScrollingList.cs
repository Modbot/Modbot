using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.VisualTree;

namespace Modbot.Overlay.Views;

/// <summary>
/// A list shown through a window of the room it is given: its rows are laid out in full, drawn
/// <see cref="Offset"/> pixels up from where they would be, and cut to the window, with a thin bar
/// down the right-hand side saying where in the list the window is.
/// </summary>
/// <remarks>
/// <para>The headset's lists are moved by pixels, not by rows, so a drag or a fling is smooth and the
/// list can start and stop anywhere. The offset is a render transform and not a layout, so moving it
/// is a redraw of the tree that is already built (<see cref="Offset"/> may be set again on a laid-out
/// panel), not a new tree. <see cref="MaxOffset"/> is what layout found the list could go.</para>
/// <para>The room for the bar is always kept, scrollable or not, so rows are the same width whether the
/// list is long enough to scroll or not and a list that grows one row longer does not re-wrap
/// the ones it had. The bar is drawn only when there is something to scroll to. It is display only.</para>
/// <para>A list that is scrolled down also shows an arrow at its top centre that takes it back to the top
/// (<see cref="TopButton"/>). It floats over the rows and is not moved with them; at the top it is hidden.</para>
/// </remarks>
internal sealed class ScrollingList : Control
{
    /// <summary>The room kept down the right-hand side for the bar, in layout pixels.</summary>
    public const double Gutter = 12;

    private const double BarWidth = 5;
    private const double BarEdge = 2;
    private const double LeastThumb = 28;

    /// <summary>How far down the list has to be before the arrow back to the top shows, in layout pixels.</summary>
    private const double TopButtonAfter = 24;

    /// <summary>The gap between the top of the list and the arrow.</summary>
    private const double TopButtonGap = 4;

    private readonly IBrush _bar;
    private readonly Control _rows;
    private double _wanted;
    private double _content;
    private double _room;
    private bool _topShown;

    /// <param name="rows">The list, all of it.</param>
    /// <param name="bar">The colour the bar is drawn in.</param>
    /// <param name="offset">How far the list is scrolled to begin with, in layout pixels.</param>
    /// <param name="topButton">The arrow that takes the list back to the top, or null for a list without one.</param>
    public ScrollingList(Control rows, IBrush bar, double offset, Control? topButton = null)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(bar);

        _bar = bar;
        _rows = rows;
        _wanted = offset;
        ClipToBounds = true;
        Adopt(rows);

        TopButton = topButton;
        ShowTopButton();
        Place();
    }

    /// <summary>The arrow back to the top, in the tree only while the list is scrolled down; null when the list has none.</summary>
    public Control? TopButton { get; }

    /// <summary>How far the rows are scrolled up, in layout pixels: as asked for, held inside the list's ends.</summary>
    public double Offset
    {
        get => Math.Clamp(_wanted, 0, MaxOffset);
        set
        {
            _wanted = value;
            Place();
            ShowTopButton();
            InvalidateVisual();
        }
    }

    /// <summary>How far the list can be scrolled, in layout pixels: what its rows are taller than its room by.</summary>
    public double MaxOffset => Math.Max(0, _content - _room);

    /// <summary>The first list in a laid-out tree, or null when what is drawn has none.</summary>
    public static ScrollingList? In(Visual root)
    {
        ArgumentNullException.ThrowIfNull(root);

        if (root is ScrollingList here)
            return here;

        foreach (var child in root.GetVisualChildren())
        {
            if (In(child) is { } found)
                return found;
        }

        return null;
    }

    private void Adopt(Control child)
    {
        LogicalChildren.Add(child);
        VisualChildren.Add(child);
    }

    private void Place() => _rows.RenderTransform = new TranslateTransform(0, -Offset);

    /// <summary>
    /// Puts the arrow in the tree while the list is scrolled down and takes it out at the top, so at the top it is
    /// not drawn and not a target. The list may be scrolled on a tree that is already laid out and is not laid out
    /// again, so an arrow that comes in is measured and placed here, not left to the next layout.
    /// </summary>
    private void ShowTopButton()
    {
        if (TopButton is null || _topShown == Offset > TopButtonAfter)
            return;

        _topShown = !_topShown;

        if (_topShown)
        {
            // After the rows, so it is drawn over them.
            Adopt(TopButton);
            PlaceTopButton(Bounds.Width);
        }
        else
        {
            VisualChildren.Remove(TopButton);
            LogicalChildren.Remove(TopButton);
        }
    }

    private void PlaceTopButton(double width)
    {
        if (!_topShown || TopButton is null)
            return;

        TopButton.Measure(Size.Infinity);
        var size = TopButton.DesiredSize;
        var across = Math.Max(0, width - Gutter);
        TopButton.Arrange(new Rect(Math.Max(0, (across - size.Width) / 2), TopButtonGap, size.Width, size.Height));
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        _rows.Measure(new Size(Math.Max(0, availableSize.Width - Gutter), double.PositiveInfinity));
        _content = _rows.DesiredSize.Height;

        var room = double.IsInfinity(availableSize.Height) ? _content : Math.Min(_content, availableSize.Height);
        _room = room;
        Place();
        ShowTopButton();

        var width = double.IsInfinity(availableSize.Width) ? _rows.DesiredSize.Width + Gutter : availableSize.Width;
        return new Size(width, room);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        _room = finalSize.Height;
        var across = Math.Max(0, finalSize.Width - Gutter);
        _rows.Arrange(new Rect(0, 0, across, _content));

        PlaceTopButton(finalSize.Width);
        Place();
        return finalSize;
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);

        if (MaxOffset <= 0 || _room <= 0)
            return;

        var x = Bounds.Width - BarWidth - BarEdge;
        var radius = BarWidth / 2;

        using (context.PushOpacity(0.25))
            context.DrawRectangle(_bar, null, new RoundedRect(new Rect(x, 0, BarWidth, _room), radius));

        var thumb = Math.Clamp(_room * _room / _content, Math.Min(LeastThumb, _room), _room);
        var top = Offset / MaxOffset * (_room - thumb);

        using (context.PushOpacity(0.9))
            context.DrawRectangle(_bar, null, new RoundedRect(new Rect(x, top, BarWidth, thumb), radius));
    }
}

/// <summary>
/// A column that gives its last child what the others leave of the height, or its own height if that
/// is less, where a stack would give it as much as it asked for and let the end run off the panel.
/// </summary>
/// <remarks>
/// With no limit on the height it is a stack. That is what makes the last child, a list, know how much room
/// it has to scroll in.
/// </remarks>
internal sealed class FillStack : Panel
{
    /// <summary>The gap between one child and the next.</summary>
    public double Spacing { get; set; }

    protected override Size MeasureOverride(Size availableSize)
    {
        var shown = Children.Where(child => child.IsVisible).ToList();
        var used = 0.0;
        var width = 0.0;

        for (var i = 0; i < shown.Count; i++)
        {
            var last = i == shown.Count - 1;
            var gap = i > 0 ? Spacing : 0;

            // The last gets what is left; the others as much as they want, as a stack gives.
            var room = !last || double.IsInfinity(availableSize.Height)
                ? double.PositiveInfinity
                : Math.Max(0, availableSize.Height - used - gap);

            shown[i].Measure(new Size(availableSize.Width, room));
            used += gap + shown[i].DesiredSize.Height;
            width = Math.Max(width, shown[i].DesiredSize.Width);
        }

        return new Size(width, used);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var top = 0.0;
        var first = true;

        foreach (var child in Children.Where(child => child.IsVisible))
        {
            if (!first)
                top += Spacing;

            first = false;
            child.Arrange(new Rect(0, top, finalSize.Width, child.DesiredSize.Height));
            top += child.DesiredSize.Height;
        }

        return finalSize;
    }
}
