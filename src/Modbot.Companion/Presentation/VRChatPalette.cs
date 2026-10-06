using System.Globalization;
using System.Text.Json;

namespace Modbot.Companion.Presentation;

/// <summary>
/// A colour as VRChat stores it in a palette: three bytes, written <c>#RRGGBB</c>, no see-through part.
/// </summary>
public readonly record struct PaletteColour(byte R, byte G, byte B)
{
    /// <summary>Reads <c>#RRGGBB</c>. Anything else, including a short or long form, is not a colour.</summary>
    public static bool TryParse(string? text, out PaletteColour colour)
    {
        colour = default;

        if (text is not { Length: 7 } || text[0] != '#')
            return false;

        const NumberStyles Hex = NumberStyles.AllowHexSpecifier;

        if (!byte.TryParse(text.AsSpan(1, 2), Hex, CultureInfo.InvariantCulture, out var r)
            || !byte.TryParse(text.AsSpan(3, 2), Hex, CultureInfo.InvariantCulture, out var g)
            || !byte.TryParse(text.AsSpan(5, 2), Hex, CultureInfo.InvariantCulture, out var b))
        {
            return false;
        }

        colour = new PaletteColour(r, g, b);
        return true;
    }

    /// <summary>Every channel times <paramref name="share"/>, rounded and kept between 0 and 255.</summary>
    public PaletteColour Times(double share) => new(Scale(R, share), Scale(G, share), Scale(B, share));

    /// <summary>Part of the way from this colour to <paramref name="other"/>: 0 is this one, 1 is the other.</summary>
    public PaletteColour Toward(PaletteColour other, double part) => new(
        Blend(R, other.R, part),
        Blend(G, other.G, part),
        Blend(B, other.B, part));

    /// <summary>The colour written the way a style sheet writes it, in lower case.</summary>
    public string Hex => $"#{R:x2}{G:x2}{B:x2}";

    /// <summary>How bright the eye finds it, from 0 (black) to 1 (white), by the web's own formula.</summary>
    public double Brightness
        => (0.2126 * Linear(R)) + (0.7152 * Linear(G)) + (0.0722 * Linear(B));

    /// <summary>How well two colours read against each other: 1 for the same colour, 21 for black on white.</summary>
    public static double Contrast(PaletteColour a, PaletteColour b)
    {
        var lighter = Math.Max(a.Brightness, b.Brightness);
        var darker = Math.Min(a.Brightness, b.Brightness);
        return (lighter + 0.05) / (darker + 0.05);
    }

    private static byte Scale(byte channel, double share)
        => (byte)Math.Clamp(Math.Round(channel * share, MidpointRounding.AwayFromZero), 0, 255);

    private static byte Blend(byte from, byte to, double part)
        => (byte)Math.Clamp(Math.Round(from + ((to - from) * part), MidpointRounding.AwayFromZero), 0, 255);

    private static double Linear(byte channel)
    {
        var share = channel / 255.0;
        return share <= 0.03928 ? share / 12.92 : Math.Pow((share + 0.055) / 1.055, 2.4);
    }
}

