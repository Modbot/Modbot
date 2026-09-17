using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Modbot.Companion.CloudBackup;
using Modbot.Companion.Pairing;

namespace Modbot.Companion.Presentation;

/// <summary>
/// What a moderator can change about the client itself: which page "Pair with a server" opens,
/// whether it checks for newer versions of itself, whether it starts with Windows, and where its
/// event backup to Modbot Cloud goes.
/// </summary>
/// <remarks>
/// <para><strong>What this reads and writes.</strong> One file, <c>settings.json</c>, in Modbot's own
/// folder under your user profile — beside <c>pairings.json</c>. It is plain JSON with optional fields:
/// <c>pairingPage</c>, <c>checkForUpdates</c>, <c>startWithWindows</c> and <c>cloud</c>
/// (<c>{ "endpoint": "…", "disabled": true }</c>). If it is missing or unreadable the defaults are
/// used. The client writes it only when a switch on the settings screen is changed, and then changes
/// only that switch's field, leaving anything else in the file as it was. The <c>cloud</c> object is
/// never written by the client; the environment variables <c>MODBOT_CLOUD_ENDPOINT</c> and
/// <c>MODBOT_CLOUD_DISABLED</c> are also read, and win over it (<see cref="CloudSettings"/>).</para>
/// <para><strong>Nothing here leaves the machine.</strong> The pairing page address is what the client
/// opens in your browser when you press the button; no server is told what it is. The switches decide
/// what the client does; they are not reported anywhere.</para>
/// <para><strong>Why it exists.</strong> The default page is the project's own, which forwards a
/// signed-in moderator to their group's server. A tester with only their own server points the
/// button at <c>https://their-server/pair</c> directly. The update switch is for a group whose policy
/// is to pin a version (M3 9.2). The event backup is on unless the person running the client turns it
/// off here or in the environment (cloud event backup spec 3.1).</para>
/// </remarks>
/// <param name="CheckForUpdates">
/// Whether an installed client asks the release feed for newer versions. On unless
/// <c>"checkForUpdates": false</c> is in the file.
/// </param>
/// <param name="StartWithWindows">
/// "Start Modbot Companion when my computer starts". On unless <c>"startWithWindows": false</c> is in the file,
/// and only acted on by an installed copy.
/// </param>
public sealed record CompanionSettings(Uri PairingPage, bool CheckForUpdates = true, bool StartWithWindows = true)
{
    /// <summary>
    /// Where the event backup goes, and whether it is sent: the environment, then the file's
    /// <c>cloud</c> object, then on to <c>https://cloud.modbot.co</c>.
    /// </summary>
    public CloudSettings Cloud { get; init; } = CloudSettings.Default;

    /// <summary>
    /// my.modbot.co's redirect route, pointed at <c>/pair</c>: it picks one of the moderator's saved
    /// servers and opens that server's own pairing page.
    /// </summary>
    public const string DefaultPairingPage = "https://my.modbot.co/go?redir=/pair";

    public const string StartWithWindowsField = "startWithWindows";

    public static CompanionSettings Default { get; } = new(new Uri(DefaultPairingPage));

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>The default location: <c>%APPDATA%\Modbot\settings.json</c>.</summary>
    public static string DefaultPath(string applicationData)
        => Path.Combine(applicationData, "Modbot", "settings.json");

    /// <summary>
    /// Reads the file and the two Cloud environment variables. A missing or unreadable file counts as
    /// empty. Never throws for a bad file: a typo in a settings file must not stop the client
    /// reporting.
    /// </summary>
    /// <param name="environment">Reads one environment variable. Null reads this process's own.</param>
    public static CompanionSettings Load(string path, Func<string, string?>? environment = null)
    {
        environment ??= Environment.GetEnvironmentVariable;

        var shape = ReadFile(path);

        return FromPairingPage(shape?.PairingPage) with
        {
            CheckForUpdates = shape?.CheckForUpdates ?? true,
            StartWithWindows = shape?.StartWithWindows ?? true,
            Cloud = CloudSettings.Resolve(shape?.Cloud?.Endpoint, shape?.Cloud?.Disabled, environment),
        };
    }

    private static FileShape? ReadFile(string path)
    {
        if (!File.Exists(path))
            return null;

        try
        {
            return JsonSerializer.Deserialize<FileShape>(File.ReadAllText(path), Json);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Writes one switch to the file, keeping every other field in it. Returns false, and changes
    /// nothing, when the file cannot be read as JSON or cannot be written: a hand-edited file with a
    /// typo is not overwritten.
    /// </summary>
    public static bool SaveSwitch(string path, string field, bool value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(field);

        try
        {
            JsonObject root;
            if (File.Exists(path))
            {
                if (JsonNode.Parse(File.ReadAllText(path)) is not JsonObject existing)
                    return false;

                root = existing;
            }
            else
            {
                root = [];
            }

            root[field] = value;

            if (Path.GetDirectoryName(path) is { Length: > 0 } directory)
                Directory.CreateDirectory(directory);

            File.WriteAllText(path, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
            return true;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Applies the same address rule as pairing itself: HTTPS, or plain HTTP only to this machine.
    /// A button that opened an insecure page would be handing the moderator's sign-in to whoever
    /// is on the network.
    /// </summary>
    public static CompanionSettings FromPairingPage(string? pairingPage)
    {
        if (string.IsNullOrWhiteSpace(pairingPage))
            return Default;

        return Uri.TryCreate(pairingPage.Trim(), UriKind.Absolute, out var page) && ServerAddresses.IsAllowed(page)
            ? new CompanionSettings(page)
            : Default;
    }

    private sealed record FileShape(
        [property: JsonPropertyName("pairingPage")] string? PairingPage,
        [property: JsonPropertyName("checkForUpdates")] bool? CheckForUpdates,
        [property: JsonPropertyName("startWithWindows")] bool? StartWithWindows,
        [property: JsonPropertyName("cloud")] CloudShape? Cloud);

    private sealed record CloudShape(
        [property: JsonPropertyName("endpoint")] string? Endpoint,
        [property: JsonPropertyName("disabled")] bool? Disabled);
}
