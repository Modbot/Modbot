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
    /// modifier on its own. A bare letter is allowed here, unlike the overlay's shortcut, because it
    /// is pressed rather than claimed: nothing is taken from any other program.
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
