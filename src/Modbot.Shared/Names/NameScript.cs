namespace Modbot.Shared.Names;

/// <summary>
/// The writing system a letter belongs to, as coarsely as the normalizer needs it.
/// </summary>
/// <remarks>
/// A look-alike letter from another script is folded to the Latin letter it resembles only when
/// the word around it is not written in that script -- <c>Addеrаll</c> with a Cyrillic е and а is
/// wearing a font, <c>Принцесса</c> is Russian. Telling those apart needs no more than "which
/// script is each letter in", so this is a block lookup, not the Unicode Scripts property.
/// </remarks>
public enum NameScript
{
    /// <summary>Punctuation, symbols, digits and anything that belongs to no script.</summary>
    Common = 0,

    Latin,
    Greek,
    Cyrillic,
    Armenian,
    Hebrew,
    Arabic,
    Thai,
    Lao,
    Georgian,

    /// <summary>Han, kana, bopomofo and hangul together: Japanese names mix two of them in one word.</summary>
    Cjk,

    /// <summary>Every other living script: Indic, Ethiopic, Khmer, Tibetan, Cherokee and the rest.</summary>
    Other,

    /// <summary>
    /// Scripts that turn up in names only as ornament -- cuneiform, hieroglyphs, runes, Yi, the
    /// syllabaries -- and are dropped from the plain forms.
    /// </summary>
    Decoration,
}

public static class NameScripts
{
    /// <summary>The script of a code point, by block. Meaningful for letters and digits only.</summary>
    public static NameScript Of(int cp)
    {
        if (cp < 0x80)
            return cp is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') ? NameScript.Latin : NameScript.Common;

        return cp switch
        {
            0x00AA or 0x00BA => NameScript.Latin,
            0x00D7 or 0x00F7 => NameScript.Common,
            >= 0x00C0 and <= 0x024F => NameScript.Latin,
            >= 0x0250 and <= 0x02AF => NameScript.Latin,   // IPA
            >= 0x02B0 and <= 0x02FF => NameScript.Common,  // spacing modifier letters: ornament
            >= 0x0370 and <= 0x03FF => NameScript.Greek,
            >= 0x0400 and <= 0x052F => NameScript.Cyrillic,
            >= 0x0530 and <= 0x058F => NameScript.Armenian,
            >= 0x0590 and <= 0x05FF => NameScript.Hebrew,
            >= 0x0600 and <= 0x06FF => NameScript.Arabic,
            >= 0x0750 and <= 0x077F => NameScript.Arabic,
            >= 0x08A0 and <= 0x08FF => NameScript.Arabic,
            >= 0x0E00 and <= 0x0E7F => NameScript.Thai,
            >= 0x0E80 and <= 0x0EFF => NameScript.Lao,
            >= 0x10A0 and <= 0x10FF => NameScript.Georgian,
            >= 0x1100 and <= 0x11FF => NameScript.Cjk,     // hangul jamo
            >= 0x1400 and <= 0x167F => NameScript.Decoration, // Canadian syllabics
            >= 0x1680 and <= 0x169F => NameScript.Decoration, // Ogham
            >= 0x16A0 and <= 0x16FF => NameScript.Decoration, // Runic
            >= 0x18B0 and <= 0x18FF => NameScript.Decoration, // Canadian syllabics extended
            >= 0x1C80 and <= 0x1C8F => NameScript.Cyrillic,
            >= 0x1C90 and <= 0x1CBF => NameScript.Georgian,
            >= 0x1D00 and <= 0x1DBF => NameScript.Latin,   // phonetic extensions: small capitals
            >= 0x1E00 and <= 0x1EFF => NameScript.Latin,
            >= 0x1F00 and <= 0x1FFF => NameScript.Greek,
            0x2071 or 0x207F => NameScript.Latin,
            >= 0x2090 and <= 0x209C => NameScript.Latin,
            >= 0x2100 and <= 0x214F => NameScript.Latin,   // letterlike symbols
            >= 0x2460 and <= 0x24FF => NameScript.Latin,   // enclosed alphanumerics
            >= 0x2C00 and <= 0x2C5F => NameScript.Decoration, // Glagolitic
            >= 0x2C60 and <= 0x2C7F => NameScript.Latin,
            >= 0x2D00 and <= 0x2D2F => NameScript.Georgian,
            >= 0x2DE0 and <= 0x2DFF => NameScript.Cyrillic,
            >= 0x2E80 and <= 0x2FDF => NameScript.Cjk,
            >= 0x3005 and <= 0x3007 => NameScript.Cjk,
            >= 0x3021 and <= 0x3029 => NameScript.Cjk,
            >= 0x3038 and <= 0x303B => NameScript.Cjk,
            >= 0x3040 and <= 0x31FF => NameScript.Cjk,     // hiragana, katakana, bopomofo, compatibility jamo
            >= 0x3400 and <= 0x4DBF => NameScript.Cjk,
            >= 0x4E00 and <= 0x9FFF => NameScript.Cjk,
            >= 0xA000 and <= 0xA4CF => NameScript.Decoration, // Yi
            >= 0xA4D0 and <= 0xA4FF => NameScript.Decoration, // Lisu
            >= 0xA500 and <= 0xA63F => NameScript.Decoration, // Vai
            >= 0xA640 and <= 0xA69F => NameScript.Cyrillic,
            >= 0xA6A0 and <= 0xA6FF => NameScript.Decoration, // Bamum
            >= 0xA700 and <= 0xA71F => NameScript.Common,  // modifier tone letters: ornament
            >= 0xA720 and <= 0xA7FF => NameScript.Latin,
            >= 0xA960 and <= 0xA97F => NameScript.Cjk,
            >= 0xA980 and <= 0xA9DF => NameScript.Decoration, // Javanese
            >= 0xAA00 and <= 0xAA5F => NameScript.Decoration, // Cham
            >= 0xAA80 and <= 0xAADF => NameScript.Decoration, // Tai Viet
            >= 0xAB30 and <= 0xAB6F => NameScript.Latin,
            >= 0xAB70 and <= 0xABBF => NameScript.Other,   // Cherokee supplement
            >= 0xAC00 and <= 0xD7FF => NameScript.Cjk,     // hangul syllables and jamo extended
            >= 0xF900 and <= 0xFAFF => NameScript.Cjk,
            >= 0xFB00 and <= 0xFB06 => NameScript.Latin,
            >= 0xFB13 and <= 0xFB17 => NameScript.Armenian,
            >= 0xFB1D and <= 0xFB4F => NameScript.Hebrew,
            >= 0xFB50 and <= 0xFDFF => NameScript.Arabic,
            >= 0xFE70 and <= 0xFEFF => NameScript.Arabic,
            >= 0xFF21 and <= 0xFF3A => NameScript.Latin,
            >= 0xFF41 and <= 0xFF5A => NameScript.Latin,
            >= 0xFF66 and <= 0xFFDC => NameScript.Cjk,     // halfwidth katakana and hangul
            >= 0x1B000 and <= 0x1B16F => NameScript.Cjk,   // kana supplement
            >= 0x1D400 and <= 0x1D7FF => NameScript.Latin, // mathematical alphanumerics
            >= 0x1F100 and <= 0x1F1FF => NameScript.Latin, // enclosed alphanumeric supplement
            >= 0x10000 and <= 0x1FFFF => NameScript.Decoration,
            >= 0x20000 and <= 0x3FFFF => NameScript.Cjk,
            _ => NameScript.Other,
        };
    }
}
