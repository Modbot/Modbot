using System.Text.Json;
using System.Text.Json.Serialization;
using Modbot.Client.Pairing;

namespace Modbot.Client.Presentation;

/// <summary>
/// The two things a moderator can change about the client itself: which page "Pair with a server"
/// opens, and whether it checks for newer versions of itself.
/// </summary>
/// <remarks>
/// <para><strong>What this reads.</strong> One optional file, <c>settings.json</c>, in Modbot's own
/// folder under your user profile — beside <c>pairings.json</c>. It is plain JSON with two optional
/// fields, <c>pairingPage</c> and <c>checkForUpdates</c>, and if it is missing, unreadable or names
/// an address the client would not talk to, the defaults are used and nothing is written. The
/// client never creates this file; a person who wants an override creates it.</para>
/// <para><strong>Nothing here leaves the machine.</strong> The address is what the client opens in
/// your browser when you press the button; it is not sent anywhere, and no server is told what it
/// is.</para>
/// <para><strong>Why it exists.</strong> The default page is the project's own, which forwards a
/// signed-in moderator to their group's server. A tester with only their own server, or a group
/// that would rather not go through the project's page at all, points the button at
/// <c>https://their-server/pair</c> directly. The update switch is for a group whose policy is to
/// pin a version and never have software call out for new versions on its own (M3 9.2).</para>
/// </remarks>
/// <param name="CheckForUpdates">
/// Whether an installed client asks the release feed for newer versions. On unless
/// <c>"checkForUpdates": false</c> is in the file.
/// </param>
public sealed record ClientSettings(Uri PairingPage, bool CheckForUpdates = true)
{
    /// <summary>
    /// my.modbot.co's redirect route, pointed at <c>/pair</c>: it picks one of the moderator's saved
    /// servers and opens that server's own pairing page.
    /// </summary>
    public const string DefaultPairingPage = "https://my.modbot.co/go?redir=/pair";

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

        return FromPairingPage(shape?.PairingPage) with { CheckForUpdates = shape?.CheckForUpdates ?? true };
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
        [property: JsonPropertyName("checkForUpdates")] bool? CheckForUpdates);
}
