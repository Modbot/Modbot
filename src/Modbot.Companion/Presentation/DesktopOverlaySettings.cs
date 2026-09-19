namespace Modbot.Companion.Presentation;

/// <summary>
/// One keyboard combination, in the shape Windows wants to hear it.
/// </summary>
/// <remarks>
/// The numbers are Windows' own: the modifier flags <c>RegisterHotKey</c> takes and a virtual-key
/// code. They are plain integers here so the reading and the checking can be tested without a
/// window and without Windows — the one call that hands them to the operating system lives in the
/// client's <c>DesktopOverlayShortcut</c>.
/// </remarks>
/// <param name="Modifiers">Alt is 1, Ctrl is 2, Shift is 4, added together.</param>
/// <param name="Key">The virtual-key code of the one key held with them.</param>
public readonly record struct ShortcutKeys(int Modifiers, int Key)
{
    /// <summary>Windows' flag for Alt.</summary>
    public const int Alt = 0x0001;

    /// <summary>Windows' flag for Ctrl.</summary>
    public const int Ctrl = 0x0002;

    /// <summary>Windows' flag for Shift.</summary>
    public const int Shift = 0x0004;
}

/// <summary>
/// Reads a shortcut written the way the window's own keys are written.
/// </summary>
/// <remarks>
/// <para>The spelling is <see cref="KeyTokens"/>': modifiers first, lower case, joined by
/// <c>+</c> — <c>mod+alt+m</c>, <c>mod+shift+f8</c>. <c>mod</c> is Ctrl. One combination only;
/// a chord such as <c>g e</c> is a thing the window can wait for and the operating system
/// cannot.</para>
/// <para><strong>A modifier is required.</strong> A bare letter claimed across the whole machine
/// would swallow that letter in every other program, the game included, which is not a thing any
/// moderator meant to ask for.</para>
/// <para>Nothing here reads the keyboard. It turns words into two numbers.</para>
/// </remarks>
public static class DesktopOverlayKeys
{
    /// <summary>The keys that are not a letter, a digit or a function key, and their codes.</summary>
    private static readonly Dictionary<string, int> Named = new(StringComparer.Ordinal)
    {
        ["space"] = 0x20,
        ["tab"] = 0x09,
        ["enter"] = 0x0D,
        ["backspace"] = 0x08,
        ["insert"] = 0x2D,
        ["delete"] = 0x2E,
        ["home"] = 0x24,
        ["end"] = 0x23,
        ["pageup"] = 0x21,
        ["pagedown"] = 0x22,
        ["arrowleft"] = 0x25,
        ["arrowup"] = 0x26,
        ["arrowright"] = 0x27,
        ["arrowdown"] = 0x28,
    };

    /// <summary>
    /// Reads one combination, or null when it is not one the operating system could be asked for.
    /// </summary>
    /// <remarks>
    /// Refused: nothing at all, a chord, more than one ordinary key, a key nobody has, no
    /// modifier, and <c>escape</c> — which is how VRChat opens its own menu, so claiming it across
    /// the whole machine would take the game's menu key away from the game.
    /// </remarks>
    public static ShortcutKeys? Read(string? shortcut)
    {
        if (string.IsNullOrWhiteSpace(shortcut) || shortcut.Contains(' ', StringComparison.Ordinal))
            return null;

        var modifiers = 0;
        int? key = null;

        foreach (var part in shortcut.Trim().ToLowerInvariant().Split('+', StringSplitOptions.RemoveEmptyEntries))
        {
            switch (part)
            {
                case "mod":
                    modifiers |= ShortcutKeys.Ctrl;
                    continue;
                case "alt":
                    modifiers |= ShortcutKeys.Alt;
                    continue;
                case "shift":
                    modifiers |= ShortcutKeys.Shift;
                    continue;
            }

            // Two ordinary keys is not a combination Windows can be asked for.
            if (key is not null)
                return null;

            key = CodeFor(part);
            if (key is null)
                return null;
        }

        return key is { } code && modifiers != 0 ? new ShortcutKeys(modifiers, code) : null;
    }

    /// <summary>Whether a shortcut is one the client could ask the operating system for.</summary>
    public static bool CanBeUsed(string? shortcut) => Read(shortcut) is not null;

    private static int? CodeFor(string key)
    {
        if (key.Length == 1)
        {
            var c = key[0];
            if (c is >= 'a' and <= 'z')
                return 'A' + (c - 'a');

            if (c is >= '0' and <= '9')
                return '0' + (c - '0');

            return null;
        }

        if (key.Length is 2 or 3 && key[0] == 'f' && int.TryParse(key.AsSpan(1), out var number) && number is >= 1 and <= 24)
            return 0x70 + number - 1;

        return Named.TryGetValue(key, out var code) ? code : null;
    }
}

