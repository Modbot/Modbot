using System.Collections.Concurrent;
using LanguageDetection;

namespace Modbot.AI.Moderation;

/// <summary>
/// What language a piece of text is in (AI moderation design §18).
/// </summary>
/// <remarks>
/// <para>
/// M8 §4.4: a rule's false positives cluster in text that is not English, and nobody can see that
/// unless the language is on the flag. So every flag records one, term list and AI topic alike,
/// and the Flags page and each rule's card break the dismissal rate down by it.
/// </para>
/// <para>
/// Worked out here rather than asked of the model: it costs nothing, it answers the same way for a
/// term list as for a topic, and a model asked to name a language will name one even when the text
/// is three words of nothing. Short text is exactly where it is wrong, so text under
/// <see cref="ShortestText"/> characters is left unknown rather than guessed at.
/// </para>
/// <para>
/// Singleton: the profiles are built once, and detecting is thread-safe once they are.
/// </para>
/// </remarks>
public sealed class TextLanguage
{
    /// <summary>Text shorter than this is left unknown: the detector guesses at it.</summary>
    public const int ShortestText = 12;

    /// <summary>The most characters one detection reads. Past this the answer stops changing.</summary>
    public const int MostText = 2000;

    private readonly Lazy<LanguageDetector> _detector = new(() =>
    {
        var detector = new LanguageDetector();
        detector.AddAllLanguages();
        return detector;
    });

    /// <summary>
    /// The text's language as an ISO 639-3 code, e.g. <c>eng</c> or <c>rus</c>, or null when the
    /// text is too short or the detector could not tell.
    /// </summary>
    public string? Of(string? text)
    {
        if (text is null)
            return null;

        var trimmed = text.Trim();
        if (trimmed.Length < ShortestText)
            return null;

        if (trimmed.Length > MostText)
            trimmed = trimmed[..MostText];

        try
        {
            var code = _detector.Value.Detect(trimmed);
            return string.IsNullOrWhiteSpace(code) ? null : code.Trim().ToLowerInvariant();
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            // A detector that cannot read a string must not stop a message being checked.
            return null;
        }
    }
}

/// <summary>The languages a flag can be marked with, in words a moderator reads.</summary>
/// <remarks>
/// Only the codes the detector can answer with are named. A code with no name here is shown as it
/// is, which is still better than nothing and needs no release to fix.
/// </remarks>
public static class LanguageNames
{
    /// <summary>What the Flags page filter and the rule cards call a flag with no language.</summary>
    public const string UnknownLabel = "Unknown";

    private static readonly ConcurrentDictionary<string, string> Names = new(StringComparer.OrdinalIgnoreCase)
    {
        ["afr"] = "Afrikaans",
        ["ara"] = "Arabic",
        ["ben"] = "Bengali",
        ["bul"] = "Bulgarian",
        ["ces"] = "Czech",
        ["dan"] = "Danish",
        ["deu"] = "German",
        ["ell"] = "Greek",
        ["eng"] = "English",
        ["est"] = "Estonian",
        ["fas"] = "Persian",
        ["fin"] = "Finnish",
        ["fra"] = "French",
        ["guj"] = "Gujarati",
        ["heb"] = "Hebrew",
        ["hin"] = "Hindi",
        ["hrv"] = "Croatian",
        ["hun"] = "Hungarian",
        ["ind"] = "Indonesian",
        ["ita"] = "Italian",
        ["jpn"] = "Japanese",
        ["kan"] = "Kannada",
        ["kor"] = "Korean",
        ["lav"] = "Latvian",
        ["lit"] = "Lithuanian",
        ["mal"] = "Malayalam",
        ["mar"] = "Marathi",
        ["mkd"] = "Macedonian",
        ["nep"] = "Nepali",
        ["nld"] = "Dutch",
        ["nor"] = "Norwegian",
        ["pan"] = "Punjabi",
        ["pol"] = "Polish",
        ["por"] = "Portuguese",
        ["ron"] = "Romanian",
        ["rus"] = "Russian",
        ["slk"] = "Slovak",
        ["slv"] = "Slovenian",
        ["som"] = "Somali",
        ["spa"] = "Spanish",
        ["sqi"] = "Albanian",
        ["swa"] = "Swahili",
        ["swe"] = "Swedish",
        ["tam"] = "Tamil",
        ["tel"] = "Telugu",
        ["tgl"] = "Tagalog",
        ["tha"] = "Thai",
        ["tur"] = "Turkish",
        ["twi"] = "Twi",
        ["ukr"] = "Ukrainian",
        ["urd"] = "Urdu",
        ["vie"] = "Vietnamese",
        ["zho"] = "Chinese",
    };

    /// <summary>"English", "Russian", or the code itself when it is not one Modbot names.</summary>
    public static string Label(string? code)
        => string.IsNullOrWhiteSpace(code)
            ? UnknownLabel
            : Names.GetValueOrDefault(code.Trim(), code.Trim());
}
