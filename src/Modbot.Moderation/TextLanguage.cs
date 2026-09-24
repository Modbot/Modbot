using System.Collections.Concurrent;
using LanguageDetection;

namespace Modbot.Moderation;

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
/// Singleton: the profiles are built on the first question and detecting is thread-safe once they
/// are. They are also put down again when nobody has asked for a while, because the fifty-one
/// profiles are 36 MB of the heap — measured, and more than everything else Modbot holds at rest
/// put together. A Modbot on a small machine that moderates in bursts should not pay for them
/// through the hours between. Building them back costs about a tenth of a second, once, and the
/// answers are identical either way: nothing about what Modbot can detect changes here, only how
/// long the profiles stay in memory after the last question.
/// </para>
/// <para>
/// The quiet is counted in ticks of a timer rather than in clock time, which is why there is no
/// <c>IModbotClock</c> here: two ticks with no question between them is quiet, so the profiles go
/// somewhere between one and two <see cref="QuietPeriod"/>s after the last one.
/// </para>
/// </remarks>
public sealed class TextLanguage : IDisposable
{
    /// <summary>Text shorter than this is left unknown: the detector guesses at it.</summary>
    public const int ShortestText = 12;

    /// <summary>The most characters one detection reads. Past this the answer stops changing.</summary>
    public const int MostText = 2000;

    /// <summary>How long a stretch of nobody asking counts as quiet.</summary>
    public static readonly TimeSpan QuietPeriod = TimeSpan.FromMinutes(15);

    private readonly Lock _gate = new();

    private LanguageDetector? _detector;

    /// <summary>Watches for the quiet. Only exists while the profiles do.</summary>
    private Timer? _quietCheck;

    /// <summary>Whether anything has asked since the last tick.</summary>
    private bool _asked;

    /// <summary>Whether the profiles are in memory right now. For the tests.</summary>
    public bool Loaded
    {
        get { lock (_gate) return _detector is not null; }
    }

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
            // Held as a local, so a detector put down while this call is running stays alive
            // until the call that is using it finishes with it.
            var code = Detector().Detect(trimmed);
            return string.IsNullOrWhiteSpace(code) ? null : code.Trim().ToLowerInvariant();
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            // A detector that cannot read a string must not stop a message being checked.
            return null;
        }
    }

    /// <summary>Puts the profiles down now, whether or not it has been quiet. For the tests.</summary>
    public void PutDown()
    {
        lock (_gate)
        {
            _detector = null;
            _asked = false;
            _quietCheck?.Dispose();
            _quietCheck = null;
        }
    }

    public void Dispose() => PutDown();

    /// <summary>
    /// The profiles, built if they are not in memory. Building inside the lock is deliberate: two
    /// threads arriving together would otherwise build two sets and hold 72 MB between them.
    /// </summary>
    private LanguageDetector Detector()
    {
        lock (_gate)
        {
            _asked = true;

            if (_detector is { } ready)
                return ready;

            var built = new LanguageDetector();
            built.AddAllLanguages();
            _detector = built;
            _quietCheck ??= new Timer(DropIfQuiet, null, QuietPeriod, QuietPeriod);
            return built;
        }
    }

    private void DropIfQuiet(object? _)
    {
        lock (_gate)
        {
            if (_asked)
            {
                _asked = false;
                return;
            }

            _detector = null;
            _quietCheck?.Dispose();
            _quietCheck = null;
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