/// <summary>
/// The desktop overlay: whether it runs, the keyboard shortcut that brings it up, and how solid
/// its background is.
/// </summary>
/// <remarks>
/// <para>Saved as the <c>desktopOverlay</c> object in <c>settings.json</c>
/// (<see cref="CompanionSettings.SaveDesktopOverlay"/>), which is rewritten whole and leaves every
/// other field in the settings alone.</para>
/// <para><strong>Off until somebody turns it on.</strong> The shortcut is a claim on every other
/// program's keyboard, and the design spec (2026-09-18, §6) says that is asked for rather than
/// assumed.</para>
/// </remarks>
/// <param name="On">Whether the overlay window exists and its shortcut is registered.</param>
/// <param name="Shortcut">The combination, in <see cref="KeyTokens"/> spelling.</param>
/// <param name="Opacity">How solid the panel's background is, from 20 to 100.</param>
public sealed record DesktopOverlaySettings(bool On, string Shortcut, int Opacity)
{
    /// <summary>
    /// Ctrl+Alt+M. VRChat's desktop keys are bare letters and function keys, Windows keeps the
    /// Windows key and Ctrl+Alt+Del, and the existing token spelling can write this one exactly —
    /// which <c>Ctrl+Shift+M</c> cannot, because Shift is dropped for a printable key.
    /// </summary>
    public const string DefaultShortcut = "mod+alt+m";

    public const int DefaultOpacity = 90;

    public const int MinimumOpacity = 20;

    public const int MaximumOpacity = 100;

    /// <summary>Off, on Ctrl+Alt+M, nearly solid.</summary>
    public static DesktopOverlaySettings Default { get; } = new(false, DefaultShortcut, DefaultOpacity);

    /// <summary>Between 20 and 100: a panel at 0 would be a window nobody could see or find.</summary>
    public static int ClampOpacity(int opacity) => Math.Clamp(opacity, MinimumOpacity, MaximumOpacity);

    /// <summary>
    /// The shortcut as the client will actually use it: what was saved when that can be asked for,
    /// and the default when it cannot. A settings entry nobody could register would otherwise
    /// leave the moderator with an overlay and no way to reach it.
    /// </summary>
    public string ShortcutOrDefault => DesktopOverlayKeys.CanBeUsed(Shortcut) ? Shortcut : DefaultShortcut;

    /// <summary>The background's alpha, from the opacity.</summary>
    public byte Alpha => (byte)(ClampOpacity(Opacity) * 255 / 100);

    /// <summary>
    /// Whether the window should be up after the shortcut is pressed.
    /// </summary>
    /// <remarks>
    /// Two rules in one line. The shortcut turns it over, so the same keys that bring it up take
    /// it away; and switched off it is never up, whatever was pressed.
    /// <para><strong>Escape is not one of the ways it closes.</strong> Escape is how VRChat opens
    /// its own menu, so a moderator pressing it means the menu, and a window that took the press
    /// instead put itself in a fight with the game it is sitting on top of. The window closes on
    /// the shortcut and on its own Close button.</para>
    /// </remarks>
    /// <param name="showing">Whether the window is up now.</param>
    public bool NextShowing(bool showing) => On && !showing;
}

/// <summary>How the shortcut got on with the operating system.</summary>
public enum ShortcutState
{
    /// <summary>The overlay is switched off, so nothing was asked for.</summary>
    Off,

    /// <summary>The operating system gave the client that combination.</summary>
    Registered,

    /// <summary>Another program already has it.</summary>
    Taken,

    /// <summary>Not a combination that could be asked for at all.</summary>
    NotUnderstood,

    /// <summary>This is not Windows, and the client asks for shortcuts nowhere else.</summary>
    NotOnThisSystem,
}

/// <summary>
/// The desktop overlay as the Settings page shows it: what it is set to, whether the shortcut was
/// granted, and whether the window is up right now.
/// </summary>
/// <remarks>
/// Plain words and no Avalonia, like the rest of this file: the wording a moderator reads when a
/// shortcut is refused is worth testing without a window.
/// </remarks>
public sealed record DesktopOverlayStatus(DesktopOverlaySettings Settings, ShortcutState Shortcut, bool Showing)
{
    /// <summary>Before the client has tried, and when it is switched off.</summary>
    public static DesktopOverlayStatus None { get; } = new(DesktopOverlaySettings.Default, ShortcutState.Off, false);

    /// <summary>
    /// What went wrong, in one line, or null when nothing did. An error says what failed; it does
    /// not explain the feature.
    /// </summary>
    public string? Problem => Shortcut switch
    {
        ShortcutState.Taken => $"{KeyTokens.Describe(Settings.ShortcutOrDefault)} is already taken by another program.",
        ShortcutState.NotUnderstood => "That shortcut cannot be used.",
        ShortcutState.NotOnThisSystem => "Shortcuts only work on Windows.",
        _ => null,
    };
}
