using System.Text.Json;
using System.Text.Json.Serialization;

namespace Modbot.Companion.Voice;

/// <summary>
/// The one voice the companion can download: where it comes from, what it must hash to, and
/// what the folder holds once it is there.
/// </summary>
/// <remarks>
/// <para><strong>What this reads.</strong> The companion's own <c>voices</c> folder, to check
/// whether the voice is present: three files by name, and one small JSON marker written when the
/// download was checked and unpacked. Nothing else on the disk.</para>
/// <para><strong>What leaves the machine.</strong> Nothing from here; the download itself is
/// <see cref="VoiceDownload"/>.</para>
/// <para><strong>Why one pinned file.</strong> A moderator should be able to say exactly what the
/// companion fetched. So: one address, one size, one SHA-256, and a download that does not match
/// all three is thrown away. The voice is a Piper text-to-speech model — <c>en_US-kristin-medium</c>,
/// a US English voice trained from scratch on public-domain LibriVox recordings, MIT-licensed by
/// the Piper project — as packaged by the sherpa-onnx project with the espeak-ng data that turns
/// words into sounds (GPL-3.0-or-later). About 64 MB to download; about 80 MB on disk.</para>
/// </remarks>
/// <param name="Name">The folder the voice lives in under <c>voices</c>.</param>
/// <param name="Url">The one address it is fetched from.</param>
/// <param name="Sha256">What the downloaded file must hash to, lower-case hex.</param>
/// <param name="Size">How many bytes the download is, exactly.</param>
/// <param name="ArchiveFolder">The folder every entry in the archive sits under; stripped when unpacking.</param>
/// <param name="ModelFile">The model, inside the voice's folder.</param>
/// <param name="TokensFile">The token list beside it.</param>
/// <param name="DataFolder">The espeak-ng data folder beside it.</param>
/// <param name="SampleRate">How many samples a second the voice produces.</param>
public sealed record VoiceModel(
    string Name,
    Uri Url,
    string Sha256,
    long Size,
    string ArchiveFolder,
    string ModelFile,
    string TokensFile,
    string DataFolder,
    int SampleRate)
{
    /// <summary>The name of the marker written once a download has been checked and unpacked.</summary>
    public const string MarkerFile = "voice.json";

    public static VoiceModel Default { get; } = new(
        "en_US-kristin-medium",
        new Uri("https://github.com/k2-fsa/sherpa-onnx/releases/download/tts-models/vits-piper-en_US-kristin-medium.tar.bz2"),
        "c2206f572df2956c50b1ae3367eebce3853c663e890cba8048cd62b1e4dbe6c7",
        67_259_230,
        "vits-piper-en_US-kristin-medium",
        "en_US-kristin-medium.onnx",
        "tokens.txt",
        "espeak-ng-data",
        22_050);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    /// <summary>Where the companion keeps voices: <c>%APPDATA%\Modbot\voices</c>.</summary>
    public static string VoicesFolder(string companionFolder) => Path.Combine(companionFolder, "voices");

    public string Folder(string voicesFolder) => Path.Combine(voicesFolder, Name);

    public string ModelPath(string voicesFolder) => Path.Combine(Folder(voicesFolder), ModelFile);

    public string TokensPath(string voicesFolder) => Path.Combine(Folder(voicesFolder), TokensFile);

    public string DataPath(string voicesFolder) => Path.Combine(Folder(voicesFolder), DataFolder);

    public string MarkerPath(string voicesFolder) => Path.Combine(Folder(voicesFolder), MarkerFile);

    /// <summary>
    /// Whether this exact voice is on disk: the three things the engine needs, and a marker that
    /// says they came from a download that hashed right. A half-unpacked folder is not present.
    /// </summary>
    public bool IsPresent(string voicesFolder)
    {
        if (!File.Exists(ModelPath(voicesFolder)) || !File.Exists(TokensPath(voicesFolder)) || !Directory.Exists(DataPath(voicesFolder)))
            return false;

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

    /// <summary>The marker's text, written by <see cref="VoiceDownload"/> after a good unpack.</summary>
    public string MarkerText() => JsonSerializer.Serialize(new Marker(Name, Sha256, Url.ToString()), Json);

    private sealed record Marker(
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("sha256")] string Sha256,
        [property: JsonPropertyName("from")] string From);
}
