using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;

namespace Modbot.Overlay.Interaction;

/// <summary>
/// The one rule for what a control looks like while the laser or the mouse is on it: the accent
/// treatment the controller hints wear when lit, on every control the pointer could press.
/// </summary>
/// <remarks>
/// The headset's laser sends no pointer events into the panel's tree, so Avalonia's own hover never
/// fires there. The host works out which tagged target is under the pointer and applies this to the
/// tree it is about to draw; the desktop window applies the same rule to its live tree under the
/// mouse. A control is found by its <c>Tag</c>, the way a press finds it, so what lights is what a
/// press would land on. The controls it changes are put back by <see cref="Clear"/>.
/// </remarks>
public sealed class Lit
{
    private readonly List<Action> _putBack = [];

    /// <summary>
    /// Whether a target is something a press can land on and so lights up. The lists, the pop-up
    /// box and the bar's strip only hold other controls, and the bar's controls are there to press
    /// only in edit mode.
    /// </summary>
    public static bool Lights(OverlayTarget? target, bool editMode) => target switch
    {
        null or OverlayTarget.Roster or OverlayTarget.Events or OverlayTarget.PopUpBox or OverlayTarget.Bar => false,
        OverlayTarget.Lock or OverlayTarget.ClickThrough or OverlayTarget.ResizeCorner => editMode,
        _ => true,
    };

    /// <summary>
    /// Puts back whatever was lit before, then lights every control in the tree tagged with
    /// <paramref name="target"/>. Null lights nothing.
    /// </summary>
    public void Show(Visual root, object? target, DesignTokens tokens)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(tokens);

        Clear();

        if (target is not null)
            Walk(root, target, tokens);
    }

    /// <summary>Puts every lit control back as it was.</summary>
    public void Clear()
    {
        foreach (var putBack in _putBack)
            putBack();

        _putBack.Clear();
    }

    private void Walk(Visual visual, object target, DesignTokens tokens)
    {
        if (visual is Control { Tag: { } tag } control && tag.Equals(target))
            Light(control, tokens);

        foreach (var child in visual.GetVisualChildren())
            Walk(child, target, tokens);
    }

    private void Light(Control control, DesignTokens tokens)
    {
        switch (control)
        {
            // Strokes on nothing: there is no surface to fill, so the strokes themselves go bright.
            case Border when control.Tag is OverlayTarget.ResizeCorner:
                foreach (var path in control.GetVisualDescendants().OfType<Avalonia.Controls.Shapes.Path>())
                {
                    var stroke = path.Stroke;
                    path.Stroke = tokens.TextBrush;
                    _putBack.Add(() => path.Stroke = stroke);
                }

                break;
            case Border border:
                var fill = border.Background;
                var edge = border.BorderBrush;
                border.Background = tokens.AccentDimBrush;
                border.BorderBrush = tokens.AccentBrush;
                _putBack.Add(() =>
                {
                    border.Background = fill;
                    border.BorderBrush = edge;
                });

                break;
            case Panel panel:
                var ground = panel.Background;
                panel.Background = tokens.AccentDimBrush;
                _putBack.Add(() => panel.Background = ground);
                break;
        }
    }
}
