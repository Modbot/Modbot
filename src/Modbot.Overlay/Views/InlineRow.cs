using Avalonia;
using Avalonia.Controls;

namespace Modbot.Overlay.Views;

/// <summary>
/// The line of marks on a roster row: badge, name, rank and chips side by side, left to right, like a
/// horizontal stack, except that it never runs past the room it is given.
/// </summary>
/// <remarks>
/// A horizontal stack measures every child with all the width it wants, so a long flag reason such as
/// "12 kicks or bans · 15 warns" was drawn past the end of the row and under the time on its right.
/// Here the name and the flag chip start at their smallest (<see cref="NameFloor"/>, <see cref="FlagFloor"/>;
/// their words end in "…"). If the line still does not fit, the <see cref="Optional"/> marks are left out,
/// last first, and as a last resort the flag chip goes. Whatever room is then left goes back to the name,
/// then the flag chip, up to the width they want. Nothing is ever drawn past the edge.
/// </remarks>
internal sealed class InlineRow : Panel
{
    private const double FlagFloor = 100;

    private double _lastWidth = double.NaN;
    private double[] _widths = [];

    /// <summary>The gap between neighbours.</summary>
    public double Spacing { get; init; } = 10;

    /// <summary>The name. Must be one of the children.</summary>
    public Control? NameText { get; init; }

    /// <summary>The smallest the name is trimmed to, in pixels.</summary>
    public double NameFloor { get; init; } = 60;

    /// <summary>The flag chip. Must be one of the children.</summary>
    public Control? Flag { get; init; }

    /// <summary>Marks that are left out, last first, when the line has no room for them.</summary>
    public HashSet<Control> Optional { get; } = [];

    public InlineRow()
    {
        ClipToBounds = true;
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        Fit(availableSize.Width);

        var height = 0.0;

        foreach (var child in Children)
            height = Math.Max(height, child.DesiredSize.Height);

        return new Size(Used(), height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        if (Math.Abs(finalSize.Width - _lastWidth) > 0.5 && !(double.IsInfinity(finalSize.Width) && double.IsInfinity(_lastWidth)))
            Fit(finalSize.Width);

        var x = 0.0;
        var shown = 0;

        for (var i = 0; i < Children.Count; i++)
        {
            var child = Children[i];

            if (_widths[i] <= 0)
            {
                child.Arrange(new Rect(0, 0, 0, 0));
                continue;
            }

            if (shown > 0)
                x += Spacing;

            child.Arrange(new Rect(x, 0, _widths[i], finalSize.Height));
            x += _widths[i];
            shown++;
        }

        return finalSize;
    }

    /// <summary>The width the children take as they now stand, gaps included.</summary>
    private double Used()
    {
        var total = 0.0;
        var shown = 0;

        foreach (var width in _widths)
        {
            if (width <= 0)
                continue;

            total += width + (shown > 0 ? Spacing : 0);
            shown++;
        }

        return total;
    }

    /// <summary>Works out every child's width for the room given, and measures each at it.</summary>
    private void Fit(double available)
    {
        _lastWidth = available;

        var count = Children.Count;
        var wanted = new double[count];
        _widths = new double[count];

        for (var i = 0; i < count; i++)
        {
            Children[i].Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            wanted[i] = Children[i].DesiredSize.Width;
            _widths[i] = wanted[i];
        }

        if (double.IsInfinity(available))
            return;

        var name = NameText is null ? -1 : Children.IndexOf(NameText);
        var flag = Flag is null ? -1 : Children.IndexOf(Flag);

        if (name >= 0)
            _widths[name] = Math.Min(wanted[name], NameFloor);

        if (flag >= 0)
            _widths[flag] = Math.Min(wanted[flag], FlagFloor);

        // Too long even so: leave out marks, last first, then the flag chip itself.
        for (var i = count - 1; i >= 0 && Used() > available; i--)
        {
            if (Children[i] is { } mark && Optional.Contains(mark))
                _widths[i] = 0;
        }

        if (Used() > available && flag >= 0)
            _widths[flag] = 0;

        // Whatever room is left goes back to the name, then to the flag chip.
        foreach (var i in new[] { name, flag })
        {
            if (i < 0 || _widths[i] <= 0)
                continue;

            var room = available - Used();

            if (room > 0)
                _widths[i] = Math.Min(wanted[i], _widths[i] + room);
        }

        for (var i = 0; i < count; i++)
        {
            if (_widths[i] <= 0 || _widths[i] >= wanted[i])
                continue;

            Children[i].Measure(new Size(_widths[i], double.PositiveInfinity));
            _widths[i] = Math.Min(_widths[i], Children[i].DesiredSize.Width);
        }
    }
}
