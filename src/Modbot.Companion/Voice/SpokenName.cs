using System.Globalization;
using System.Text;

namespace Modbot.Companion.Voice;

/// <summary>
/// Turns a display name as VRChat shows it into the words the voice says.
/// </summary>
/// <remarks>
/// VRChat names are full of decoration that reads fine on a screen and terribly out loud:
/// combining marks stacked on letters, invisible characters, look-alike alphabets. The real
/// name normaliser is being built elsewhere and will take this seam over; until then
/// <see cref="PlainSpokenName"/> does the light version.
/// </remarks>
public interface ISpokenName
{
    /// <summary>The words to say for a name. Never empty: a name nothing can be made of becomes "someone".</summary>
    string Spoken(string? name);
}

/// <summary>
/// The default: strip combining marks and invisible characters, collapse the spaces, and leave
/// everything else alone.
/// </summary>
/// <remarks>
/// <para>Deliberately light. Guessing at look-alike letters or stylised alphabets is the real
/// normaliser's job, and a wrong guess spoken out loud is worse than a name said plainly.</para>
/// <para>Only marks that stand on their own are removed. An accented letter written as one
/// character ("í") is a letter and stays; the companion runs in invariant globalization mode,
/// where decomposing it is not something every platform can do, and the voice says it fine.</para>
/// </remarks>
public sealed class PlainSpokenName : ISpokenName
{
    /// <summary>What is said when nothing speakable is left of a name, or there was no name.</summary>
    public const string Nobody = "someone";

    public string Spoken(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return Nobody;

        var builder = new StringBuilder(name.Length);
        var lastWasSpace = true;

        foreach (var rune in name.EnumerateRunes())
        {
            switch (Rune.GetUnicodeCategory(rune))
            {
                // Accents and decorations stacked on a letter; the letter itself stays.
                case UnicodeCategory.NonSpacingMark:
                case UnicodeCategory.EnclosingMark:
                // Zero-width joiners, direction marks, variation selectors and the like: they
                // change nothing a voice can say.
                case UnicodeCategory.Format:
                case UnicodeCategory.Control:
                case UnicodeCategory.PrivateUse:
                case UnicodeCategory.OtherNotAssigned:
                    continue;

                case UnicodeCategory.SpaceSeparator:
                case UnicodeCategory.LineSeparator:
                case UnicodeCategory.ParagraphSeparator:
                    if (!lastWasSpace)
                        builder.Append(' ');

                    lastWasSpace = true;
                    continue;

                default:
                    if (Rune.IsWhiteSpace(rune))
                    {
                        if (!lastWasSpace)
                            builder.Append(' ');

                        lastWasSpace = true;
                        continue;
                    }

                    builder.Append(rune.ToString());
                    lastWasSpace = false;
                    continue;
            }
        }

        var spoken = builder.ToString().Trim();
        return spoken.Length == 0 ? Nobody : spoken;
    }
}
