using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Modbot.Companion.Listening;

/// <summary>
/// One phrase the client listens for: the words a moderator says, and the pieces the engine
/// matches them by.
/// </summary>
/// <remarks>
/// <para>The engine does not match words. It matches the small pieces its own vocabulary is built
/// from, so "MODBOT CLIP THAT" has to be written out as those pieces before it can be listened
/// for. The pieces below were produced from the downloaded model's own vocabulary file, and
/// <c>PhraseModelTests</c> checks the shape of every one of them. Working them out on this PC
/// instead would mean shipping a second library to do it, which is why a moderator cannot type
/// their own phrase (listening design §5).</para>
/// </remarks>
/// <param name="Said">The words, as somebody would say them. Shown on the documentation page.</param>
/// <param name="Pieces">The same words as the pieces the engine matches, separated by spaces.</param>
public sealed record Phrase(string Said, string Pieces);

/// <summary>
/// The one model the client can download to listen for a phrase: where it comes from, what it must
/// hash to, what the folder holds once it is there, and the phrases it is told to listen for.
/// </summary>
/// <remarks>
/// <para><strong>What this reads.</strong> The companion's own <c>phrases</c> folder, to check
/// whether the model is present: four files by name, and one small JSON marker written when the
/// download was checked and unpacked. Nothing else on the disk, and never a microphone — opening
/// one is <c>PhraseListening.cs</c>, the one file in the client allowed to.</para>
/// <para><strong>What leaves the machine.</strong> Nothing from here; the download itself is
/// <see cref="PhraseDownload"/>.</para>
/// <para><strong>Why one pinned file.</strong> The same rule as the voice: a moderator should be
/// able to say exactly what the client fetched, so there is one address, one size, one SHA-256,
/// and a download that does not match all three is thrown away. The model is the sherpa-onnx
/// project's English phrase model — a 3.3M-parameter zipformer trained on GigaSpeech, Apache-2.0.
/// About 17 MB to download and about 20 MB on disk, against the voice's 305 MB, because it only
/// has to recognise a handful of phrases rather than every English sentence.</para>
/// <para><strong>Why the full-size files and not the 8-bit ones.</strong> The archive carries both.
/// The full-size encoder, decoder and joiner come to about 13 MB together, which is small enough
/// that the 8-bit copies buy nothing worth having — and the voice engine design (2026-09-18)
/// already measured that this engine has no fast 8-bit path and emulates one, so the 8-bit copies
/// would cost processor time rather than save it. The client is trying to stay out of the way of a
/// VR game; that is the number that matters here.</para>
/// </remarks>
/// <param name="Name">The folder the model lives in under <c>phrases</c>.</param>
/// <param name="Url">The one address it is fetched from.</param>
/// <param name="Sha256">What the downloaded file must hash to, lower-case hex.</param>
/// <param name="Size">How many bytes the download is, exactly.</param>
/// <param name="ArchiveFolder">The folder every entry in the archive sits under; stripped when unpacking.</param>
/// <param name="EncoderFile">The part that turns sound into what the other two read.</param>
/// <param name="DecoderFile">The part that keeps track of what has been heard so far.</param>
/// <param name="JoinerFile">The part that decides what the next piece was.</param>
/// <param name="TokensFile">The vocabulary the pieces are named in.</param>
/// <param name="SampleRate">How many samples a second the model expects.</param>
/// <param name="Phrases">Every phrase the client listens for.</param>
public sealed record PhraseModel(
    string Name,
    Uri Url,
    string Sha256,
    long Size,
    string ArchiveFolder,
    string EncoderFile,
    string DecoderFile,
    string JoinerFile,
    string TokensFile,
    int SampleRate,
    IReadOnlyList<Phrase> Phrases)
{
    /// <summary>The name of the marker written once a download has been checked and unpacked.</summary>
    public const string MarkerFile = "phrase.json";

    /// <summary>
    /// The file the phrases are written into, beside the model, so the engine can be handed them
    /// and so anybody suspicious can open it and read exactly what their PC is listening for.
    /// </summary>
    public const string PhrasesFile = "phrases.txt";

    /// <summary>
    /// The phrases, as a moderator says them and as the engine matches them.
    /// </summary>
    /// <remarks>
    /// <para>Four rather than one, because the engine hears sounds rather than spellings and
    /// "Modbot" is not a word it was trained on: it can come out as one run of pieces or as "mod"
    /// and "bot" separately, and a moderator saying "clip this" means exactly what a moderator
    /// saying "clip that" means. Four costs nothing — the engine is told all of them at once and
    /// matches whichever arrives.</para>
    /// <para>The leading <c>▁</c> in each piece is the engine's own mark for "a word starts
    /// here". It is written as an escape rather than as the character itself so that the meaning of
    /// this file cannot depend on how somebody's editor saved it.</para>
    /// </remarks>
    private static readonly Phrase[] ClipPhrases =
    [
        new("Modbot, clip that", "▁MO D B O T ▁C LI P ▁THAT"),
        new("Mod bot, clip that", "▁MO D ▁BO T ▁C LI P ▁THAT"),
        new("Modbot, clip this", "▁MO D B O T ▁C LI P ▁THIS"),
        new("Mod bot, clip this", "▁MO D ▁BO T ▁C LI P ▁THIS"),
    ];

    public static PhraseModel Default { get; } = new(
        "sherpa-onnx-kws-zipformer-gigaspeech-3.3M-2024-01-01",
        new Uri("https://github.com/k2-fsa/sherpa-onnx/releases/download/kws-models/sherpa-onnx-kws-zipformer-gigaspeech-3.3M-2024-01-01.tar.bz2"),
        "f170013b4716e41b62b9bfd809687c207cef798ef9bc6534d524e17af9b6561a",
        17_626_723,
        "sherpa-onnx-kws-zipformer-gigaspeech-3.3M-2024-01-01",
        "encoder-epoch-12-avg-2-chunk-16-left-64.onnx",
        "decoder-epoch-12-avg-2-chunk-16-left-64.onnx",
        "joiner-epoch-12-avg-2-chunk-16-left-64.onnx",
        "tokens.txt",
        16_000,
        ClipPhrases);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    /// <summary>Where the client keeps it: <c>%APPDATA%\Modbot\phrases</c>.</summary>
    public static string PhrasesFolder(string companionFolder) => Path.Combine(companionFolder, "phrases");

    public string Folder(string phrasesFolder) => Path.Combine(phrasesFolder, Name);

    public string EncoderPath(string phrasesFolder) => Path.Combine(Folder(phrasesFolder), EncoderFile);

    public string DecoderPath(string phrasesFolder) => Path.Combine(Folder(phrasesFolder), DecoderFile);

    public string JoinerPath(string phrasesFolder) => Path.Combine(Folder(phrasesFolder), JoinerFile);

    public string TokensPath(string phrasesFolder) => Path.Combine(Folder(phrasesFolder), TokensFile);

    public string PhrasesPath(string phrasesFolder) => Path.Combine(Folder(phrasesFolder), PhrasesFile);

    public string MarkerPath(string phrasesFolder) => Path.Combine(Folder(phrasesFolder), MarkerFile);

    private IReadOnlyList<string>? _spoken;

    /// <summary>
    /// The words a moderator would say, for the settings screen and for a log line.
    /// </summary>
    /// <remarks>
    /// Made once and then the same list every time. The window compares one snapshot with the last
    /// to decide whether to redraw, and a fresh list each time would say "something changed" once a
    /// second forever — which costs somebody the box they were typing in.
    /// </remarks>
    public IReadOnlyList<string> Spoken => _spoken ??= [.. Phrases.Select(p => p.Said)];

    /// <summary>
    /// The phrases file's text: one phrase a line, as the pieces the engine matches, and nothing
    /// else on the line.
    /// </summary>
    /// <remarks>
    /// The engine's own format allows a plain-English label after an <c>@</c>, which would have
    /// been nicer to read, but it takes one word rather than a sentence and anything it does not
    /// recognise ends the whole process rather than being ignored. Not worth it for a label. The
    /// English is here in this file, beside every line of pieces, where a person reading can see
    /// both.
    /// </remarks>
    public string PhrasesText()
    {
        var text = new StringBuilder();
        foreach (var phrase in Phrases)
            text.Append(phrase.Pieces).Append('\n');

        return text.ToString();
    }

    /// <summary>
    /// Whether this exact model is on disk: the four things the engine needs, the phrases file, and
    /// a marker saying they came from a download that hashed right. A half-unpacked folder is not
    /// present.
    /// </summary>
    public bool IsPresent(string phrasesFolder)
    {
        if (!File.Exists(EncoderPath(phrasesFolder))
            || !File.Exists(DecoderPath(phrasesFolder))
            || !File.Exists(JoinerPath(phrasesFolder))
            || !File.Exists(TokensPath(phrasesFolder))
            || !File.Exists(PhrasesPath(phrasesFolder)))
        {
            return false;
        }

        try
        {
            var marker = JsonSerializer.Deserialize<Marker>(File.ReadAllText(MarkerPath(phrasesFolder)), Json);
            return marker is not null
                && string.Equals(marker.Name, Name, StringComparison.Ordinal)
                && string.Equals(marker.Sha256, Sha256, StringComparison.OrdinalIgnoreCase)
                && string.Equals(marker.Phrases, PhrasesText(), StringComparison.Ordinal);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>The marker's text, written by <see cref="PhraseDownload"/> after a good unpack.</summary>
    public string MarkerText() => JsonSerializer.Serialize(new Marker(Name, Sha256, Url.ToString(), PhrasesText()), Json);

    private sealed record Marker(
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("sha256")] string Sha256,
        [property: JsonPropertyName("from")] string From,
        [property: JsonPropertyName("phrases")] string Phrases);
}
