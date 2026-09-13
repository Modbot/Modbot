using System.Text.Json;
using System.Text.Json.Serialization;
using Modbot.Client.Pairing;

namespace Modbot.Client.Presentation;

/// <summary>
/// The one thing a moderator can change about the client itself: which page "Pair with a server"
/// opens.
/// </summary>
/// <remarks>
/// <para><strong>What this reads.</strong> One optional file, <c>settings.json</c>, in Modbot's own
/// folder under your user profile — beside <c>pairings.json</c>. It is plain JSON with one field,
/// <c>pairingPage</c>, and if it is missing, unreadable or names an address the client would not
/// talk to, the default is used and nothing is written. The client never creates this file; a
/// person who wants the override creates it.</para>
/// <para><strong>Nothing here leaves the machine.</strong> The address is what the client opens in
/// your browser when you press the button; it is not sent anywhere, and no server is told what it
/// is.</para>
/// <para><strong>Why it exists.</strong> The default is the project's own page, which forwards a
/// signed-in moderator to their group's server. A tester with only their own server, or a group
/// that would rather not go through the project's page at all, points the button at
/// <c>https://their-server/pair</c> directly.</para>
/// </remarks>
public sealed record ClientSettings(Uri PairingPage)
{
    /// <summary>The project's pairing page, which sends a signed-in moderator on to their own server's.</summary>
    public const string DefaultPairingPage = "https://my.modbot.co/pair";

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

        return FromPairingPage(shape?.PairingPage);
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

    private sealed record FileShape([property: JsonPropertyName("pairingPage")] string? PairingPage);
}
