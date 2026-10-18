using Modbot.Companion.Presentation;

namespace Modbot.Companion.Talk;

/// <summary>
/// A key a person recorded for Discord, as the keys Windows presses.
/// </summary>
/// <remarks>
/// <para>Spelled the way the window's own keys are (<see cref="KeyTokens"/>): modifiers first, lower
/// case, joined by <c>+</c>. <c>mod</c> is Ctrl. Beyond what the desktop overlay's shortcut takes
/// this allows <strong>no modifier at all</strong> (<c>f13</c> is the usual choice for a key nobody
/// else uses) and the number pad (<c>num0</c> to <c>num9</c>).</para>
/// <para>Nothing here reads the keyboard. It turns words into two numbers, which the client's
/// <c>WindowsKeyPresser</c> hands to Windows to press. What leaves the machine: nothing.</para>
/// </remarks>
public static class TalkKeys
{
    /// <summary>Windows' virtual-key code for the number pad's 0.</summary>
    private const int NumberPadZero = 0x60;

    /// <summary>The recorded key as modifier flags and a virtual-key code, or null when it cannot be pressed.</summary>
    /// <remarks>
    /// Refused: nothing at all, a chord, more than one ordinary key, a key nobody has, and a
    /// modifier on its own. A bare letter is allowed here, unlike the overlay's shortcut, but only for
    /// a key Modbot <em>presses</em> for Discord, where nothing is taken from any other program. A key
    /// Modbot <em>claims</em> from Windows as a hotkey takes the key away from every program, so
    /// <see cref="ReadHotkey"/> is stricter.
    /// </remarks>
    public static ShortcutKeys? Read(string? key)
    {
        if (string.IsNullOrWhiteSpace(key) || key.Contains(' ', StringComparison.Ordinal))
            return null;

        var modifiers = 0;
        int? code = null;

        foreach (var part in key.Trim().ToLowerInvariant().Split('+', StringSplitOptions.RemoveEmptyEntries))
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

            if (code is not null)
                return null;

            code = CodeFor(part);
            if (code is null)
                return null;
        }

        return code is { } found ? new ShortcutKeys(modifiers, found) : null;
    }

    public static bool CanBePressed(string? key) => Read(key) is not null;

    /// <summary>The one line shown when a typing key is offered as a hotkey.</summary>
    public const string HotkeyNeedsModifier = "A hotkey needs Ctrl or Alt, or a function key.";

    /// <summary>Windows' virtual-key codes for F1 and F24.</summary>
    private const int FirstFunctionKey = 0x70;

    private const int LastFunctionKey = 0x87;

    /// <summary>
    /// A recorded key as a hotkey Modbot may claim from Windows, or null. Everything
    /// <see cref="Read"/> refuses is refused here, and so is any key without Ctrl or Alt that is not
    /// a function key: claimed across the whole machine, a bare letter, digit, punctuation mark,
    /// space, Enter, Tab or arrow would stop working in every other program, the game included
    /// (Shift on its own does not help, as a capital letter is still typing).
    /// </summary>
    public static ShortcutKeys? ReadHotkey(string? key)
    {
        if (Read(key) is not { } keys)
            return null;

        var heldWithControl = (keys.Modifiers & (ShortcutKeys.Ctrl | ShortcutKeys.Alt)) != 0;
        var functionKey = keys.Key is >= FirstFunctionKey and <= LastFunctionKey;

        return heldWithControl || functionKey ? keys : null;
    }

    /// <summary>Whether <see cref="ReadHotkey"/> accepts the key.</summary>
    public static bool CanBeHotkey(string? key) => ReadHotkey(key) is not null;

    /// <summary>The key as a person reads it: <c>Ctrl Shift M</c>, <c>F13</c>, <c>Num 0</c>.</summary>
    public static string Describe(string key)
    {
        ArgumentNullException.ThrowIfNull(key);

        return string.Join(' ', key.Split('+', StringSplitOptions.RemoveEmptyEntries).Select(part =>
        {
            if (part.Length == 4 && part.StartsWith("num", StringComparison.Ordinal) && char.IsAsciiDigit(part[3]))
                return $"Num {part[3]}";

            if (part.Length is 2 or 3 && part[0] == 'f' && char.IsAsciiDigit(part[1]))
                return part.ToUpperInvariant();

            return KeyTokens.DescribeCombo(part);
        }));
    }

    private static int? CodeFor(string part)
    {
        if (part.Length == 4 && part.StartsWith("num", StringComparison.Ordinal) && char.IsAsciiDigit(part[3]))
            return NumberPadZero + (part[3] - '0');

        return DesktopOverlayKeys.CodeFor(part);
    }
}
