using System.Text.Json;
using System.Text.Json.Serialization;

namespace Modbot.Companion.Voice;

/// <summary>One of the voices the downloaded file holds: what it is called, and which one it is.</summary>
/// <param name="Name">What the Settings screen calls it, and what <c>settings.json</c> remembers.</param>
/// <param name="Number">Which of the file's voices it is, counting from zero.</param>
public sealed record NamedVoice(string Name, int Number);

/// <summary>
/// The one voice the companion can download: where it comes from, what it must hash to, and
/// what the folder holds once it is there.
/// </summary>
/// <remarks>
/// <para><strong>What this reads.</strong> The companion's own <c>voices</c> folder, to check
/// whether the voice is present: four files by name, and one small JSON marker written when the
/// download was checked and unpacked. Nothing else on the disk.</para>
/// <para><strong>What leaves the machine.</strong> Nothing from here; the download itself is
/// <see cref="VoiceDownload"/>.</para>
/// <para><strong>Why one pinned file.</strong> A moderator should be able to say exactly what the
/// companion fetched. So: one address, one size, one SHA-256, and a download that does not match
/// all three is thrown away. The voice is Kokoro 82M — version 0.19, English, full size,
/// Apache-2.0 — as packaged by the sherpa-onnx project with the espeak-ng data that turns words
/// into sounds (GPL-3.0-or-later). About 305 MB to download; about 353 MB on disk.</para>
/// <para><strong>Why the big file and not the small one.</strong> The same release carries an
/// 8-bit copy at a third of the size. Measured on the engine the client actually uses, that copy
/// costs twice the processor time per sentence and takes twice as long before a line starts:
/// ONNX Runtime has no fast 8-bit path for this model and emulates one. The voice engine design
/// (2026-09-18) has the numbers.</para>
/// </remarks>
/// <param name="Name">The folder the voice lives in under <c>voices</c>.</param>
/// <param name="Url">The one address it is fetched from.</param>
/// <param name="Sha256">What the downloaded file must hash to, lower-case hex.</param>
/// <param name="Size">How many bytes the download is, exactly.</param>
/// <param name="ArchiveFolder">The folder every entry in the archive sits under; stripped when unpacking.</param>
/// <param name="ModelFile">The model, inside the voice's folder.</param>
/// <param name="VoicesFile">The voices that model can speak as, beside it.</param>
/// <param name="TokensFile">The token list beside it.</param>
/// <param name="DataFolder">The espeak-ng data folder beside it.</param>
/// <param name="SampleRate">How many samples a second the voice produces.</param>
/// <param name="Voices">Every voice in the file the Settings screen offers, in the order it shows them.</param>
public sealed record VoiceModel(
    string Name,
    Uri Url,
    string Sha256,
    long Size,
    string ArchiveFolder,
    string ModelFile,
    string VoicesFile,
    string TokensFile,
    string DataFolder,
    int SampleRate,
    IReadOnlyList<NamedVoice> Voices)
{
    /// <summary>The name of the marker written once a download has been checked and unpacked.</summary>
    public const string MarkerFile = "voice.json";

    /// <summary>
    /// The voice a fresh install speaks with, and the one an unknown name in the settings file
    /// falls back to.
    /// </summary>
    public const string DefaultName = "Bella";

    /// <summary>
    /// The ten people in Kokoro v0.19's file, by the number the engine knows each of them by.
    /// </summary>
    /// <remarks>
    /// The file holds eleven. Number 0 is a blend of two of the others rather than a person, and
    /// is left out: a list with "Default" sitting between "Bella" and "Nicole" reads as a mistake.
    /// </remarks>
    private static readonly NamedVoice[] KokoroVoices =
    [
        new("Bella", 1),
        new("Nicole", 2),
        new("Sarah", 3),
        new("Sky", 4),
        new("Adam", 5),
        new("Michael", 6),
        new("Emma", 7),
        new("Isabella", 8),
        new("George", 9),
        new("Lewis", 10),
    ];

    public static VoiceModel Default { get; } = new(
        "kokoro-en-v0_19",
        new Uri("https://github.com/k2-fsa/sherpa-onnx/releases/download/tts-models/kokoro-en-v0_19.tar.bz2"),
        "912804855a04745fa77a30be545b3f9a5d15c4d66db00b88cbcd4921df605ac7",
        319_625_534,
        "kokoro-en-v0_19",
        "model.onnx",
        "voices.bin",
        "tokens.txt",
        "espeak-ng-data",
        24_000,
        KokoroVoices);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    /// <summary>Where the companion keeps voices: <c>%APPDATA%\Modbot\voices</c>.</summary>
    public static string VoicesFolder(string companionFolder) => Path.Combine(companionFolder, "voices");

    public string Folder(string voicesFolder) => Path.Combine(voicesFolder, Name);

    public string ModelPath(string voicesFolder) => Path.Combine(Folder(voicesFolder), ModelFile);

    public string VoicesPath(string voicesFolder) => Path.Combine(Folder(voicesFolder), VoicesFile);

    public string TokensPath(string voicesFolder) => Path.Combine(Folder(voicesFolder), TokensFile);

    public string DataPath(string voicesFolder) => Path.Combine(Folder(voicesFolder), DataFolder);

    public string MarkerPath(string voicesFolder) => Path.Combine(Folder(voicesFolder), MarkerFile);

    /// <summary>
    /// Which of the file's voices a name means. A name this voice does not have — a hand-edited
    /// settings file, or a name from a later version — is the default one rather than a failure.
    /// </summary>
    public int Number(string? name)
    {
        foreach (var voice in Voices)
        {
            if (string.Equals(voice.Name, name, StringComparison.OrdinalIgnoreCase))
                return voice.Number;
        }

        foreach (var voice in Voices)
        {
            if (string.Equals(voice.Name, DefaultName, StringComparison.OrdinalIgnoreCase))
                return voice.Number;
        }

        return Voices.Count > 0 ? Voices[0].Number : 0;
    }

    /// <summary>
    /// Whether this exact voice is on disk: the four things the engine needs, and a marker that
    /// says they came from a download that hashed right. A half-unpacked folder is not present.
    /// </summary>
    public bool IsPresent(string voicesFolder)
    {
        if (!File.Exists(ModelPath(voicesFolder))
            || !File.Exists(VoicesPath(voicesFolder))
            || !File.Exists(TokensPath(voicesFolder))
            || !Directory.Exists(DataPath(voicesFolder)))
        {
            return false;
        }

        try
        {
            var marker = JsonSerializer.Deserialize<Marker>(File.ReadAllText(MarkerPath(voicesFolder)), Json);
            return marker is not null
                && string.Equals(marker.Name, Name, StringComparison.Ordinal)
                && string.Equals(marker.Sha256, Sha256, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Whether some other voice is sitting in the folder — an older one this client no longer
    /// speaks with. The Settings screen uses it to say it is replacing a voice rather than
    /// fetching a first one.
    /// </summary>
    public bool AnotherIsPresent(string voicesFolder)
    {
        try
        {
            foreach (var folder in Directory.EnumerateDirectories(voicesFolder))
            {
                if (!string.Equals(Path.GetFileName(folder), Name, StringComparison.OrdinalIgnoreCase)
                    && File.Exists(Path.Combine(folder, MarkerFile)))
                {
                    return true;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
        {
            return false;
        }

        return false;
    }

    /// <summary>The marker's text, written by <see cref="VoiceDownload"/> after a good unpack.</summary>
    public string MarkerText() => JsonSerializer.Serialize(new Marker(Name, Sha256, Url.ToString()), Json);

    private sealed record Marker(
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("sha256")] string Sha256,
        [property: JsonPropertyName("from")] string From);
}
