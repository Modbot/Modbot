namespace Modbot.Companion.Presentation;

/// <summary>
/// Where Modbot's bubble goes in VRChat's HUD row, and what it says
/// (<c>2026-10-05-desktop-overlay-escape-menu-design.md</c> §3).
/// </summary>
/// <remarks>
/// <para>Plain numbers, so the placing can be tested without a screen. The window that draws the
/// bubble only turns these into pixels.</para>
/// <para><strong>A fifth slot after Y.</strong> VRChat's row is Esc, R, Tab and Y. While Esc is
/// open it lists hotkeys directly under that row, so a bubble under Esc would hide them; Modbot's
/// bubble takes the next slot along the row instead, with the same icon, label and height as the
/// others and nothing of VRChat's covered.</para>
/// <para><strong>Two sizes known, and no others.</strong> The numbers were read off pictures of
/// VRChat's own interface (§3.3.1): the row's geometry at the smaller HUD, and the scale of the
/// larger one. A client area that is neither says no from <see cref="For"/> and the bubble stays
/// away rather than sitting in the wrong place. Every number a live window might correct is a
/// constant here.</para>
/// </remarks>
public static class EscapeBubbleLayout
{
    /// <summary>The client areas (the window without its title bar) the numbers are known for, and how big the HUD is in each.</summary>
    private static readonly (int Width, int Height, double Scale)[] Known =
    [
        (1918, 1008, 1.0),
        (1918, 1030, 1.074),
    ];

    /// <summary>
    /// The sizes for a client area, or null when it is not one the numbers are known for.
    /// </summary>
    public static EscapeBubbleMetrics? For(int clientWidth, int clientHeight)
    {
        foreach (var (width, height, scale) in Known)
        {
            if (width == clientWidth && height == clientHeight)
                return new EscapeBubbleMetrics(scale);
        }

        return null;
    }

    /// <summary>
    /// The shortcut as VRChat writes its own labels: <c>F1</c>, or <c>Ctrl+Alt+M</c>. Null when the
    /// shortcut is empty.
    /// </summary>
    public static string? Label(string? shortcut)
    {
        if (string.IsNullOrWhiteSpace(shortcut))
            return null;

        return string.Join('+', shortcut.Trim().Split('+', StringSplitOptions.RemoveEmptyEntries).Select(Part));
    }

    private static string Part(string part) => part switch
    {
        "mod" => "Ctrl",
        "alt" => "Alt",
        "shift" => "Shift",
        "escape" => "Esc",
        "enter" => "Enter",
        "space" => "Space",
        _ => part.ToUpperInvariant() is var upper && (part.Length == 1 || IsFunctionKey(upper)) ? upper : part,
    };

    private static bool IsFunctionKey(string key)
        => key.Length is 2 or 3 && key[0] == 'F' && int.TryParse(key.AsSpan(1), out var n) && n is >= 1 and <= 24;
}
