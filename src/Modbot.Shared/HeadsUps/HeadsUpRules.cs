using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Modbot.Shared.HeadsUps;

/// <summary>
/// What a heads-up is about: a short note one moderator leaves for the other staff standing in
/// the same VRChat instance (heads-ups, 2026-10-03).
/// </summary>
public enum HeadsUpKind
{
    /// <summary>On the instance itself, for everybody here.</summary>
    Pin = 0,

    /// <summary>
    /// On one person. Outlasts them leaving: it stands until somebody clears it or the instance
    /// closes or empties, and their coming back raises a card.
    /// </summary>
    KeepAnEye = 1,

    /// <summary>Free text, on a person or on the instance. One on a person ends when they leave.</summary>
    Message = 2,

    /// <summary>Asking for help at a place, picked from <see cref="HeadsUpRules.Places"/>.</summary>
    AskForHelp = 3,
}

/// <summary>
/// The rules a heads-up is held to, kept where both the server and the companion read them so the
/// two cannot disagree about what may be sent.
/// </summary>
/// <remarks>
/// <para><strong>Plain text, short, and no links.</strong> A heads-up is read in a headset at a
/// glance, by people who did not choose to receive it. A link there is something nobody can open
/// and somebody might be tricked by, so it is refused rather than shown.</para>
/// <para><strong>The places are words, not positions.</strong> VRChat tells a client nothing about
/// where anybody stands, so a place is one of a fixed list of names and nothing more.</para>
/// </remarks>
public static class HeadsUpRules
{
    /// <summary>The longest a heads-up's text may be, in characters, after cleaning.</summary>
    public const int MaxTextLength = 140;

    /// <summary>The longest a person's name is kept, as the roster gave it.</summary>
    public const int MaxNameLength = 64;

    /// <summary>
    /// How many may stand in one instance at once. A handful is useful; past this the list on the
    /// panel would push the roster off it.
    /// </summary>
    public const int MostStanding = 20;

    /// <summary>The places Ask for help offers, in the order they are shown.</summary>
    public static IReadOnlyList<string> Places { get; } =
        ["Entrance", "Spawn", "Bar", "Stage", "Dance floor", "Back room", "Outside"];

    /// <summary>The kinds as sent on the wire.</summary>
    public static string Word(HeadsUpKind kind) => kind switch
    {
        HeadsUpKind.Pin => "pin",
        HeadsUpKind.KeepAnEye => "keep_an_eye",
        HeadsUpKind.AskForHelp => "ask_for_help",
        _ => "message",
    };

    /// <summary>A kind from its wire word, or null for one this build does not know.</summary>
    public static HeadsUpKind? Parse(string? word) => word switch
    {
        "pin" => HeadsUpKind.Pin,
        "keep_an_eye" => HeadsUpKind.KeepAnEye,
        "message" => HeadsUpKind.Message,
        "ask_for_help" => HeadsUpKind.AskForHelp,
        _ => null,
    };

    /// <summary>The kind as a moderator reads it.</summary>
    public static string Name(HeadsUpKind kind) => kind switch
    {
        HeadsUpKind.Pin => "Pin",
        HeadsUpKind.KeepAnEye => "Keep an eye",
        HeadsUpKind.AskForHelp => "Ask for help",
        _ => "Message",
    };

    /// <summary>Whether this kind is about a person: it needs one.</summary>
    public static bool NeedsPerson(HeadsUpKind kind) => kind is HeadsUpKind.KeepAnEye;

    /// <summary>Whether this kind may be about a person at all. A pin and a call for help are the instance's.</summary>
    public static bool TakesPerson(HeadsUpKind kind) => kind is HeadsUpKind.KeepAnEye or HeadsUpKind.Message;

    /// <summary>Whether this kind needs words.</summary>
    public static bool NeedsText(HeadsUpKind kind) => kind is HeadsUpKind.Pin or HeadsUpKind.Message;

    /// <summary>
    /// Text as it will be kept: control and formatting characters (bidi overrides, zero-width
    /// characters) taken out, runs of space made one, and the ends trimmed. Null when nothing is left.
    /// </summary>
    public static string? Clean(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        var kept = new StringBuilder(text.Length);
        var lastWasSpace = false;

        foreach (var c in text)
        {
            var category = char.GetUnicodeCategory(c);
            if (category is UnicodeCategory.Control or UnicodeCategory.Format)
            {
                // A newline or a tab is still a gap between words.
                if (char.IsWhiteSpace(c) && !lastWasSpace && kept.Length > 0)
                {
                    kept.Append(' ');
                    lastWasSpace = true;
                }

                continue;
            }

            if (char.IsWhiteSpace(c))
            {
                if (!lastWasSpace && kept.Length > 0)
                    kept.Append(' ');

                lastWasSpace = true;
                continue;
            }

            kept.Append(c);
            lastWasSpace = false;
        }

        var cleaned = kept.ToString().Trim();
        return cleaned.Length == 0 ? null : cleaned;
    }

    /// <summary>Whether the text holds anything that reads as a web address.</summary>
    public static bool HasLink(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        try
        {
            return Link.IsMatch(text);
        }
        catch (RegexMatchTimeoutException)
        {
            // Text built to make the check slow is refused, never let through and never a fault.
            return true;
        }
    }

    /// <summary>The place as listed, matched without regard to case, or null when it is not one.</summary>
    public static string? Place(string? place)
        => place is null ? null : Places.FirstOrDefault(p => string.Equals(p, place.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Why this heads-up cannot be placed, in words, or null when it can. The text and the place
    /// are the cleaned ones.
    /// </summary>
    public static string? Problem(HeadsUpKind kind, string? subjectId, string? text, string? place)
    {
        if (NeedsPerson(kind) && string.IsNullOrWhiteSpace(subjectId))
            return "Pick a person for this.";

        if (NeedsText(kind) && text is null)
            return "Write something.";

        if (text is { Length: > MaxTextLength })
            return $"Keep it to {MaxTextLength} characters.";

        if (text is not null && HasLink(text))
            return "Links are not allowed.";

        if (kind is HeadsUpKind.AskForHelp && Place(place) is null)
            return "Pick a place.";

        return null;
    }

    /// <summary>
    /// A scheme, a <c>www.</c>, or a name ending in a common web ending. Wide on purpose: a
    /// sentence that only looks like an address is reworded in a second, and an address that slips
    /// through is in front of every moderator in the instance.
    /// </summary>
    private static readonly Regex Link = new(
        @"://|\bwww\.|\b[\p{L}\p{N}-]+\.(?:com|net|org|gg|io|me|ly|co|xyz|app|dev|tv|link|to|be|us|uk|de|ru|cc|site|online|info|biz|gl|sh|chat|club|live|vip)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled,
        TimeSpan.FromMilliseconds(200));
}
