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
/// <para><strong>One rule for every size.</strong> VRChat draws its HUD at a scale that follows
/// the window's height, <c>S = client height / 1009</c>, measured on one real window at two sizes
/// (§3.3.1). So any client area gives sizes, by <see cref="VRChatHudLayout.ScaleFor"/>; only an
/// empty or nonsensical one says no from <see cref="For"/>. This replaces the first version's
/// "two known sizes and no others". Every number a live window might correct is a constant in
/// <see cref="EscapeBubbleMetrics"/> or <see cref="VRChatHudLayout"/>.</para>
/// </remarks>
public static class EscapeBubbleLayout
{
    /// <summary>
    /// The sizes for a client area (VRChat's window without its title bar), by the rule that the
    /// HUD's scale is the client's height over 1009. Null when the client area is empty or makes
    /// no sense.
    /// </summary>
    public static EscapeBubbleMetrics? For(int clientWidth, int clientHeight)
        => VRChatHudLayout.For(clientWidth, clientHeight) is { } layout
            ? new EscapeBubbleMetrics(layout.Scale)
            : null;

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
