namespace Modbot.Landing.Configuration;

/// <summary>The environment variables Modbot.Landing reads, and nothing else.</summary>
/// <param name="Port">The port to listen on. Railway injects it.</param>
/// <param name="CloudUrl">
/// The Modbot Cloud the open instances are read from. Unset means the instances page says nothing is open.
/// </param>
/// <param name="CloudApiKey">
/// The key that opens Cloud's public instances feed. It is used only by this server, on the server
/// side, and is never written into a page: the browser asks this site, and this site asks Cloud.
/// </param>
/// <param name="MyUrl">
/// Where my.modbot.co is, from <c>MODBOT_MY_URL</c>. Every link on the page that points at the
/// selector is rewritten to it as the page is served, so a group running its own points them all
/// somewhere else with one variable.
/// </param>
/// <param name="DiscordUrl">
/// Where <c>/discord</c> sends people. Null when it is unset or not an http address, and then
/// <c>/discord</c> answers with the page that says there is no invite yet.
/// </param>
/// <param name="GithubUrl">Where <c>/github</c> sends people. Falls back to the project's own repository.</param>
public sealed record LandingEnvironment(
    int Port,
    Uri? CloudUrl,
    string? CloudApiKey,
    string MyUrl,
    string? DiscordUrl,
    string? GithubUrl)
{
    public const string PortVariable = "PORT";
    public const string CloudUrlVariable = "MODBOT_CLOUD_PROXY_URL";
    public const string CloudApiKeyVariable = "MODBOT_CLOUD_API_KEY";
    public const string MyUrlVariable = "MODBOT_MY_URL";
    public const string DiscordVariable = "MODBOT_DISCORD_URL";
    public const string GithubVariable = "MODBOT_GITHUB_URL";

    public const int DefaultPort = 8080;
    public const string DefaultGithubUrl = "https://github.com/Modbot/Modbot";

    /// <summary>The address the page is built with, and what a missing or unusable value means.</summary>
    public const string DefaultMyUrl = "https://my.modbot.co";

    /// <summary>True when the instances page has somewhere to read from.</summary>
    public bool CanReadInstances => CloudUrl is not null && !string.IsNullOrEmpty(CloudApiKey);

    public static LandingEnvironment Read(Func<string, string?>? get = null)
    {
        get ??= Environment.GetEnvironmentVariable;

        var port = int.TryParse(get(PortVariable), out var parsed) && parsed is > 0 and <= 65535
            ? parsed
            : DefaultPort;

        // Anything that is not a full http or https address is no address at all. The instances page
        // then shows nothing rather than this server trying to resolve a typo every minute.
        var cloud = Uri.TryCreate(get(CloudUrlVariable), UriKind.Absolute, out var url)
                    && url.Scheme is "http" or "https"
            ? url
            : null;

        var key = get(CloudApiKeyVariable);

        return new LandingEnvironment(
            port,
            cloud,
            string.IsNullOrWhiteSpace(key) ? null : key.Trim(),
            Address(get(MyUrlVariable)) ?? DefaultMyUrl,
            Address(get(DiscordVariable)),
            Address(get(GithubVariable)) ?? DefaultGithubUrl);
    }

    /// <summary>
    /// An absolute http or https address with no trailing slash, or null. A page on this site links
    /// it or sends people to it, so a typo, a bare word or a <c>javascript:</c> scheme is left out
    /// rather than handed to a visitor.
    /// </summary>
    private static string? Address(string? value) =>
        Uri.TryCreate(value?.Trim(), UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https"
            ? uri.GetLeftPart(UriPartial.Authority) + uri.AbsolutePath.TrimEnd('/')
            : null;
}