/// <summary>
/// The six colours of one of VRChat's own colour palettes, as the person using this PC picked it
/// in VRChat's settings.
/// </summary>
/// <remarks>
/// <para><strong>What this reads.</strong> Nothing here reads anything: it is the plain half, text
/// in and a value out, so it can be tested without a registry. What gets handed to it is one value
/// VRChat keeps for the person's own palette, and the text of that value is VRChat's own JSON, for
/// example <c>{"name":"Thy Kingdom","id":"pal_00006","highlights":"#C53B48","icons":"#F66229",
/// "buttons":"#934226","backgrounds":"#67171E","text":"#FFE07B","subtext":"#D86049"}</c> followed
/// by a zero byte, because Unity stores its settings as text with a terminator. Only the six colours
/// are taken from it; the name and the id are not kept.</para>
/// <para><strong>What leaves the machine: nothing.</strong> The palette colours the desktop overlay
/// and are never stored by this client, written anywhere, or sent to a server.</para>
/// </remarks>
public sealed record VRChatPalette(
    PaletteColour Highlights,
    PaletteColour Icons,
    PaletteColour Buttons,
    PaletteColour Backgrounds,
    PaletteColour Text,
    PaletteColour Subtext)
{
    /// <summary>More than this is not a palette, whatever it says; the real thing is about 200 bytes.</summary>
    public const int LongestValue = 4096;

    /// <summary>
    /// Reads a palette from the value's bytes. Null for anything that is not a whole one: not text,
    /// not JSON, not an object, or any of the six colours missing or not written <c>#RRGGBB</c>.
    /// </summary>
    /// <remarks>The zero byte (or several) at the end is part of how VRChat stores it, not a fault.</remarks>
    public static VRChatPalette? Parse(ReadOnlySpan<byte> data)
    {
        var text = data.TrimEnd((byte)0);

        if (text.Length == 0 || text.Length > LongestValue)
            return null;

        try
        {
            using var document = JsonDocument.Parse(text.ToArray());

            if (document.RootElement.ValueKind != JsonValueKind.Object)
                return null;

            if (Colour(document.RootElement, "highlights") is not { } highlights
                || Colour(document.RootElement, "icons") is not { } icons
                || Colour(document.RootElement, "buttons") is not { } buttons
                || Colour(document.RootElement, "backgrounds") is not { } backgrounds
                || Colour(document.RootElement, "text") is not { } words
                || Colour(document.RootElement, "subtext") is not { } subtext)
            {
                return null;
            }

            return new VRChatPalette(highlights, icons, buttons, backgrounds, words, subtext);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static PaletteColour? Colour(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String)
            return null;

        return PaletteColour.TryParse(value.GetString(), out var colour) ? colour : null;
    }
}

/// <summary>
/// Which of VRChat's stored values is the palette selected for one person, found by its name.
/// </summary>
/// <remarks>
/// <para>VRChat's settings are Unity's: each value's name is the setting, then <c>_h</c>, then a
/// number Unity adds to it. The selected palette is
/// <c>COLOR_PALETTES_CURRENT_&lt;user id&gt;_h&lt;number&gt;</c>. The same settings hold values for
/// every account used on the PC, so the match is made for the one user id the client already
/// learned from VRChat's log, and every other name is left alone, unread.</para>
/// <para>The list of all palettes (<c>COLOR_PALETTES_&lt;user id&gt;_h…</c>) is a different value and is
/// never matched here.</para>
/// </remarks>
public static class VRChatPaletteValue
{
    /// <summary>How the name of the selected-palette value starts.</summary>
    public const string CurrentPrefix = "COLOR_PALETTES_CURRENT_";

    /// <summary>A user id the match may be made for: the letters, digits, dash and underscore VRChat's ids use.</summary>
    public static bool IsUsableId(string? userId)
        => userId is { Length: > 0 and <= 80 } && userId.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-');

    /// <summary>Whether a stored value's name is the selected palette of this one person.</summary>
    public static bool IsTheCurrentOneFor(string valueName, string? userId)
    {
        if (!IsUsableId(userId))
            return false;

        var start = CurrentPrefix + userId + "_h";

        if (!valueName.StartsWith(start, StringComparison.Ordinal))
            return false;

        // The rest is Unity's number, and nothing but digits: a longer id that merely begins with
        // this one carries more letters there, and so does the list of all palettes.
        var number = valueName.AsSpan(start.Length);
        return number.Length is > 0 and <= 12 && number.IndexOfAnyExceptInRange('0', '9') < 0;
    }

    /// <summary>
    /// The one name that is this person's selected palette, or null when there is none. Taken in
    /// the names' own order when somehow there are two, so the answer does not change between looks.
    /// </summary>
    public static string? Pick(IEnumerable<string> names, string? userId)
    {
        ArgumentNullException.ThrowIfNull(names);

        return names
            .Where(name => IsTheCurrentOneFor(name, userId))
            .Order(StringComparer.Ordinal)
            .FirstOrDefault();
    }
}
