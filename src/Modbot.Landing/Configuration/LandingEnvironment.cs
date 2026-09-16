namespace Modbot.Landing.Configuration;

/// <summary>The environment variables Modbot.Landing reads, and nothing else.</summary>
/// <param name="Port">The port to listen on. Railway injects it.</param>
/// <param name="CloudUrl">
/// The Modbot Cloud the open rooms are read from. Unset means the rooms page says nothing is open.
/// </param>
/// <param name="CloudApiKey">
/// The key that opens Cloud's public rooms feed. It is used only by this server, on the server
/// side, and is never written into a page: the browser asks this site, and this site asks Cloud.
/// </param>
public sealed record LandingEnvironment(int Port, Uri? CloudUrl, string? CloudApiKey)
{
    public const string PortVariable = "PORT";
    public const string CloudUrlVariable = "MODBOT_CLOUD_PROXY_URL";
    public const string CloudApiKeyVariable = "MODBOT_CLOUD_API_KEY";
    public const int DefaultPort = 8080;

    /// <summary>True when the rooms page has somewhere to read from.</summary>
    public bool CanReadRooms => CloudUrl is not null && !string.IsNullOrEmpty(CloudApiKey);

    public static LandingEnvironment Read(Func<string, string?>? get = null)
    {
        get ??= Environment.GetEnvironmentVariable;

        var port = int.TryParse(get(PortVariable), out var parsed) && parsed is > 0 and <= 65535
            ? parsed
            : DefaultPort;

        // Anything that is not a full http or https address is no address at all. The rooms page
        // then shows nothing rather than this server trying to resolve a typo every minute.
        var cloud = Uri.TryCreate(get(CloudUrlVariable), UriKind.Absolute, out var url)
                    && url.Scheme is "http" or "https"
            ? url
            : null;

        var key = get(CloudApiKeyVariable);

        return new LandingEnvironment(port, cloud, string.IsNullOrWhiteSpace(key) ? null : key.Trim());
    }
}
