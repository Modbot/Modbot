using System.Text.Json;
using System.Text.Json.Serialization;

namespace Modbot.Companion.Dictation;

/// <summary>
/// The one speech-to-text model Dictate uses, pinned: where it comes from, how big it is, and the
/// SHA-256 it must have, so that a file that is not exactly this one is never loaded.
/// </summary>
/// <remarks>
/// <para><strong>Why this one.</strong> Moonshine tiny, English only, 8-bit. It is the smallest
/// model that writes ordinary speech out well enough to be worth sending, about 108 megabytes
/// downloaded, and it runs on the processor, so it needs no graphics card. It is made by Useful
/// Sensors under the MIT licence and packaged for the same engine (sherpa-onnx) that listens for a
/// phrase, so no second engine is added to the client.</para>
/// <para><strong>Where it lives.</strong> <c>%APPDATA%\Modbot\dictation</c>, downloaded once, the
/// first time somebody taps Dictate (<see cref="DictationDownload"/>). Nothing is fetched at any
/// other time.</para>
/// <para><strong>What this reads.</strong> Whether the model's files and its marker are on the
/// disk, and the marker's text. It writes nothing and sends nothing; the download and what it
/// writes are described in <see cref="DictationDownload"/>.</para>
/// </remarks>
/// <param name="Name">The model's folder name, here and inside the archive.</param>
/// <param name="Url">The one address it is downloaded from.</param>
/// <param name="Sha256">The archive's SHA-256, lower-case hex.</param>
/// <param name="Size">The archive's size in bytes.</param>
/// <param name="ArchiveFolder">The folder the files sit in inside the archive.</param>
/// <param name="SampleRate">The rate the model hears at, in hertz.</param>
public sealed record DictationModel(
    string Name,
    Uri Url,
    string Sha256,
    long Size,
    string ArchiveFolder,
    int SampleRate)
{
    /// <summary>The name of the marker written once a download has been checked and unpacked.</summary>
    public const string MarkerFile = "dictation.json";

    /// <summary>The part that turns sound into the numbers the encoder reads.</summary>
    public const string PreprocessorFile = "preprocess.onnx";

    public const string EncoderFile = "encode.int8.onnx";

    public const string UncachedDecoderFile = "uncached_decode.int8.onnx";

    public const string CachedDecoderFile = "cached_decode.int8.onnx";

    public const string TokensFile = "tokens.txt";

    /// <summary>The longest one recording may be, in seconds. It stops by itself at this.</summary>
    public const int MostSeconds = 60;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static DictationModel Default { get; } = new(
        "sherpa-onnx-moonshine-tiny-en-int8",
        new Uri("https://github.com/k2-fsa/sherpa-onnx/releases/download/asr-models/sherpa-onnx-moonshine-tiny-en-int8.tar.bz2"),
        "d5fe6ec4334fef36255b2a4010412cad4c007e33103fec62fb5d17cad88086f2",
        107_600_538,
        "sherpa-onnx-moonshine-tiny-en-int8",
        16_000);

    /// <summary>The files kept out of the archive, and nothing else.</summary>
    public static IReadOnlyList<string> Files { get; } =
        [PreprocessorFile, EncoderFile, UncachedDecoderFile, CachedDecoderFile, TokensFile];

    /// <summary>Where the client keeps it: <c>%APPDATA%\Modbot\dictation</c>.</summary>
    public static string DictationFolder(string companionFolder) => Path.Combine(companionFolder, "dictation");

    public string Folder(string dictationFolder) => Path.Combine(dictationFolder, Name);

    public string PathOf(string dictationFolder, string file) => Path.Combine(Folder(dictationFolder), file);

    /// <summary>How many samples a recording may hold before it is stopped.</summary>
    public int MostSamples => MostSeconds * SampleRate;

    /// <summary>
    /// Whether this exact model is on disk: every file, and a marker saying they came from a
    /// download that hashed right. A half-unpacked folder is not present.
    /// </summary>
    public bool IsPresent(string dictationFolder)
    {
        if (Files.Any(file => !File.Exists(PathOf(dictationFolder, file))))
            return false;

        try
        {
            var marker = JsonSerializer.Deserialize<Marker>(File.ReadAllText(PathOf(dictationFolder, MarkerFile)), Json);
            return marker is not null
                && string.Equals(marker.Name, Name, StringComparison.Ordinal)
                && string.Equals(marker.Sha256, Sha256, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>The marker's text, written by <see cref="DictationDownload"/> after a good unpack.</summary>
    public string MarkerText() => JsonSerializer.Serialize(new Marker(Name, Sha256, Url.ToString()), Json);

    private sealed record Marker(
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("sha256")] string Sha256,
        [property: JsonPropertyName("from")] string From);
}
