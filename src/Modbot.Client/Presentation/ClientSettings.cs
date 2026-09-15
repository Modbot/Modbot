using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Modbot.Client.Pairing;

namespace Modbot.Client.Presentation;

/// <summary>
/// What a moderator can change about the client itself: which page "Pair with a server" opens,
/// whether it checks for newer versions of itself, and whether it backs up VRChat's log to Modbot
/// Cloud.
/// </summary>
/// <remarks>
/// <para><strong>What this reads and writes.</strong> One file, <c>settings.json</c>, in Modbot's own
/// folder under your user profile — beside <c>pairings.json</c>. It is plain JSON with optional fields:
/// <c>pairingPage</c>, <c>checkForUpdates</c> and <c>sendLogsToCloud</c>. If it is missing or
/// unreadable the defaults are used. The client writes it only when a switch on the settings screen
/// is changed, and then changes only that switch's field, leaving anything else in the file as it
/// was.</para>
/// <para><strong>Nothing here leaves the machine.</strong> The pairing page address is what the client
/// opens in your browser when you press the button; no server is told what it is. The switches decide
/// what the client does; they are not reported anywhere.</para>
/// <para><strong>Why it exists.</strong> The default page is the project's own, which forwards a
/// signed-in moderator to their group's server. A tester with only their own server points the
/// button at <c>https://their-server/pair</c> directly. The update switch is for a group whose policy
/// is to pin a version (M3 9.2). The backup switch is on unless the moderator turns it off (cloud log
/// backup spec 1).</para>
/// </remarks>
/// <param name="CheckForUpdates">
/// Whether an installed client asks the release feed for newer versions. On unless
/// <c>"checkForUpdates": false</c> is in the file.
/// </param>
/// <param name="SendLogsToCloud">
/// "Send all logging to Modbot Cloud as backup". On unless <c>"sendLogsToCloud": false</c> is in the file.
/// </param>
public sealed record ClientSettings(Uri PairingPage, bool CheckForUpdates = true, bool SendLogsToCloud = true)
{
    /// <summary>
    /// my.modbot.co's redirect route, pointed at <c>/pair</c>: it picks one of the moderator's saved
    /// servers and opens that server's own pairing page.
    /// </summary>
    public const string DefaultPairingPage = "https://my.modbot.co/go?redir=/pair";

    public const string SendLogsToCloudField = "sendLogsToCloud";

    public static ClientSettings Default { get; } = new(new Uri(DefaultPairingPage));

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>The default location: <c>%APPDATA%\Modbot\settings.json</c>.</summary>
    public static string DefaultPath(string applicationData)
        => Path.Combine(applicationData, "Modbot", "settings.json");

    /// <summary>
    /// Reads the file, or returns <see cref="Default"/>. Never throws for a bad file: a typo in a
    /// settings file must not stop the client reporting.
    /// </summary>
    public static ClientSettings Load(string path)
    {
        if (!File.Exists(path))
            return Default;

        FileShape? shape;
        try
        {
            shape = JsonSerializer.Deserialize<FileShape>(File.ReadAllText(path), Json);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return Default;
        }

        return FromPairingPage(shape?.PairingPage) with
        {
            CheckForUpdates = shape?.CheckForUpdates ?? true,
            SendLogsToCloud = shape?.SendLogsToCloud ?? true,
        };
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
    public static ClientSettings FromPairingPage(string? pairingPage)
    {
        if (string.IsNullOrWhiteSpace(pairingPage))
            return Default;

        return Uri.TryCreate(pairingPage.Trim(), UriKind.Absolute, out var page) && ServerAddresses.IsAllowed(page)
            ? new ClientSettings(page)
            : Default;
    }

    private sealed record FileShape(
        [property: JsonPropertyName("pairingPage")] string? PairingPage,
        [property: JsonPropertyName("checkForUpdates")] bool? CheckForUpdates,
        [property: JsonPropertyName("sendLogsToCloud")] bool? SendLogsToCloud);
}
